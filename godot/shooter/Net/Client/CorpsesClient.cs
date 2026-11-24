using Godot;
using Drycastle.Shooter.Characters;
using Drycastle.ShooterCore.Camo;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Corpses;
using Drycastle.ShooterCore.Events;
using Drycastle.ShooterCore.Items;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using WaterPalettes = Drycastle.MapLoader.WaterPalettes;
using WaterSurfaces = Drycastle.MapLoader.WaterSurfaces;

namespace Drycastle.Shooter.Net.Client;

/// <summary>
/// Corpses on the client (ticket #130) as drawn: the body in the death pose with its weapon, thrown bodies tumbling, a fresh kill collapsing (#357), a body on the water floating, the
/// Bigfoot's corpse as the giant lying where it fell (#264) and a bale (<see cref="BaleCorpses"/>). It finds the corpse in reach; what the player does there is <see cref="CorpseLoot"/>.
/// A corpse is a world entity of type <see cref="CorpseRules.TypeId"/>: it appears, moves and goes with the world channel.
/// </summary>
internal sealed class CorpsesClient
{
    /// <summary>A corpse younger than this waits for its death event; one older with none is an old corpse (a join), and lies in the death pose.</summary>
    const double CollapseWaitSeconds = 0.25, CollapseWindowSeconds = 2.0;

    readonly ClientContext _ctx;
    readonly ItemsClient _items;
    readonly CamoClient _camo;
    readonly BaleCorpses _bales;
    readonly BigfootFile _bigfoot;
    readonly InteractTargets _targets;
    readonly Dictionary<ushort, CorpseView> _corpses = new();
    /// <summary>The latest death each player had (when, killer): a corpse that appears just after it is a fresh kill and collapses (#357).</summary>
    readonly Dictionary<ushort, (double At, ushort Killer)> _deathSeen = new();

    public CorpsesClient(ClientContext ctx, ItemsClient items, CamoClient camo, BaleCorpses bales, BigfootFile bigfoot, InteractTargets targets)
    {
        _ctx = ctx; _items = items; _camo = camo; _bales = bales; _bigfoot = bigfoot; _targets = targets;
        CorpseRules.Register(WorldEntityRegistry.Shared);
        ctx.WorldEntities.Spawned += OnSpawned;
        ctx.WorldEntities.Despawned += OnDespawned;
        items.CorpseItemsChanged += Dress;
        items.CorpseItemsChanged += ShowGiantHead;
    }

    /// <summary>An entity is gone (any kind: a corpse's, if it was one, is dropped after this): what is kept per corpse id goes.</summary>
    public event Action<ushort>? Despawned;

    /// <summary>The corpses by entity id.</summary>
    public IReadOnlyDictionary<ushort, CorpseView> All => _corpses;

    /// <summary>The player died (the death event): a corpse that appears now is a fresh kill.</summary>
    public void NoteDeath(DeathMsg d) => _deathSeen[d.Victim] = (_ctx.Now, d.Killer);

    /// <summary>True while a corpse of player <paramref name="owner"/> is on the map.</summary>
    public bool CorpseOf(ushort owner)
    {
        foreach (var id in _corpses.Keys)
            if (_ctx.WorldEntities.Entities.TryGetValue(id, out var e) && e.State.Owner == owner) return true;
        return false;
    }

    /// <summary>True for the corpse the Bigfoot left: a giant lying in its death pose, not a character.</summary>
    public bool IsGiantCorpse(ushort corpse) => _ctx.WorldEntities.Entities.TryGetValue(corpse, out var e) && e.State.Owner == BigfootRules.TargetId;

    /// <summary>True for a bale (#331): a corpse owned by <see cref="HarvesterRules.BaleOwner"/>.</summary>
    public bool IsBaleCorpse(ushort corpse) => _ctx.WorldEntities.Entities.TryGetValue(corpse, out var e) && HarvesterRules.IsBale(e.State.Owner);

