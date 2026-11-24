using Godot;
using Drycastle.Shooter.Audio;
using Drycastle.Shooter.Thieves;
using Drycastle.Shooter.Vehicles;
using Drycastle.Shooter.Weapons;
using Drycastle.ShooterCore.Items;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Thieves;

namespace Drycastle.Shooter.Net.Client.Incidents;

/// <summary>
/// The Thieves event on the client (docs/events/thieves/): the Gang members, world entities of type <see cref="GangRules.TypeId"/>, and the Raid vans (<see cref="RaidVanRules.TypeId"/>). A member is a
/// <see cref="GangMemberView"/> (the look number in the state's owner field picks the body, the hp byte says the kind), placed from the interpolated state and shown by its state byte. A van is a
/// <see cref="RaidVanView"/> (door and damage from its state, hp and owner fields) with a solid box for the player's own movement while it stands (the one the server builds, so the prediction
/// agrees). The server owns both; the client only draws them, the van's honk, the Thieves' voices and songs, the shocked Thief and the satellite dishes taken. The Loot is <see cref="Loot"/>.
/// </summary>
public sealed class ThievesClient
{
    /// <summary>The sound a honk plays: no horn exists yet, so the closest the game has (a short low buzz of the UI sounds; ticket 09 replaces it). It is not positional by itself, so <see cref="OnFx"/> keeps it to its range.</summary>
    const string HonkSound = "helper.warning";
    /// <summary>How far from the man (flat) a pole's wire seats still take part: the man stands beside the column, the arms reach about two metres.</summary>
    const float ShockPoleM = 3f;

    sealed class GangNode
    {
        public GangMemberView View = null!;
        public Vector3 Last;
        public bool Placed;
        public float Speed;   // metres per second, smoothed: whether it is walking
    }

    sealed class VanNode
    {
        public RaidVanView View = null!;
        public StaticBody3D? Solid;
        public Vector3 Last;
        public bool Placed;
        public RaidVanState Shown;
        public (int X, int Y, int Z, ushort Yaw, bool Solid)? SolidAt;
    }

    readonly ClientContext _ctx;
    readonly ThievesFile _data;
    readonly ItemsFile _items;
    readonly Dictionary<ushort, GangNode> _gang = new();
    readonly Dictionary<ushort, VanNode> _vans = new();
    /// <summary>The object ids of the dishes taken, as the server last said.</summary>
    readonly HashSet<string> _dishesTaken = new();
    ThiefAudio? _audio;

    public LootClient Loot { get; }

    public ThievesClient(ClientContext ctx, ThievesFile data, ItemsFile items)
    {
        _ctx = ctx;
        _data = data;
        _items = items;
        GangRules.Register(WorldEntityRegistry.Shared);
        RaidVanRules.Register(WorldEntityRegistry.Shared);
        Loot = new LootClient(ctx, data);
        ctx.WorldEntities.Spawned += OnSpawned;
        ctx.WorldEntities.Despawned += OnDespawned;
    }

    /// <summary>The Gang members' bodies, for the bounding-volume overlay.</summary>
    public IEnumerable<GangMemberView> GangViews => _gang.Values.Select(g => g.View);

    /// <summary>The Thieves' sound (made on first use).</summary>
    ThiefAudio Audio
    {
        get
        {
            if (_audio != null) return _audio;
            _audio = new ThiefAudio(_data.Audio);
            _ctx.Owned.AddChild(_audio);
            return _audio;
        }
    }

    void OnSpawned(WorldReplica.Entity e)
    {
        if (e.Type == RaidVanRules.TypeId)
        {
            var van = RaidVanView.Make(_data.Van);
            van.Name = $"RaidVan{e.Id}";
            _ctx.Owned.AddChild(van);
            var shown = RaidVanRules.StateOf(e.State.State);
            _vans[e.Id] = new VanNode { View = van, Shown = shown };
            if (shown != RaidVanState.Destroyed) StartVanSong(van, RaidVanRules.SongOf(e.State.Owner));
            return;
        }
        if (e.Type != GangRules.TypeId) return;
        var view = GangMemberView.Make(_data, GangRules.KindOf(e.State.Hp), e.State.Owner, _items.Find(_data.Reward.ChainItem)?.Model ?? "");
        view.Name = $"GangMember{e.Id}";
        _ctx.Owned.AddChild(view);
        _gang[e.Id] = new GangNode { View = view };
    }

    void OnDespawned(ushort id)
    {
        if (_gang.Remove(id, out var g)) g.View.QueueFree();
        if (_vans.Remove(id, out var v)) { if (_audio != null) _audio.StopVanSong(v.View); v.View.QueueFree(); v.Solid?.QueueFree(); }
    }

