using Godot;
using Suchohrad.ShooterCore;

namespace Suchohrad.Shooter;

/// <summary>
/// Corpses on the client (ticket #130): the body in the death pose with its weapon, the pickup prompt within reach, the
/// hold and tap of the interact key, and the chooser. Holding the key takes the corpse's weapon into the primary position
/// (<see cref="CorpseRules.PrimaryPos"/>); a tap opens <see cref="CorpseChooser"/> to pick the primary or the secondary.
/// The server validates reach and slot; the hotbar it sends back (<see cref="EventKind.Hotbar"/>) is the one that counts.
/// </summary>
public partial class NetClient
{
    /// <summary>Hold time after which the interact key takes the corpse into the primary position.</summary>
    const double CorpseHoldSeconds = 0.4;

    sealed class CorpseView
    {
        public Node3D Node = null!;
        public CharacterView View = null!;
        public string Weapon = "";
        /// <summary>Tumble while thrown (the ragdoll): the angle about <see cref="Axis"/> (rad), the axis (horizontal, across the flight) and the last position, for the flight direction.</summary>
        public float Spin;
        public Vector3 Axis = Vector3.Right, Last;
        public bool Seen;
    }

    /// <summary>Tumble speed of a thrown corpse (rad/s) and how fast it settles flat after landing.</summary>
    const float TumbleRate = 9f, TumbleSettle = 10f, BodyMid = 0.9f;

    readonly Dictionary<ushort, CorpseView> _corpses = new();
    CorpseChooser _chooser = null!;
    /// <summary>The corpse within reach, from the last render (null when none, or when the player cannot take one).</summary>
    ushort? _near;
    double _useHeld;
    bool _useWas, _holdSent;

    /// <summary>True while the corpse chooser is up; the hotbar keys and wheel belong to it then.</summary>
    bool ChooserOpen => _chooser.IsOpen;

    /// <summary>Called once from <see cref="_Ready"/>: registers the corpse type, follows the corpse entities and builds the chooser.</summary>
    void InitCorpses()
    {
        CorpseRules.Register(WorldEntityRegistry.Shared);
        WorldEntities.Spawned += OnCorpseSpawned;
        WorldEntities.Despawned += OnCorpseDespawned;
        _chooser = new CorpseChooser();
        _chooser.Picked += pos => { if (_chooser.IsOpen) SendPickUp(_chooser.Corpse, pos); _chooser.Close(); };
        AddChild(_chooser);
    }

    void OnCorpseSpawned(WorldReplica.Entity e)
    {
        if (e.Type != CorpseRules.TypeId) return;
        var node = new Node3D { Name = $"Corpse{e.Id}" };
        var view = CharacterView.Make();
        view.Position = new Vector3(0, -BodyMid, 0); // the node sits at the body's middle, so a thrown body spins about its centre
        node.AddChild(view);
        Owned.AddChild(node);
        _corpses[e.Id] = new CorpseView { Node = node, View = view };
    }

    void OnCorpseDespawned(ushort id)
    {
        if (!_corpses.Remove(id, out var v)) return;
        v.Node.QueueFree();
        if (_chooser.IsOpen && _chooser.Corpse == id) _chooser.Close();
    }

    /// <summary>Places the corpses (position, yaw, the weapon they carry) and finds the one in reach.</summary>
    void RenderCorpses()
    {
        foreach (var (id, v) in _corpses)
        {
            if (!WorldEntities.Entities.TryGetValue(id, out var e)) continue;
            var s = e.State;
            var p = s.Position;
            var at = new Vector3(p.X, p.Y, p.Z);
            var flying = s.PitchRad > CorpseRules.FlightPitch * 0.5f; // a thrown body (blast, car): the server flies it, we tumble it
            if (v.Seen && flying)
            {
                var d = at - v.Last; d.Y = 0;
                if (d.LengthSquared() > 1e-6f) v.Axis = Vector3.Up.Cross(d.Normalized()).Normalized(); // pitches the body forward along its flight
            }
            v.Last = at; v.Seen = true;
            var dt = (float)GetProcessDeltaTime();
            if (flying) v.Spin += TumbleRate * dt;
            else if (v.Spin != 0)
            {
                var whole = Mathf.Round(v.Spin / Mathf.Tau) * Mathf.Tau; // the nearest full turn: lying flat again
                v.Spin = Mathf.MoveToward(v.Spin, whole, TumbleSettle * dt);
                if (Mathf.Abs(v.Spin - whole) < 0.01f) v.Spin = 0;
            }
            v.Node.GlobalPosition = at + Vector3.Up * BodyMid;
            v.Node.Basis = new Basis(v.Axis, v.Spin) * new Basis(Vector3.Up, s.YawRad);
            var weapon = WeaponId(s.State);
            if (weapon != v.Weapon) { v.Weapon = weapon; v.View.ShowCorpse(weapon); }
        }
        _near = NearestCorpseInReach();
    }

