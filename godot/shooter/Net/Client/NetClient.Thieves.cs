using Godot;
using Drycastle.Shooter.Audio;
using Drycastle.Shooter.Thieves;
using Drycastle.Shooter.Vehicles;
using Drycastle.Shooter.Weapons;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Thieves;

namespace Drycastle.Shooter.Net.Client;

/// <summary>
/// The Thieves event on the client (docs/events/thieves/): the Gang members, world entities of type <see cref="GangRules.TypeId"/>, and the Raid vans (<see cref="RaidVanRules.TypeId"/>). A member is a
/// <see cref="GangMemberView"/> (the look number in the state's owner field picks the body, the hp byte says the kind), placed from the interpolated state and shown by its state byte. A van is a
/// <see cref="RaidVanView"/> (door and damage from its state, hp and owner fields) with a solid box for the player's own movement while it stands (the one the server builds, so the prediction
/// agrees). The server owns both; the client only draws them and the van's honk.
/// </summary>
public partial class NetClient
{
    /// <summary>thieves.json: set by the host (the looks and clips of the Gang members).</summary>
    public ThievesFile ThievesData = new();

    sealed class GangNode
    {
        public GangMemberView View = null!;
        public Vector3 Last;
        public bool Placed;
        public float Speed;   // metres per second, smoothed: whether it is walking
    }

    readonly Dictionary<ushort, GangNode> _gang = new();

    sealed class VanNode
    {
        public RaidVanView View = null!;
        public StaticBody3D? Solid;
        public Vector3 Last;
        public bool Placed;
        public RaidVanState Shown;
        public (int X, int Y, int Z, ushort Yaw, bool Solid)? SolidAt;
    }

    readonly Dictionary<ushort, VanNode> _vans = new();

    /// <summary>The sound a honk plays: no horn exists yet, so the closest the game has (a short low buzz of the UI sounds; ticket 09 replaces it). It is not positional by itself, so <see cref="OnThievesFx"/> keeps it to its range.</summary>
    const string HonkSound = "helper.warning";

    /// <summary>Called once from <see cref="_Ready"/>: registers the Gang member type and follows its entities.</summary>
    void InitThieves()
    {
        GangRules.Register(WorldEntityRegistry.Shared);
        RaidVanRules.Register(WorldEntityRegistry.Shared);
        InitLootView();
        WorldEntities.Spawned += OnGangSpawned;
        WorldEntities.Despawned += OnGangDespawned;
    }

    void OnGangSpawned(WorldReplica.Entity e)
    {
        if (e.Type == RaidVanRules.TypeId)
        {
            var van = RaidVanView.Make(ThievesData.Van);
            van.Name = $"RaidVan{e.Id}";
            Owned.AddChild(van);
            var shown = RaidVanRules.StateOf(e.State.State);
            _vans[e.Id] = new VanNode { View = van, Shown = shown };
            if (shown != RaidVanState.Destroyed) StartVanSong(van, RaidVanRules.SongOf(e.State.Owner));
            return;
        }
        if (e.Type != GangRules.TypeId) return;
        var view = GangMemberView.Make(ThievesData, GangRules.KindOf(e.State.Hp), e.State.Owner, ItemData.Find(ThievesData.Reward.ChainItem)?.Model ?? "");
        view.Name = $"GangMember{e.Id}";
        Owned.AddChild(view);
        _gang[e.Id] = new GangNode { View = view };
    }

    void OnGangDespawned(ushort id)
    {
        if (_gang.Remove(id, out var g)) g.View.QueueFree();
        if (_vans.Remove(id, out var v)) { if (_thiefAudio != null) _thiefAudio.StopVanSong(v.View); v.View.QueueFree(); v.Solid?.QueueFree(); }
    }

    /// <summary>Places the Gang members (interpolated, a little in the past like every remote body) and shows the state they are in.</summary>
    void RenderGang(double delta)
    {
        RenderVans(delta);
        RenderLoot();
        if (_gang.Count == 0 || _lastTick == 0) return;
        var rt = RenderTickNow();
        foreach (var (id, g) in _gang)
        {
            if (!WorldEntities.Entities.TryGetValue(id, out var e)) continue;
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
            UpdateGangSpeaker(id, g, e.State.Hp); // the speaker on it and its song (ticket 09)
        }
    }