    /// <summary>Where a corpse's feet are, from the last render.</summary>
    public Vector3? FeetOf(ushort corpse) => _corpses.TryGetValue(corpse, out var v) ? v.Feet : null;

/// <summary>A look that arrives after the corpse of that player spawned goes on it (#203).</summary>
    public void DressCamo(ushort owner, CamoSpec look)
    {
        foreach (var (id, c) in _corpses)
            if (_ctx.WorldEntities.Entities.TryGetValue(id, out var e) && e.State.Owner == owner) c.View?.SetCamo(look, _camo.Data);
    }

/// <summary>
    /// Settles whether a new corpse collapses. True once decided. A fresh kill (its death event within <see cref="CollapseWindowSeconds"/>) of a player on foot
    /// topples away from the killer; a thrown body flies, a rider's stays seated, an old one lies. Hidden until decided (at most <see cref="CollapseWaitSeconds"/>).
    /// </summary>
    bool DecideCollapse(ushort id, CorpseView v, ushort owner, Vector3 at, float yaw, bool flying)
    {
        var fresh = _deathSeen.TryGetValue(owner, out var death) && _ctx.Now - death.At < CollapseWindowSeconds;
        if (!fresh && _ctx.Now - v.Born < CollapseWaitSeconds) return false;
        v.Decided = true;
        v.Node.Visible = true;
        var rode = _ctx.Remotes.LastRode(owner) is { } when && _ctx.Now - when < 1.5;
        if (!fresh || flying || rode) return true;
        var away = new Basis(Vector3.Up, yaw) * Vector3.Back; // no killer to push it (a fall, a suicide): backwards
        Vector3? from = death.Killer == owner ? null : death.Killer == _ctx.MyId ? _ctx.Me?.GlobalPosition : _ctx.Remotes.TryGetValue(death.Killer, out var k) ? k.Node.GlobalPosition : null;
        if (from is { } f) { var d = at - f; d.Y = 0; if (d.LengthSquared() > 0.04f) away = d.Normalized(); }
        v.Tumble.BeginCollapse(away, 0.6f);
        return true;
    }

/// <summary>The up of the ground under a body (the slope it lies on): a short ray from above its feet. Up when there is none.</summary>
    Vector3 GroundUpAt(Vector3 at)
    {
        var q = PhysicsRayQueryParameters3D.Create(at + Vector3.Up * 1f, at + Vector3.Down * 1.5f, CollisionRules.StaticMask(MoverKind.Drop));
        var hit = _ctx.Owned.GetWorld3D().DirectSpaceState.IntersectRay(q);
        return hit.Count > 0 ? hit["normal"].AsVector3() : Vector3.Up;
    }

void OnSpawned(WorldReplica.Entity e)
    {
        if (e.Type != CorpseRules.TypeId) return;
        if (e.State.Owner == BigfootRules.TargetId) { _corpses[e.Id] = MakeGiantCorpse(e); return; }
        if (HarvesterRules.IsBale(e.State.Owner)) { _corpses[e.Id] = _bales.MakeCorpse(e); return; }
        var node = new Node3D { Name = $"Corpse{e.Id}" };
        var view = CharacterView.Make();
        _camo.Dress(view, e.State.Owner); // the dead player's look (#203)
        view.Position = new Vector3(0, -RagdollTumble.Half, 0); // the node sits at the body's middle, so a thrown body spins about its centre
        node.AddChild(view);
        _ctx.Owned.AddChild(node);
        node.Visible = false; // until RenderCorpses knows whether this body collapses (DecideCollapse)
        _corpses[e.Id] = new CorpseView { Node = node, View = view, Born = _ctx.Now };
        Dress(e.Id); // the item the corpse carries, when its event came first (#266)
    }

    void OnDespawned(ushort id)
    {
        _items.ForgetCorpse(id);
        Despawned?.Invoke(id);
        if (_corpses.Remove(id, out var v)) v.Node.QueueFree();
    }

/// <summary>Builds the corpse view of the giant: the model at its feet, the Death clip held at its last frame.</summary>
    CorpseView MakeGiantCorpse(WorldReplica.Entity e)
    {
        var node = new Node3D { Name = $"Corpse{e.Id}" };
        var model = BigfootModel.MakeGiant(_bigfoot, out var anim);
        node.AddChild(model);
        _ctx.Owned.AddChild(node); // in the tree first: an AnimationPlayer outside it poses nothing, and the giant would stand in its rest pose
        if (anim != null)
        {
            var clips = new Dictionary<string, string>();
            BigfootModel.IndexClips(clips, anim);
            if (clips.TryGetValue(BigfootRules.ClipDeath, out var death))
            {
                anim.Play(death);
                anim.Seek(Math.Max(0, anim.GetAnimation(death).Length - 0.01), true); // just short of the end: it lies as it fell
                anim.Pause();
            }
        }
        return new CorpseView { Node = node, View = null, Giant = true };
    }

/// <summary>The giant's corpse with or without its head: the head is the Item it carries, so once that is taken the body lies there headless.</summary>
    void ShowGiantHead(ushort corpse)
    {
        if (!_corpses.TryGetValue(corpse, out var v) || !v.Giant) return;
        if (v.Node.FindChildren("*", "Skeleton3D", true, false).FirstOrDefault() is not Skeleton3D sk) return;
        var head = sk.FindBone("Head");
        if (head < 0) return;
        sk.SetBonePoseScale(head, _items.CorpseItems(corpse).Count > 0 ? Vector3.One : Vector3.One * 0.001f); // the clip is paused: the pose stays as set
    }

/// <summary>Shows the item a corpse carries on the dead body (and nothing once it is taken or has crumbled). The view may not exist yet: it is dressed when it spawns.</summary>
    void Dress(ushort corpse)
    {
        if (!_corpses.TryGetValue(corpse, out var v) || v.View == null) return; // the giant's corpse draws no worn item
        var items = _items.CorpseItems(corpse);
        for (var s = 0; s < Equipment.SlotCount; s++)
        {
            ItemDef? shown = null; // the first item of the slot in the corpse's list (the worn ones come first)
            foreach (var h in items) if (!h.Def.BagOnly && (int)h.Def.Slot == s) { shown = h.Def; break; }
            v.View.SetWorn((EquipSlot)s, shown);
        }
    }

/// <summary>Places the corpses (position, yaw, the weapon they carry) and finds the one in reach.</summary>
    public void Render(double delta)
    {
        foreach (var (id, v) in _corpses)
        {
            if (!_ctx.WorldEntities.Entities.TryGetValue(id, out var e)) continue;
            var s = e.State;
            var p = s.Position;
            var at = new Vector3(p.X, p.Y, p.Z);
            if (v.Bale) { _bales.Render(id, v, s, at, (float)delta); continue; }
            if (v.Giant)
            {
                v.Feet = at; v.Node.GlobalPosition = at; v.Node.Basis = new Basis(Vector3.Up, s.YawRad);
                // it is looted at its head: the same point the server measures from (CorpseRules.ReachPoint)
                var head = _bigfoot.Reward.CorpseHead;
                v.ReachFrom = at + v.Node.Basis * new Vector3(head.X, head.Y, head.Z);
                continue;
            }
            var view = v.View!;
            var flying = s.PitchRad > CorpseRules.FlightPitch * 0.5f; // a thrown body (blast, car): the server flies it, we turn it and flail its limbs
            var t = v.Tumble;
            if (!v.Decided && !DecideCollapse(id, v, s.Owner, at, s.YawRad, flying)) continue;
            var wasThrown = t.Thrown;
            t.Update(at, flying, (float)delta);
            if (t.Collapsing && !t.AtRest) t.GroundUp = GroundUpAt(at);
            var weapon = _ctx.Sound.WeaponId(s.State);
            if (t.Thrown)
            {
                if (!wasThrown || weapon != v.Weapon) { v.Weapon = weapon; view.ShowThrown(weapon); }
                view.SetRagdoll(true, t.Amount, t.AtRest);
            }
            else if (weapon != v.Weapon) { v.Weapon = weapon; view.ShowCorpse(weapon); }
            v.Feet = at;
            v.Node.Visible = !(_ctx.Remotes.TryGetValue(s.Owner, out var owner) && owner.Guess.Active); // the driver's guessed throw of this body is what is drawn until it has landed (#411)
            v.Node.GlobalPosition = at + FloatOffset(id, at, t.Thrown) + Vector3.Up * t.PivotHeight;
            v.Node.Basis = t.BasisFor(s.YawRad);
        }
        _targets.Corpse = NearestInReach();
    }

