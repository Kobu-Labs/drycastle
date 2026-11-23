using Godot;
using Drycastle.Shooter.Characters;
using Drycastle.Shooter.Corpses;
using Drycastle.Shooter.Menu;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Corpses;
using Drycastle.ShooterCore.Events;
using Drycastle.ShooterCore.Hud;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Thieves;
using Drycastle.ShooterCore.Waters;
using Drycastle.ShooterCore.Weapons;
using WaterPalettes = Drycastle.MapLoader.WaterPalettes;
using WaterSurfaces = Drycastle.MapLoader.WaterSurfaces;

namespace Drycastle.Shooter.Net.Client;

/// <summary>
/// Corpses on the client (ticket #130): the body in the death pose with its weapon, the pickup prompt within reach, the
/// hold and tap of the interact key, and the chooser. Holding the key takes the corpse's weapon into the primary position
/// (<see cref="CorpseRules.PrimaryPos"/>); a tap opens <see cref="CorpseChooser"/> to pick the primary or the secondary.
/// The server validates reach and slot; the hotbar it sends back (<see cref="EventKind.Hotbar"/>) is the one that counts.
/// A corpse that carries items (<see cref="EventKind.CorpseItems"/>, up to eight; issues #261, #269) adds a row of item cells to the chooser, one for each
/// of them: taking an item is a separate action from taking the weapon (a click or Enter on its cell, <see cref="RequestTakeCorpseItem"/>), and the cell
/// leaves the list when the server sends the corpse's next list. Holding the key still takes the weapon only. The prompt names the item only when there is
/// exactly one, and nothing else for several.
/// </summary>
public partial class NetClient
{
    /// <summary>Hold time after which the interact key takes the corpse into the primary position.</summary>
    const double CorpseHoldSeconds = 0.4;

    sealed class CorpseView
    {
        public Node3D Node = null!;
        /// <summary>The character that lies there; null for the giant (<see cref="Giant"/>).</summary>
        public CharacterView? View;
        /// <summary>The Bigfoot's corpse (issue #264): the giant's model in its death pose, no character, no weapon to take.</summary>
        public bool Giant;
        /// <summary>Where the corpse is looted from when that is not where it lies (the giant's head); null: its <see cref="Feet"/>.</summary>
        public Vector3? ReachFrom;
        /// <summary>A hay bale (#331): a corpse owned by <see cref="HarvesterRules.BaleOwner"/>, drawn as a bale; only its Items to take.</summary>
        public bool Bale;
        /// <summary>A bale's drawing state (smoothed position, roll, the body inside it, #351); null for anything else.</summary>
        public BaleMotion? Motion;
        public string Weapon = "";
        /// <summary>The ragdoll: the spin, limb flail and settling of a body thrown by a blast or a car.</summary>
        public readonly RagdollTumble Tumble = new();
        /// <summary>The feet, from the last render (the node sits at the body's turning point).</summary>
        public Vector3 Feet;
        /// <summary>When the entity first came (client clock), and whether the collapse question is settled (<see cref="DecideCollapse"/>).</summary>
        public double Born;
        public bool Decided;
    }

    /// <summary>The latest death each player had (when, killer): a corpse that appears just after it is a fresh kill and collapses (#357).</summary>
    readonly Dictionary<ushort, (double At, ushort Killer)> _deathSeen = new();
    /// <summary>The last time each player was seen riding a vehicle: a rider's corpse stays seated or is thrown, it does not collapse.</summary>
    readonly Dictionary<ushort, double> _lastRode = new();
    /// <summary>A corpse younger than this waits for its death event; one older with none is an old corpse (a join), and lies in the death pose.</summary>
    const double CollapseWaitSeconds = 0.25, CollapseWindowSeconds = 2.0;

    void NoteDeath(DeathMsg d) => _deathSeen[d.Victim] = (Now, d.Killer);