    /// <summary>Places the Gang members (interpolated, a little in the past like every remote body) and shows the state they are in.</summary>
    public void Render(double delta)
    {
        RenderVans();
        Loot.Render();
        if (_gang.Count == 0 || _ctx.LastTick == 0) return;
        var rt = _ctx.RenderTick;
        foreach (var (id, g) in _gang)
        {
            if (!_ctx.WorldEntities.Entities.TryGetValue(id, out var e)) continue;
            if (e.Buf.At(rt) is { } s)
            {
                var p = new Vector3(s.Pos.X, s.Pos.Y, s.Pos.Z);
                g.View.GlobalPosition = p;
                g.View.Rotation = new Vector3(0, s.Yaw, 0);
                if (g.Placed && delta > 0) g.Speed = Mathf.Lerp(g.Speed, (p - g.Last).Length() / (float)delta, 0.2f);
                g.Last = p; g.Placed = true;
            }
            g.View.Show(GangRules.StateOf(e.State.State), g.Speed > 0.35f);
            g.View.SetCarried(e.State.Hp); // the Loot in its hands
            UpdateSpeaker(id, g, e.State.Hp); // the speaker on it and its song (ticket 09)
        }
    }

    /// <summary>The nearest living Gang member to <paramref name="from"/>, or null: the damage marker of a blow points at it (the hit event names a member by its hit id, which a client does not know).</summary>
    public Vector3? NearestGangMember(Vector3 from)
    {
        Vector3? best = null;
        var bestD = float.MaxValue;
        foreach (var (id, g) in _gang)
        {
            if (!_ctx.WorldEntities.Entities.TryGetValue(id, out var e) || !GangRules.AliveOf(e.State.Hp)) continue;
            var d = g.View.GlobalPosition.DistanceSquaredTo(from);
            if (d < bestD) { bestD = d; best = g.View.GlobalPosition; }
        }
        return best;
    }

    /// <summary>The nearest Raid van to <paramref name="from"/>, or null: the damage marker of a run-over points at it (the hit event names the van by its hit id, which a client does not know).</summary>
    public Vector3? NearestVan(Vector3 from)
    {
        Vector3? best = null;
        var bestD = float.MaxValue;
        foreach (var v in _vans.Values)
        {
            var d = v.View.GlobalPosition.DistanceSquaredTo(from);
            if (d < bestD) { bestD = d; best = v.View.GlobalPosition; }
        }
        return best;
    }

    // ---- Raid vans ----

    /// <summary>Places the Raid vans (interpolated like every remote body), shows the door, the damage and the wreck, turns the wheels, and keeps the solid box where the van stands.</summary>
    void RenderVans()
    {
        if (_vans.Count == 0 || _ctx.LastTick == 0) return;
        var rt = _ctx.RenderTick;
        foreach (var (id, v) in _vans)
        {
            if (!_ctx.WorldEntities.Entities.TryGetValue(id, out var e)) continue;
            var state = RaidVanRules.StateOf(e.State.State);
            if (e.Buf.At(rt) is { } s)
            {
                var p = new Vector3(s.Pos.X, s.Pos.Y, s.Pos.Z);
                v.View.GlobalPosition = p;
                v.View.Rotation = new Vector3(e.State.PitchRad, s.Yaw, 0);
                if (v.Placed) v.View.Roll((p - v.Last).Length() * (state == RaidVanState.Destroyed ? 0f : 1f));
                v.Last = p; v.Placed = true;
            }
            v.View.Apply(state, RaidVanRules.DoorOf(e.State.Owner), RaidVanRules.LeftOf(e.State.Owner), RaidVanRules.StageOf(e.State.Hp));
            if (state == RaidVanState.Destroyed && v.Shown != RaidVanState.Destroyed)
            {
                if (_audio != null) _audio.StopVanSong(v.View); // the music dies with the van
                if (ShotFx.Enabled) ExplodeVan(v.View.GlobalPosition);
            }
            var near = _ctx.Root.GetViewport().GetCamera3D() is { } cam && cam.GlobalPosition.DistanceTo(v.View.GlobalPosition) <= _data.Van.View.SeatDrawM;
            v.View.SetCrew(_data, RaidVanRules.InsideOf(e.State.Owner), near, id);
            v.Shown = state;
            PlaceVanSolid(v, e.State, RaidVanRules.IsSolid(state));
        }
    }

    /// <summary>The van was destroyed in front of us: the vehicle blast at its middle (the wreck stays).</summary>
    void ExplodeVan(Vector3 at)
    {
        var radius = _ctx.Vehicles?.Explosion.Splash.Radius ?? 9f;
        at.Y += 1.0f;
        VehicleFx.Explode(_ctx.Owned, at, radius);
        if (Sfx.Enabled) Sfx.Play3D("explosion.vehicle", at);
        _ctx.Shake.At(at, radius);
    }