    /// <summary>
    /// A corpse at rest on the water surface floats (ADR 0026): it rises and falls with the waves and drifts a little about where the server has it. Cosmetic
    /// only: the server position (<see cref="CorpseView.Feet"/>, the pickup reach) does not move. Zero off the water, in flight, or with reduce motion's frozen waves.
    /// </summary>
    Vector3 FloatOffset(ushort id, Vector3 at, bool thrown)
    {
        if (thrown || _ctx.Water.BodyAt(at.X, at.Z) is not { } body || MathF.Abs(at.Y - body.SurfaceY) > 0.15f) return Vector3.Zero;
        var t = (float)WaterSurfaces.WaveClock;
        var drift = new Vector3(MathF.Sin(t * 0.5f + id), 0, MathF.Cos(t * 0.4f + id * 1.7f)) * 0.3f;
        return drift + Vector3.Up * WaterPalettes.WaveOffset(body, at.X, at.Z);
    }

    ushort? NearestInReach()
    {
        if (_ctx.Me == null || _ctx.Dead || _ctx.Driving) return null;
        ushort? best = null;
        var bd = CorpseRules.ReachMetres;
        var at = _ctx.Me.GlobalPosition;
        foreach (var (id, v) in _corpses)
        {
            var d = (v.ReachFrom ?? v.Feet).DistanceTo(at);
            if (d <= bd) { best = id; bd = d; }
        }
        return best;
    }

/// <summary>The own corpses are shown or hidden: the death camera lies where the body fell, and the body would stand in its view.</summary>
    public void ShowOwn(bool visible)
    {
        foreach (var (id, v) in _corpses)
        {
            if (v.Giant || v.Bale || !_ctx.WorldEntities.Entities.TryGetValue(id, out var e) || e.State.Owner != _ctx.MyId) continue;
            if (v.Node.Visible != visible) v.Node.Visible = visible;
        }
    }

    /// <summary>Where the own corpse's body is (its middle), or null while it is not in the world yet.</summary>
    public Vector3? OwnBodyAt()
    {
        // the newest own body only: an older one of ours (they last two minutes) lies still elsewhere, and an unplaced one sits at the origin until Render decides it
        CorpseView? newest = null;
        foreach (var (id, v) in _corpses)
        {
            if (v.Giant || v.Bale || !_ctx.WorldEntities.Entities.TryGetValue(id, out var e) || e.State.Owner != _ctx.MyId) continue;
            if (newest == null || v.Born > newest.Born) newest = v;
        }
        return newest is { Decided: true } ? newest.Node.GlobalPosition : null;
    }
}