    ushort? NearestCorpseInReach()
    {
        if (_me == null || Dead || _driving) return null;
        ushort? best = null;
        var bd = CorpseRules.ReachMetres;
        var at = _me.GlobalPosition;
        foreach (var (id, v) in _corpses)
        {
            var d = (v.Node.GlobalPosition - Vector3.Up * BodyMid).DistanceTo(at);
            if (d <= bd) { best = id; bd = d; }
        }
        return best;
    }

    /// <summary>The weapon a corpse carries, or null when it is unknown.</summary>
    WeaponDef? CorpseWeapon(ushort id) =>
        Weapons != null && WorldEntities.Entities.TryGetValue(id, out var e) && e.State.State < Weapons.Weapons.Count
            ? Weapons.Weapons[e.State.State] : null;

    /// <summary>The corpse prompt in reach (hidden while the chooser is up); null otherwise.</summary>
    string? CorpsePrompt()
    {
        if (_near is not { } id || ChooserOpen || CorpseWeapon(id) is not { } w) return null;
        var key = InputBindings.Name("interact");
        return CorpseRules.CanReplace(w, CorpseRules.PrimaryPos)
            ? Strings.F("hud.corpse.hold", key, WeaponName(w.Id))
            : Strings.F("hud.corpse.tap", key, WeaponName(w.Id));
    }

    /// <summary>
    /// Hold and tap of the interact key at a corpse. Held for <see cref="CorpseHoldSeconds"/> takes it into the primary
    /// position; released sooner opens the chooser. Runs on the input tick, with the same Use key as the vehicles.
    /// </summary>
    void TickCorpses(double dt)
    {
        var use = Input.MouseMode == Input.MouseModeEnum.Captured && Input.IsActionPressed("interact");
        if (ChooserOpen && (Dead || _driving || _near != _chooser.Corpse)) _chooser.Close();
        if (use && !_useWas) { _useHeld = 0; _holdSent = false; }
        if (use) _useHeld += dt;
        var near = _crateNear != null ? null : _near; // a care package in reach owns the key (ticket #143)
        if (use && !_holdSent && !ChooserOpen && near is { } id && _useHeld >= CorpseHoldSeconds && CorpseWeapon(id) is { } w
            && CorpseRules.CanReplace(w, CorpseRules.PrimaryPos))
        {
            SendPickUp(id, CorpseRules.PrimaryPos);
            _holdSent = true;
        }
        if (!use && _useWas && !_holdSent && !ChooserOpen && near is { } tap && CorpseWeapon(tap) is { } tw)
            OpenChooser(tap, tw);
        _useWas = use;
    }

    void OpenChooser(ushort corpse, WeaponDef weapon)
    {
        var options = new List<(int Pos, string Text)>();
        foreach (var pos in new[] { CorpseRules.PrimaryPos, CorpseRules.SecondaryPos })
        {
            if (!CorpseRules.CanReplace(weapon, pos) || pos >= _hotbar.Length) continue;
            var current = WeaponName(Weapons!.Weapons[_hotbar[pos]].Id);
            options.Add((pos, Strings.F(pos == CorpseRules.PrimaryPos ? "corpse.primary" : "corpse.secondary", current)));
        }
        if (options.Count == 0) return;
        _chooser.Open(corpse, Strings.F("corpse.title", WeaponName(weapon.Id)), options);
    }

    void SendPickUp(ushort corpse, int pos) =>
        Tx(_peer, Wire.ChanReliable, Wire.Encode(new EventMsg((byte)EventKind.PickUpCorpse, CorpseCodec.EncodePickUp(corpse, (byte)pos))), (int)ENetPacketPeer.FlagReliable);

    /// <summary>The server's hotbar after a pickup. A selected weapon that was replaced hands the selection to its new weapon.</summary>
    void ApplyHotbar(int[] hot)
    {
        if (Weapons == null || hot.Length == 0) return;
        for (var i = 0; i < Math.Min(hot.Length, _hotbar.Length); i++)
            if (hot[i] != _hotbar[i] && _wantSlot == _hotbar[i] + 1) _wantSlot = hot[i] + 1;
        _hotbar = hot;
        _hud?.SetWeapons(_hotbar.Select(i => WeaponName(Weapons.Weapons[i].Id)).ToList());
    }
}
