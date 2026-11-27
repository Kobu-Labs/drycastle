using Godot;
using Drycastle.Shooter.Demon;
using Drycastle.Shooter.Net.Server.Combat;
using Drycastle.Shooter.Net.Server.Seating;
using Drycastle.Shooter.Net.Server.Statuses;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Bunkers;
using Drycastle.ShooterCore.Chat;
using Drycastle.ShooterCore.Churches;
using Drycastle.ShooterCore.Citizens;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Demon;
using Drycastle.ShooterCore.Events;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Status;
using Drycastle.ShooterCore.Weapons;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Parish;

/// <summary>
/// The demon of the Holy Mass on the server (docs/events/README.md, "Demon"). When the pastor turns (<see cref="Begin"/>, or <c>/event demon</c>) the fight runs in stages: <b>delay</b> (the dead pastor still
/// lies there), <b>transforming</b> (the pastor's body is removed, everyone else in the church burns to death where they stand, the demon entity burns into being; it cannot be hit), <b>climbing</b> (a scripted leap
/// from the floor to its first anchor), <b>fighting</b> (<see cref="DemonBrain"/>: crawls the baked anchors, throws fireballs; hits are counted, not damage) and <b>dying</b> (the corpse time, then the event is
/// over). The brain is pure; this class feeds it the players inside the church, steps the fireballs (<see cref="FireballFlight"/>, <see cref="FireballBlast"/>) and drives the doors.
/// <para>
/// Doors: locked while someone is inside. When nobody is (a wipe, or nobody was inside when it turned) they open and wait; a living player inside shuts
/// them again after <see cref="DemonFightDef.DoorGraceS"/>. The demon heals to full on a wipe (the brain) and wakes when a player is inside.
/// </para>
/// <para>
/// The demon is hit like a citizen: a hit target of id <see cref="DemonRules.HitId"/> in every target list (<see cref="AddTarget"/>), and the damage path hands those hits to <see cref="Hit"/>. Fireballs are
/// replicated as events (<see cref="EventKind.DemonFireball"/> and <see cref="EventKind.DemonImpact"/>), the arc is flown by the clients; the damage and the burn are the victim's own (no kill credit). The pounce
/// is <see cref="PounceServer"/>'s.
/// </para>
/// </summary>
internal sealed class DemonServer
{
    readonly ServerContext _ctx;
    readonly Func<DemonFile> _demon;
    readonly Func<MassServer> _mass;
    readonly ChurchServer _church;
    readonly CitizenServer _citizens;
    readonly Sitters _sitters;
    readonly StatusServer _status;
    readonly PounceServer _pounce;
    readonly TargetBook _targets;
    readonly DealDamage _deal;
    readonly Func<WorldQuery> _query;
    readonly Action<Vector3, float> _blastGlass;
    readonly Func<Shelters> _shelters;
    readonly WeaponsFile _weapons;
    readonly string _mapDir;
    readonly Node3D _world;
    readonly EventBook _events;

    /// <summary>The stage and its clock (pure, ShooterCore): this class only does the world work of each stage. It reads the data live, as the host may replace it.</summary>
    readonly DemonLifecycle _life;
    DemonEntity? _ent;
    ushort _entId;
    DemonBrain? _brain;
    AnchorGraph? _anchors;
    int _startAnchor;
    NVec3 _spot, _spotForward;
    int _inside;
    readonly List<DemonEvent> _brainEvents = new();
    readonly List<DemonPlayer> _players = new();
    readonly List<Fireball> _balls = new();
    /// <summary>The demon's Body capsule in the physics world (<see cref="DemonBlocker"/>): null until there is a demon.</summary>
    DemonBlocker? _body;