    /// <summary>
    /// Settles whether a new corpse collapses. True once decided. A fresh kill (its death event within <see cref="CollapseWindowSeconds"/>) of a player on foot
    /// topples away from the killer; a thrown body flies, a rider's stays seated, an old one lies. Hidden until decided (at most <see cref="CollapseWaitSeconds"/>).
    /// </summary>
    bool DecideCollapse(ushort id, CorpseView v, ushort owner, Vector3 at, float yaw, bool flying)
    {
        var fresh = _deathSeen.TryGetValue(owner, out var death) && Now - death.At < CollapseWindowSeconds;
        if (!fresh && Now - v.Born < CollapseWaitSeconds) return false;
        v.Decided = true;
        v.Node.Visible = true;
        var rode = _lastRode.TryGetValue(owner, out var when) && Now - when < 1.5;
        if (!fresh || flying || rode) return true;
        var away = new Basis(Vector3.Up, yaw) * Vector3.Back; // no killer to push it (a fall, a suicide): backwards
        Vector3? from = death.Killer == owner ? null : death.Killer == _myId ? _me?.GlobalPosition : _remotes.TryGetValue(death.Killer, out var k) ? k.Node.GlobalPosition : null;
        if (from is { } f) { var d = at - f; d.Y = 0; if (d.LengthSquared() > 0.04f) away = d.Normalized(); }
        v.Tumble.BeginCollapse(away, 0.6f);
        return true;
    }

    /// <summary>The up of the ground under a body (the slope it lies on): a short ray from above its feet. Up when there is none.</summary>
    Vector3 GroundUpAt(Vector3 at)
    {
        var q = PhysicsRayQueryParameters3D.Create(at + Vector3.Up * 1f, at + Vector3.Down * 1.5f, CollisionRules.StaticMask(MoverKind.Drop));
        var hit = Owned.GetWorld3D().DirectSpaceState.IntersectRay(q);
        return hit.Count > 0 ? hit["normal"].AsVector3() : Vector3.Up;
    }

    /// <summary>How a bale corpse is drawn (#351): its model hangs under <see cref="Spin"/>; <see cref="Shown"/> is the smoothed position, <see cref="Angle"/> the turn about its own axis, <see cref="Body"/> the caught body inside it.</summary>
    sealed class BaleMotion
    {
        public Node3D? Spin;
        /// <summary>The way it leans with the ground (<see cref="Up"/> eases to <see cref="UpTarget"/>; <see cref="Leaned"/>: looked up at rest), and the heading of its axis (<see cref="Yaw"/>: across the way it last moved).</summary>
        public Vector3 Shown, Up = Vector3.Up, UpTarget = Vector3.Up;
        public float Angle, Yaw;
        public bool Leaned;
        public bool Seen;
        public CharacterView? Body;
        /// <summary>Whether <see cref="Body"/>'s limbs fly (the bale moves) or hang still; null before the first frame.</summary>
        public bool? BodyLoose;
    }

    readonly Dictionary<ushort, CorpseView> _corpses = new();
    CorpseChooser _chooser = null!;
    /// <summary>The corpse within reach, from the last render (null when none, or when the player cannot take one).</summary>
    ushort? _near;
    InteractKey _useKey; // the interact key's press and release, one reading per tick
    InteractHold _corpseHold; // the hold for the primary position: counts only while the corpse owns the key and offers a weapon to replace it

    /// <summary>True while the corpse chooser is up; the hotbar keys and wheel belong to it then.</summary>
    bool ChooserOpen => _chooser.IsOpen;

    /// <summary>The chooser shows a list of Items: the mouse is released for it (the shell asks). A chooser with only the weapon rows keeps the mouse captured.</summary>
    public bool ChooserFreesMouse => _chooser.HasItems;

    /// <summary>Called once from <see cref="_Ready"/>: registers the corpse type, follows the corpse entities and builds the chooser.</summary>
    void InitCorpses()
    {
        CorpseRules.Register(WorldEntityRegistry.Shared);
        WorldEntities.Spawned += OnCorpseSpawned;
        WorldEntities.Despawned += OnCorpseDespawned;
        _chooser = new CorpseChooser();
        _chooser.Picked += pos =>
        {
            if (_chooser.IsOpen) SendPickUp(_chooser.Corpse, pos);
            _chooser.Close();
        };
        _chooser.ItemPicked += index => { if (_chooser.IsOpen) RequestTakeCorpseItem(_chooser.Corpse, index); }; // the cell leaves with the server's next list
        CorpseItemsChanged += ShowGiantHead;
        CorpseItemsChanged += corpse => { if (_chooser.IsOpen && _chooser.Corpse == corpse) _chooser.SetItems(ChooserItems(corpse)); };
        AddChild(_chooser);
    }