    /// <summary>The nearest living Gang member to <paramref name="from"/>, or null: the damage marker of a blow points at it (the hit event names a member by its hit id, which a client does not know).</summary>
    Vector3? NearestGangMember(Vector3 from)
    {
        Vector3? best = null;
        var bestD = float.MaxValue;
        foreach (var (id, g) in _gang)
        {
            if (!WorldEntities.Entities.TryGetValue(id, out var e) || !GangRules.AliveOf(e.State.Hp)) continue;
            var d = g.View.GlobalPosition.DistanceSquaredTo(from);
            if (d < bestD) { bestD = d; best = g.View.GlobalPosition; }
        }
        return best;
    }

    // ---- Raid vans ----

    /// <summary>Places the Raid vans (interpolated like every remote body), shows the door, the damage and the wreck, turns the wheels, and keeps the solid box where the van stands.</summary>
    void RenderVans(double delta)
    {
        if (_vans.Count == 0 || _lastTick == 0) return;
        var rt = RenderTickNow();
        foreach (var (id, v) in _vans)
        {
            if (!WorldEntities.Entities.TryGetValue(id, out var e)) continue;
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
                if (_thiefAudio != null) _thiefAudio.StopVanSong(v.View); // the music dies with the van
                if (ShotFx.Enabled) ExplodeVan(v.View.GlobalPosition);
            }
            var near = GetViewport().GetCamera3D() is { } cam && cam.GlobalPosition.DistanceTo(v.View.GlobalPosition) <= ThievesData.Van.View.SeatDrawM;
            v.View.SetCrew(ThievesData, RaidVanRules.InsideOf(e.State.Owner), near, id);
            v.Shown = state;
            PlaceVanSolid(v, e.State, RaidVanRules.IsSolid(state));
        }
    }

    /// <summary>The van was destroyed in front of us: the vehicle blast at its middle (the wreck stays).</summary>
    void ExplodeVan(Vector3 at)
    {
        var radius = Vehicles?.Explosion.Splash.Radius ?? 9f;
        at.Y += 1.0f;
        VehicleFx.Explode(Owned, at, radius);
        if (Sfx.Enabled) Sfx.Play3D("explosion.vehicle", at);
        Ctx.Shake.At(at, radius);
    }

    /// <summary>The box a van that stands is solid with, built from its last state as the server builds it (rounded as the record is), so the predicted move meets what the server's does.</summary>
    void PlaceVanSolid(VanNode v, WorldState s, bool solid)
    {
        var vc = ThievesData.Van;
        var at = (s.X, s.Y, s.Z, s.Yaw, solid);
        if (v.Solid != null && v.SolidAt == at) return;
        v.Solid ??= VehicleCollider.Make(Owned, vc.HalfWidthM, vc.HalfLengthM, vc.HeightM);
        v.SolidAt = at;
        VehicleCollider.Place(v.Solid, new Vector3(Quant.Pos(s.X), Quant.Pos(s.Y), Quant.Pos(s.Z)), Quant.Yaw(s.Yaw), solid);
    }

    /// <summary>The nearest Raid van to <paramref name="from"/>, or null: the damage marker of a run-over points at it (the hit event names the van by its hit id, which a client does not know).</summary>
    Vector3? NearestVan(Vector3 from)
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

    /// <summary>The Thieves event's one-shot effects. Called for every event the client receives.</summary>
    void OnThievesEvent(EventKind kind, byte[] payload)
    {
        if (kind == EventKind.ThievesFx && ThievesFxCodec.Decode(payload) is { } m) OnThievesFx(m);
        OnDishEvent(kind, payload); // the dishes taken (ticket 06)
    }

    void OnThievesFx(ThievesFxMsg m)
    {
        var at = new Vector3(m.Position.X, m.Position.Y, m.Position.Z);
        switch (m.Kind)
        {
            case ThievesFxKind.Honk:
            {
                var d = Sfx.ListenerDistance(at);
                var view = ThievesData.Van.View;
                if (d <= view.HonkHearM) Sfx.Play3D(HonkSound, at, view.HonkDb - view.HonkFadeDb * d / view.HonkHearM); // the stand-in sound has no range of its own: it fades with the distance here
                break;
            }
            case ThievesFxKind.Shock:
                OnThiefShocked(m.Id, at);
                break;
        }
    }
}