    /// <summary>Once, with the match: registers the entity type, the curse's burn follows holy_mass_demon.json (the same numbers), and a demon that despawns is forgotten.</summary>
    public DemonServer(ServerContext ctx, Func<DemonFile> demon, Func<MassServer> mass, ChurchServer church, CitizenServer citizens, Sitters sitters, StatusServer status, PounceServer pounce, TargetBook targets,
        DealDamage deal, Func<WorldQuery> query, Action<Vector3, float> blastGlass, Func<Shelters> shelters, WeaponsFile weapons, string mapDir, Node3D world, EventBook events)
    {
        _ctx = ctx; _demon = demon; _mass = mass; _church = church; _citizens = citizens; _sitters = sitters; _status = status; _pounce = pounce; _targets = targets; _deal = deal; _query = query;
        _blastGlass = blastGlass; _shelters = shelters; _weapons = weapons; _mapDir = mapDir; _world = world; _events = events;
        _life = new DemonLifecycle(() => _demon().Fight);
        DemonRules.Register(WorldEntityRegistry.Shared);
        if (demon().Burn.Dps > 0) status.CurseBurn = demon().Burn;
        ctx.Match.Entities.Despawned += d => { if (d.Id == _entId && _ent != null) _ent = null; };
    }

    /// <summary>A demon is in the church (any stage): the Mass is void and nothing else can start it.</summary>
    public bool Active => _life.Active;

    /// <summary>The pounce's brain while the demon fights, else null.</summary>
    DemonBrain? FightingBrain => _life.Is(DemonStage.Fighting) ? _brain : null;

    /// <summary>The pastor turns: the fight starts after the transformation delay. Everyone who sat stands up.</summary>
    public void Begin()
    {
        if (_life.Active) return;
        var demon = _demon();
        if (DemonLifecycle.WhyNot(demon) is { } why) { StartFailed($"{why}; the pastor turns into nothing"); return; }
        if (_citizens.Church?.Pastor is not { } spot) { StartFailed("the church has no pastor spot; the pastor turns into nothing"); return; }
        try { _anchors = AnchorGraph.FromJson(File.ReadAllText(Path.Combine(_mapDir, "church.json"))); }
        catch (Exception e) when (e is DataFileException or IOException or System.Text.Json.JsonException)
        {
            StartFailed($"church.json has no usable anchors ({e.Message}); the pastor turns into nothing");
            return;
        }
        _sitters.StandUpAll();
        _church.DoorsFor(DoorReason.DemonStarting);
        _spot = spot.Pos;
        _spotForward = new NVec3(-MathF.Sin(spot.Yaw), 0, -MathF.Cos(spot.Yaw));
        _startAnchor = _anchors.Anchors.OrderBy(a => NVec3.DistanceSquared(a.Pos, spot.Pos)).First().Id;
        _life.Begin();
        GD.Print($"server: demon: the pastor turns in {_life.Left:0.0} s (first anchor {_startAnchor})");
    }

    /// <summary>
    /// The demon cannot be made (no data, no pastor spot, no anchors, no entity). The Mass is already void and the doors were slammed shut for the fight that
    /// will not come: open them (nobody is locked in with nothing), say so in the log and in chat, and leave the event to be ended (<c>/event current end</c>).
    /// </summary>
    void StartFailed(string why)
    {
        GD.PrintErr($"server: demon: {why}");
        _church.DoorsFor(DoorReason.DemonFailed);
    }

    /// <summary>The event is over (ended, concluded, a new round): the demon, its fireballs and the fight's state go; the doors open for whoever is locked in.</summary>
    public void Clear()
    {
        if (!_life.Active) return;
        var was = _life.Clear();
        foreach (var b in _balls) _ctx.Broadcast(EventKind.DemonImpact, DemonCodec.EncodeImpact(new DemonImpactMsg((ushort)b.Id, b.Pos, false, true)));
        _balls.Clear();
        if (_brain?.PounceVictim is { } marked) _pounce.LetGo(marked, false); // the mark and the hold go with the event
        _pounce.LetGoAll();
        if (_ent != null) { _ctx.Match.Entities.Despawn(_entId, WorldDespawnReason.Removed); _ent = null; }
        _brain = null; _anchors = null;
        _church.DoorsFor(DoorReason.DemonCleared); // nobody stays locked in (also from Delay: Void had shut them); the deferred close shuts them once the church is empty
        GD.Print($"server: demon: cleared (was {was})");
    }