    void OnCorpseSpawned(WorldReplica.Entity e)
    {
        if (e.Type != CorpseRules.TypeId) return;
        if (e.State.Owner == BigfootRules.TargetId) { _corpses[e.Id] = MakeGiantCorpse(e); return; }
        if (HarvesterRules.IsBale(e.State.Owner)) { _corpses[e.Id] = MakeBaleCorpse(e); return; }
        var node = new Node3D { Name = $"Corpse{e.Id}" };
        var view = CharacterView.Make();
        if (Appearances.TryGetValue(e.State.Owner, out var look)) view.SetCamo(look, CamoData); // the dead player's look (#203)
        view.Position = new Vector3(0, -RagdollTumble.Half, 0); // the node sits at the body's middle, so a thrown body spins about its centre
        node.AddChild(view);
        Owned.AddChild(node);
        node.Visible = false; // until RenderCorpses knows whether this body collapses (DecideCollapse)
        _corpses[e.Id] = new CorpseView { Node = node, View = view, Born = Now };
        DressCorpse(e.Id); // the item the corpse carries, when its event came first (#266)
    }

    void OnCorpseDespawned(ushort id)
    {
        _corpseItems.Remove(id);
        HarvesterCorpseGone(id);
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
            if (v.Bale) { RenderBale(id, v, s, at, (float)GetProcessDeltaTime()); continue; }
            if (v.Giant)
            {
                v.Feet = at; v.Node.GlobalPosition = at; v.Node.Basis = new Basis(Vector3.Up, s.YawRad);
                // it is looted at its head: the same point the server measures from (CorpseRules.ReachPoint)
                var head = BigfootData.Reward.CorpseHead;
                v.ReachFrom = at + v.Node.Basis * new Vector3(head.X, head.Y, head.Z);
                continue;
            }
            var view = v.View!;
            var flying = s.PitchRad > CorpseRules.FlightPitch * 0.5f; // a thrown body (blast, car): the server flies it, we turn it and flail its limbs
            var t = v.Tumble;
            if (!v.Decided && !DecideCollapse(id, v, s.Owner, at, s.YawRad, flying)) continue;
            var wasThrown = t.Thrown;
            t.Update(at, flying, (float)GetProcessDeltaTime());
            if (t.Collapsing && !t.AtRest) t.GroundUp = GroundUpAt(at);
            var weapon = WeaponId(s.State);
            if (t.Thrown)
            {
                if (!wasThrown || weapon != v.Weapon) { v.Weapon = weapon; view.ShowThrown(weapon); }
                view.SetRagdoll(true, t.Amount, t.AtRest);
            }
            else if (weapon != v.Weapon) { v.Weapon = weapon; view.ShowCorpse(weapon); }
            v.Feet = at;
            v.Node.Visible = !(_remotes.TryGetValue(s.Owner, out var owner) && owner.Guess.Active); // the driver's guessed throw of this body is what is drawn until it has landed (#411)
            v.Node.GlobalPosition = at + FloatOffset(id, at, t.Thrown) + Vector3.Up * t.PivotHeight;
            v.Node.Basis = t.BasisFor(s.YawRad);
        }
        _near = NearestCorpseInReach();
    }

    /// <summary>
    /// A corpse at rest on the water surface floats (ADR 0026): it rises and falls with the waves and drifts a little about where the server has it. Cosmetic
    /// only: the server position (<see cref="CorpseView.Feet"/>, the pickup reach) does not move. Zero off the water, in flight, or with reduce motion's frozen waves.
    /// </summary>
    Vector3 FloatOffset(ushort id, Vector3 at, bool thrown)
    {
        if (thrown || Water.BodyAt(at.X, at.Z) is not { } body || MathF.Abs(at.Y - body.SurfaceY) > 0.15f) return Vector3.Zero;
        var t = (float)WaterSurfaces.WaveClock;
        var drift = new Vector3(MathF.Sin(t * 0.5f + id), 0, MathF.Cos(t * 0.4f + id * 1.7f)) * 0.3f;
        return drift + Vector3.Up * WaterPalettes.WaveOffset(body, at.X, at.Z);
    }

    ushort? NearestCorpseInReach()
    {
        if (_me == null || Dead || Ctx.Driving) return null;
        ushort? best = null;
        var bd = CorpseRules.ReachMetres;
        var at = _me.GlobalPosition;
        foreach (var (id, v) in _corpses)
        {
            var d = (v.ReachFrom ?? v.Feet).DistanceTo(at);
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
        if (_near is not { } id || !InteractArbiter.CorpseShowsPrompt(_crateNear != null, _groundNear != null, _groundNearest, true, ChooserOpen)) return null;
        var key = InputBindings.Name("interact");
        if (IsItemOnlyCorpse(id)) // the Bigfoot's and the Gang boss's corpse have no weapon to take, only what they carry (#264, Thieves 10)
            return CorpseItems(id).Count switch { 0 => null, 1 => Strings.F("hud.corpse.giant.item", key, ItemName(CorpseItem(id)!)), _ => Strings.F("hud.corpse.giant.items", key) };
        if (CorpseWeapon(id) is not { } w) return null;
        var hold = CorpseRules.CanReplace(w, CorpseRules.PrimaryPos);
        if (CorpseItems(id).Count > 1) return Strings.F(hold ? "hud.corpse.hold.items" : "hud.corpse.tap.items", key, WeaponName(w.Id)); // a list: its own chooser names them
        if (CorpseItem(id) is { } item) return Strings.F(hold ? "hud.corpse.hold.item" : "hud.corpse.tap.item", key, WeaponName(w.Id), ItemName(item));
        return hold
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
        if (ChooserOpen && (Dead || Ctx.Driving || _near != _chooser.Corpse)) _chooser.Close();
        var (_, released) = _useKey.Step(use);
        var near = KeyOwner == InteractOwner.Corpse ? _near : null; // a care package in reach owns the key (ticket #143), and so does a ground item nearer than the corpse (#267)
        var holdWeapon = !ChooserOpen && near is { } hid && CorpseWeapon(hid) is { } hw && CorpseRules.CanReplace(hw, CorpseRules.PrimaryPos);
        var wasSent = _corpseHold.Sent;
        if (_corpseHold.Tick(use, holdWeapon, dt, CorpseHoldSeconds)) SendPickUp(near!.Value, CorpseRules.PrimaryPos);
        if (released && !wasSent && !ChooserOpen && near is { } tap)
        {
            if (IsItemOnlyCorpse(tap)) OpenChooser(tap, null);
            else if (CorpseWeapon(tap) is { } tw) OpenChooser(tap, tw);
        }
    }

    /// <summary>The corpse's Items as the chooser's cells, in the server's order.</summary>
    List<ChooserItem> ChooserItems(ushort corpse) =>
        CorpseItems(corpse).Select(h => new ChooserItem(h, ItemIcon(h.Def), ItemName(h.Def))).ToList();

    /// <summary>A corpse that offers no weapon, only its Items: the giant's, and the Gang boss's with the Gold chain (owner <see cref="ThievesReward.ChainCorpseOwner"/>, drawn as a body like any other).</summary>
    bool IsItemOnlyCorpse(ushort corpse) =>
        IsGiantCorpse(corpse) || IsBaleCorpse(corpse) || WorldEntities.Entities.TryGetValue(corpse, out var e) && e.State.Owner == ThievesReward.ChainCorpseOwner;

    /// <summary>Opens the chooser at a corpse. <paramref name="weapon"/> is null for a corpse that offers no weapon (<see cref="IsItemOnlyCorpse"/>): the Items are all it offers.</summary>
    void OpenChooser(ushort corpse, WeaponDef? weapon)
    {
        var options = new List<(int Pos, string Text)>();
        foreach (var pos in new[] { CorpseRules.PrimaryPos, CorpseRules.SecondaryPos })
        {
            if (weapon == null || !CorpseRules.CanReplace(weapon, pos) || pos >= _hotbar.Length) continue;
            var current = WeaponName(Weapons!.Weapons[_hotbar[pos]].Id);
            options.Add((pos, Strings.F(pos == CorpseRules.PrimaryPos ? "corpse.primary" : "corpse.secondary", current)));
        }
        var items = ChooserItems(corpse);
        if (options.Count == 0 && items.Count == 0) return;
        var title = options.Count == 0 // nothing of the weapon can be taken: the Items are the offer
            ? Strings.T("corpse.title.items") // each Item's name stands in its row
            : items.Count == 0 ? Strings.F("corpse.title", WeaponName(weapon!.Id))
            : Strings.F("corpse.title.with_items", WeaponName(weapon!.Id));
        _chooser.Open(corpse, title, options, items);
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