    /// <summary>The box a van that stands is solid with, built from its last state as the server builds it (rounded as the record is), so the predicted move meets what the server's does.</summary>
    void PlaceVanSolid(VanNode v, WorldState s, bool solid)
    {
        var vc = _data.Van;
        var at = (s.X, s.Y, s.Z, s.Yaw, solid);
        if (v.Solid != null && v.SolidAt == at) return;
        v.Solid ??= VehicleCollider.Make(_ctx.Owned, vc.HalfWidthM, vc.HalfLengthM, vc.HeightM);
        v.SolidAt = at;
        VehicleCollider.Place(v.Solid, new Vector3(Quant.Pos(s.X), Quant.Pos(s.Y), Quant.Pos(s.Z)), Quant.Yaw(s.Yaw), solid);
    }

    // ---- events ----

    /// <summary>A Gang member says a line: it is played at the member (the entity id names it), if its body is here.</summary>
    public void OnVoiceEvent(EventKind kind, byte[] payload)
    {
        if (kind != EventKind.ThiefVoice || !GangVoiceCodec.TryDecode(payload, out var id, out var line)) return;
        if (_gang.TryGetValue(id, out var g)) Audio.PlayVoice(g.View, line);
    }

    /// <summary>The Thieves event's one-shot effects and the dishes taken. Called for every event the client receives.</summary>
    public void OnEvent(EventKind kind, byte[] payload)
    {
        if (kind == EventKind.ThievesFx && ThievesFxCodec.Decode(payload) is { } m) OnFx(m);
        OnDishEvent(kind, payload); // the dishes taken (ticket 06)
    }

    void OnFx(ThievesFxMsg m)
    {
        var at = new Vector3(m.Position.X, m.Position.Y, m.Position.Z);
        switch (m.Kind)
        {
            case ThievesFxKind.Honk:
            {
                var d = Sfx.ListenerDistance(at);
                var view = _data.Van.View;
                if (d <= view.HonkHearM) Sfx.Play3D(HonkSound, at, view.HonkDb - view.HonkFadeDb * d / view.HonkHearM); // the stand-in sound has no range of its own: it fades with the distance here
                break;
            }
            case ThievesFxKind.Shock:
                OnShocked(m.Id, at);
                break;
        }
    }

    /// <summary>A Thief electrocuted at the top of a pole (docs/events/thieves/07): the beams run from the man to the wire seats of the pole beside him for the shock's seconds (<see cref="ShockFx"/>).</summary>
    void OnShocked(ushort memberId, Vector3 at)
    {
        var seats = _ctx.Map == null ? new List<System.Numerics.Vector3>() : _ctx.Map.WireSeatsNear(new System.Numerics.Vector3(at.X, at.Y, at.Z), ShockPoleM);
        var wires = seats.Select(s => new Vector3(s.X, s.Y, s.Z)).ToArray();
        var view = _gang.TryGetValue(memberId, out var g) ? g.View : null;
        ShockFx.Play(_ctx.Owned, () => (view != null && GodotObject.IsInstanceValid(view) ? view.GlobalPosition : at) + Vector3.Up * 1.1f, wires, _data.Loot.Connector.ShockSeconds);
    }

    // ---- dishes ----

    void OnDishEvent(EventKind kind, byte[] payload)
    {
        if (kind != EventKind.DishTaken || DishCodec.Decode(payload) is not { } ids) return;
        _dishesTaken.Clear();
        foreach (var id in ids) _dishesTaken.Add(id);
        ApplyDishes();
    }

    /// <summary>Hides the taken dishes' nodes and shows the others (every placed object of the dish asset).</summary>
    void ApplyDishes()
    {
        if (_ctx.Map == null) return;
        foreach (var (id, node) in _ctx.Map.ObjectNodes)
        {
            if (_ctx.Map.ObjectAsset(id) != DishSource.AssetId || !GodotObject.IsInstanceValid(node)) continue;
            node.Visible = !_dishesTaken.Contains(id);
        }
    }

    // ---- sound ----

    /// <summary>
    /// The song of a Raid van (the number the server picked at its birth, in the van's owner field): looped at the van to <c>audio.van_range_m</c> from the server's clock (seconds of ticks) modulo the
    /// song's length, so every client, a joiner too, hears the same bar. Silence without songs (or on a client with no sound).
    /// </summary>
    void StartVanSong(Node3D van, int song)
    {
        if (!Sfx.Enabled) return;
        Audio.PlayVanSong(van, song, _ctx.LastTick / (double)Wire.TickRate);
    }

    /// <summary>Every frame per member: the speaker on it (hp byte bit 6) is drawn with its song, or taken away when the flag is gone.</summary>
    void UpdateSpeaker(ushort id, GangNode g, byte hp)
    {
        var on = GangRules.SpeakerOf(hp) && GangRules.AliveOf(hp);
        if (on || _audio != null) Audio.SetSpeaker(id, g.View, on);
    }
}