    /// <summary>Once a tick, after <see cref="Tick"/>: the Body capsule follows the demon's pose; with no demon it goes.</summary>
    public void BodyTick()
    {
        var demon = _demon();
        if (_ent == null) { if (_body != null) { _body.QueueFree(); _body = null; } return; }
        if (_body == null) { _body = new DemonBlocker(); _world.AddChild(_body); }
        _body.Place(_ent.State, _ent.Position, _ent.Normal, demon.BodyM, demon.Scale);
    }

    /// <summary>Once a tick, after the Mass.</summary>
    public void Tick(float dt)
    {
        _pounce.Sweep(FightingBrain);
        if (!_life.Active) return;
        if (_mass().Event is not { Running: true }) { Clear(); return; }
        var due = _life.Tick(dt);
        switch (_life.Stage)
        {
            case DemonStage.Delay:
                if (due) Transform();
                break;
            case DemonStage.Transforming:
                if (_ent == null) { Clear(); return; }
                if (due)
                {
                    _life.BeginClimb();
                    _ent.State = DemonLifecycle.WireStateOf(DemonStage.Climbing);
                }
                break;
            case DemonStage.Climbing:
                if (_ent == null) { Clear(); return; }
                Climb();
                if (due)
                {
                    _life.BeginFight();
                    _brain = new DemonBrain(_demon(), _anchors!, new Random(), Sight, _startAnchor, FloorY);
                    GD.Print("server: demon: the fight begins");
                }
                break;
            case DemonStage.Fighting:
                if (_ent == null) { Clear(); return; }
                Fight(dt);
                break;
            case DemonStage.Dying:
                StepFireballs(dt);
                if (due)
                {
                    GD.Print("server: demon: the corpse is gone, the Mass is over");
                    _mass().Event!.End(); // phase Idle: Clear
                    Clear();
                }
                break;
        }
        if (_ent != null && _life.Active) _ent.HitsLeft = _life.Is(DemonStage.Dying) ? 0 : _brain is { } b ? 1f - Math.Min(1f, b.HitsTaken / (float)Math.Max(1, b.HitsNeeded)) : 1f;
    }

    /// <summary>The delay is over: the dead pastor is taken away, everyone else dies in the fire, the demon is made.</summary>
    void Transform()
    {
        if (_citizens.Crowd is { } crowd)
        {
            if (_mass().Event?.Pastor is { } pastor) crowd.Remove(pastor);
            var died = crowd.KillAll(citizens: false); // the citizens flee home (HolyMassEvent turns them out); the monks burn
            GD.Print($"server: demon: {died} of the monks burn to death");
        }
        _ent = new DemonEntity { Position = _spot, Normal = NVec3.UnitY, Forward = _spotForward, State = DemonState.Transforming };
        if (_ctx.Match.Entities.Spawn(_ent) is not { } id) { _ent = null; StartFailed("the entity cap is reached; no demon"); Clear(); return; }
        _entId = id;
        _life.BeginTransform();
        _church.FightBegins();
    }

    /// <summary>The leap from the floor to the first anchor: a smooth slide with a little arc, turning from upright to the surface.</summary>
    void Climb()
    {
        var a = _anchors![_startAnchor];
        var t = _life.ClimbProgress;
        var k = t * t * (3 - 2 * t);
        var from = _spot;
        var to = a.Pos;
        var pos = NVec3.Lerp(from, to, k) + NVec3.UnitY * (MathF.Sin(k * MathF.PI) * 0.8f);
        var n = NVec3.Lerp(NVec3.UnitY, a.Normal, k);
        n = n.LengthSquared() > 1e-4f ? NVec3.Normalize(n) : a.Normal;
        var dir = to - from; dir -= n * NVec3.Dot(dir, n);
        _ent!.Position = pos; _ent.Normal = n;
        if (dir.LengthSquared() > 1e-4f) _ent.Forward = NVec3.Normalize(dir);
    }

    bool Sight(NVec3 a, NVec3 b)
    {
        var d = b - a;
        var len = d.Length();
        if (len < 0.05f) return true;
        var dir = d / len;
        return _query().Wall(new Vector3(a.X, a.Y, a.Z), new Vector3(dir.X, dir.Y, dir.Z), len) >= len - 0.05f;
    }

    /// <summary>The floor's height under a point (a ray down from just above it), for where a pinned victim lies; the point's own height when there is no floor within 6 m.</summary>
    float FloorY(NVec3 p)
    {
        var from = new Vector3(p.X, p.Y + 0.4f, p.Z);
        var d = _query().Wall(from, Vector3.Down, 6f);
        return float.IsInfinity(d) ? p.Y : from.Y - d;
    }

    void Fight(float dt)
    {
        var inside = _church.PlayersInside();
        _players.Clear();
        foreach (var id in inside)
            if (_ctx.Players.TryGetValue(id, out var c))
                _players.Add(new DemonPlayer(id, c.Body.GlobalPosition.ToNumerics(), true, c.Body.Velocity.ToNumerics(), c.Car == null && c.Body.State.OnFloor, _sitters.IsSeated(id)));
        _inside = _players.Count;

        _brainEvents.Clear();
        _brain!.Step(dt, _players, _brainEvents);
        var pose = _brain.Pose;
        _ent!.Position = pose.Position; _ent.Normal = pose.Normal; _ent.Forward = pose.Forward;
        _ent.State = DemonLifecycle.WireStateOf(_brain.Phase);
        Handle(_brainEvents);
        Doors(dt);
        StepFireballs(dt);
    }

    void Handle(List<DemonEvent> events)
    {
        foreach (var e in events)
            switch (e)
            {
                case DemonFireballLaunched l:
                    _balls.Add(l.Ball);
                    _ctx.Broadcast(EventKind.DemonFireball, DemonCodec.EncodeFireball(new DemonFireballMsg((ushort)l.Ball.Id, l.Ball.Pos, l.Ball.Vel)));
                    break;
                case DemonFlameStarted f:
                    _ctx.Broadcast(EventKind.DemonFlame, DemonCodec.EncodeFlame(f.Target));
                    break;
                case DemonFlameHit h:
                    FlameBurn(h.Player);
                    break;
                case DemonPounced p:
                    _pounce.Stage(p);
                    break;
                case DemonWiped:
                    GD.Print("server: demon: everyone inside is dead; it heals and waits");
                    break;
                case DemonDied d:
                    Killed(d.Credited);
                    break;
            }
    }

    /// <summary>Join: the pounce as it is, for a client that arrives during one.</summary>
    public void SendOnJoin(ServerClient c) => _pounce.SendOnJoin(c, FightingBrain);

    /// <summary>The demon is dead: the curse to everyone who hit it and still lives, the doors open (the church closes once everyone has left), the corpse lies a while.</summary>
    void Killed(IReadOnlyList<ushort> credited)
    {
        _life.Die();
        _mass().MarkWon(); // killing the demon wins the event (the book's run ends as Won when the Mass goes Idle)
        if (_ent != null) { _ent.State = DemonState.Dead; _ent.HitsLeft = 0; }
        _church.DoorsFor(DoorReason.DemonKilled);
        var cursed = 0;
        foreach (var id in credited)
        {
            if (!_status.GrantCurse(id)) continue;
            cursed++;
            if (_ctx.Players.TryGetValue(id, out var c) && c.Peer != null) _ctx.SendEvent(c, EventKind.DemonCursed, Array.Empty<byte>());
        }
        GD.Print($"server: demon: dead; {credited.Count} hit it, {cursed} are cursed");
    }

    /// <summary>Locked while someone is inside; open and waiting when nobody is.</summary>
    void Doors(float dt)
    {
        if (!_church.TickFight(dt, _inside, _demon().Fight.DoorGraceS, out var wasOpen)) return;
        GD.Print(wasOpen ? "server: demon: the doors shut" : "server: demon: nobody inside; the doors open and it waits");
    }

    // ---- fireballs ----

    void StepFireballs(float dt)
    {
        if (_balls.Count == 0) return;
        var demon = _demon();
        var all = _targets.Live();
        var players = all.Where(t => _ctx.Players.ContainsKey(t.Id)).ToList();
        var npcs = all.Where(t => !_ctx.Players.ContainsKey(t.Id) && !DemonRules.IsHitId(t.Id) && t.Id != BigfootRules.TargetId).ToList(); // citizens, the Gang, workers, animals, dummies: a fireball hits them as a shot does
        _targets.Return(all);
        var struck = new List<HitTarget>(players.Count + npcs.Count);
        struck.AddRange(players); struck.AddRange(npcs);
        for (var i = _balls.Count - 1; i >= 0; i--)
        {
            // stopped as every shot is: walls, cover and a vehicle's hull (WorldQuery.Shot)
            var step = FireballFlight.Step(_balls[i], demon.Fireball, dt, (o, d, len) => _query().Shot(new Vector3(o.X, o.Y, o.Z), new Vector3(d.X, d.Y, d.Z), len, null).Distance, struck);
            if (step.Outcome == FireballOutcome.Flying) { _balls[i] = step.Ball; continue; }
            var ball = _balls[i];
            _balls.RemoveAt(i);
            var fizzled = step.Outcome == FireballOutcome.Expired;
            _ctx.Broadcast(EventKind.DemonImpact, DemonCodec.EncodeImpact(new DemonImpactMsg((ushort)ball.Id, step.Impact, step.Outcome == FireballOutcome.Player, fizzled)));
            if (!fizzled) FireballBurst(step, players, npcs);
        }
    }

    /// <summary>A fireball's blast: the direct hit and the splash (the church's rooms shelter each other), each player set ablaze and hurt as their own damage, each NPC hurt.</summary>
    void FireballBurst(FireballStep step, List<HitTarget> players, List<HitTarget> npcs)
    {
        var demon = _demon();
        _blastGlass(new Vector3(step.Impact.X, step.Impact.Y, step.Impact.Z), demon.Fireball.Splash.RadiusM);
        ushort? direct = step.Outcome == FireballOutcome.Player ? step.Victim : null;
        // an NPC (the same blast, the same shelter rule) takes the damage in the demon's name; it is not set ablaze (the burn set is the players') and not pushed
        foreach (var v in FireballBlast.Detonate(step.Impact, direct, npcs, demon.Fireball, _shelters()))
            _deal(DemonRules.HitId, SpecialWeapons.Burn, v.Id, MathF.Max(1f, MathF.Round(v.Damage)), false, false);
        foreach (var v in FireballBlast.Detonate(step.Impact, direct, players, demon.Fireball, _shelters(), _status.Burns, demon.Burn))
        {
            if (!_ctx.Players.TryGetValue(v.Id, out var victim)) continue;
            if (_pounce.Immune(v.Id)) { _status.Burns.Extinguish(v.Id); continue; } // just let go of the pounce: the fire does not touch them yet
            var res = _deal(v.Id, SpecialWeapons.Burn, v.Id, MathF.Max(1f, MathF.Round(v.Damage)), false, false);
            if (res == DamageResult.Ignored) { _status.Burns.Extinguish(v.Id); continue; } // spawn protected: no burn either
            if (res != DamageResult.Killed && v.Knockback != NVec3.Zero) victim.Body.AddImpulse(new Vector3(v.Knockback.X, v.Knockback.Y, v.Knockback.Z));
        }
    }

    /// <summary>A tick of the demon's flame reaches a player: its own damage and the fireball's burn (no kill credit), like a fireball's blast.</summary>
    void FlameBurn(ushort id)
    {
        if (!_ctx.Players.ContainsKey(id) || _pounce.Immune(id)) return;
        var demon = _demon();
        _status.Burns.Ignite(id, BurnSet.NoSource, demon.Burn);
        var res = _deal(id, SpecialWeapons.Burn, id, MathF.Max(1f, MathF.Round(demon.Flame.DamagePerTick)), false, false);
        if (res == DamageResult.Ignored) _status.Burns.Extinguish(id); // spawn protected: no burn either
    }

    // ---- being hit ----

    /// <summary>Adds the demon as a hit target while it can be hurt (once the fight has begun, until it dies).</summary>
    public void AddTarget(List<HitTarget> into)
    {
        if (_ent == null || !_life.Is(DemonStage.Fighting)) return;
        var demon = _demon();
        var yaw = MathF.Atan2(-_ent.Forward.X, -_ent.Forward.Z);
        into.Add(DemonRules.Target(_ent.Position, _ent.Normal, demon.BodyM, yaw, demon.Scale));
    }

    DemonHitKind? KindOf(byte weapon)
    {
        if (weapon < _weapons.Weapons.Count) return _weapons.Weapons[weapon].Splash != null ? DemonHitKind.Explosive : DemonHitKind.Bullet;
        return weapon is SpecialWeapons.Burn or SpecialWeapons.Fall or SpecialWeapons.Void or SpecialWeapons.Debug ? null : DemonHitKind.Vehicle;
    }

    /// <summary>A hit on the demon (from the damage path): counted by the brain, the shooter gets the hit marker. Ignored while it cannot be hurt.</summary>
    public DamageResult Hit(ushort attacker, byte weapon, float damage)
    {
        // the brain starts after the leap to the wall: it cannot be hurt before that (and not after it died)
        if (_brain == null || !_life.Is(DemonStage.Fighting) || KindOf(weapon) is not { } kind || !_ctx.Players.ContainsKey(attacker)) return DamageResult.Ignored;
        var events = new List<DemonEvent>();
        var r = _brain.RegisterHit(attacker, kind, events);
        if (r == DemonHitResult.Ignored) return DamageResult.Ignored;
        Handle(events);
        if (_ctx.Players.TryGetValue(attacker, out var shooter))
            _ctx.SendEvent(shooter, EventKind.Hit, WeaponCodec.Encode(new HitMsg(DemonRules.HitId, attacker, weapon, (ushort)damage, false, false, r == DemonHitResult.Killed)));
        return r == DemonHitResult.Killed ? DamageResult.Killed : DamageResult.Damaged;
    }

    /// <summary>"/event demon": the pastor turns now, without the three kills (admin test). Open doors and an empty church: it waits for the first player.</summary>
    public void Chat(ServerClient c)
    {
        if (_demon().Hits.Base <= 0) { _ctx.ChatTo(c, ChatLineKind.Error, "holy_mass_demon.json is not loaded."); return; }
        if (_events.Current is { Kind: not EventBook.Mass } && _events.WhyNotStart(EventBook.Demon) is { } busy) { _ctx.ChatTo(c, ChatLineKind.Error, busy); return; }
        var mass = _mass();
        if (mass.Ensure(c) is { } why) { _ctx.ChatTo(c, ChatLineKind.Error, why); return; }
        if (mass.Event!.Phase == MassPhase.Void || Active) { _ctx.ChatTo(c, ChatLineKind.Error, "The demon is already here. /event current end removes it."); return; }
        if (_citizens.Church?.Pastor == null || !_church.Exists) { _ctx.ChatTo(c, ChatLineKind.Error, "The church has no pastor spot or doorway: export the map again."); return; }
        mass.Event.ForceTurn();
        if (!Active) { mass.Event.End(); return; } // StartFailed has told everyone why
        _ctx.ChatTo(c, ChatLineKind.Info, $"{c.Name} turned the pastor into the demon.");
    }
}
