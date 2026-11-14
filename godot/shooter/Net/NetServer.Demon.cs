using Godot;
using Drycastle.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

/// <summary>
/// The demon of the Holy Mass on the server (docs/events/README.md, "Demon"). When the pastor turns (<see cref="PastorTurns"/>, or
/// <c>/event demon</c>) the fight runs in stages: <b>delay</b> (the dead pastor still lies there), <b>transforming</b> (the pastor's body is removed, everyone
/// else in the church burns to death where they stand, the demon entity burns into being; it cannot be hit), <b>climbing</b> (a scripted leap from the
/// floor to its first anchor), <b>fighting</b> (<see cref="DemonBrain"/>: crawls the baked anchors, throws fireballs; hits are counted, not damage) and
/// <b>dying</b> (the corpse time, then the event is over). The brain is pure; this file feeds it the players inside the church, steps the fireballs
/// (<see cref="FireballFlight"/>, <see cref="FireballBlast"/>) and drives the doors.
/// <para>
/// Doors: locked while someone is inside. When nobody is (a wipe, or nobody was inside when it turned) they open and wait; a living player inside shuts
/// them again after <see cref="DemonFightDef.DoorGraceS"/>. The demon heals to full on a wipe (the brain) and wakes when a player is inside.
/// </para>
/// <para>
/// The demon is hit like a citizen: a hit target of id <see cref="DemonRules.HitId"/> in every target list (<see cref="AddDemonTarget"/>), and
/// <see cref="Deal"/> hands those hits to <see cref="DemonHit"/>. Fireballs are replicated as events (<see cref="EventKind.DemonFireball"/> and
/// <see cref="EventKind.DemonImpact"/>), the arc is flown by the clients; the damage and the burn are the victim's own (no kill credit).
/// </para>
/// </summary>
public partial class NetServer
{
    /// <summary>holy_mass_demon.json (set by the host). Without it (zero hits) the demon does not appear.</summary>
    public DemonFile Demon = new();

    enum DemonStage : byte { None, Delay, Transforming, Climbing, Fighting, Dying }

    DemonStage _dStage;
    float _dTime;
    DemonEntity? _dEnt;
    ushort _dEntId;
    DemonBrain? _brain;
    AnchorGraph? _anchors;
    int _dStartAnchor;
    NVec3 _dSpot, _dSpotForward;
    float _dDoorT;
    int _dInside;
    readonly List<DemonEvent> _dEvents = new();
    readonly List<DemonPlayer> _dPlayers = new();
    readonly List<Fireball> _balls = new();

    /// <summary>A demon is in the church (any stage): the Mass is void and nothing else can start it.</summary>
    public bool DemonActive => _dStage != DemonStage.None;

    /// <summary>Once, with the match: the curse's burn follows holy_mass_demon.json (the same numbers), and the pastor's third death starts the fight.</summary>
    void DemonInit()
    {
        DemonRules.Register(WorldEntityRegistry.Shared);
        if (Demon.Burn.Dps > 0) CurseBurnDef = Demon.Burn;
        PastorTurns += DemonBegin;
        _match.Entities.Despawned += d => { if (d.Id == _dEntId && _dEnt != null) _dEnt = null; };
    }

    /// <summary>The pastor turns: the fight starts after the transformation delay. Everyone who sat stands up.</summary>
    void DemonBegin()
    {
        if (_dStage != DemonStage.None) return;
        if (Demon.Hits.Base <= 0) { GD.PrintErr("server: demon: holy_mass_demon.json is not loaded; the pastor turns into nothing"); return; }
        if (_church?.Pastor is not { } spot) return;
        try { _anchors = AnchorGraph.FromJson(File.ReadAllText(Path.Combine(MapDir, "church.json"))); }
        catch (Exception e) when (e is DataFileException or IOException or System.Text.Json.JsonException)
        {
            GD.PrintErr($"server: demon: church.json has no usable anchors ({e.Message}); the pastor turns into nothing");
            return;
        }
        foreach (var id in _sitters.Keys.ToList()) MassStandUp(id);
        _closeWhenEmpty = false;
        _dSpot = spot.Pos;
        _dSpotForward = new NVec3(-MathF.Sin(spot.Yaw), 0, -MathF.Cos(spot.Yaw));
        _dStartAnchor = _anchors.Anchors.OrderBy(a => NVec3.DistanceSquared(a.Pos, spot.Pos)).First().Id;
        _dStage = DemonStage.Delay; _dTime = Demon.Fight.TransformDelayS;
        GD.Print($"server: demon: the pastor turns in {_dTime:0.0} s (first anchor {_dStartAnchor})");
    }

    /// <summary>The event is over (ended, concluded, a new round): the demon, its fireballs and the fight's state go; the doors open for whoever is locked in.</summary>
    void DemonClear()
    {
        if (_dStage == DemonStage.None) return;
        var was = _dStage;
        _dStage = DemonStage.None;
        foreach (var b in _balls) BroadcastEvent(EventKind.DemonImpact, DemonCodec.EncodeImpact(new DemonImpactMsg((ushort)b.Id, b.Pos, false, true)));
        _balls.Clear();
        if (_dEnt != null) { _match.Entities.Despawn(_dEntId, WorldDespawnReason.Removed); _dEnt = null; }
        _brain = null; _anchors = null;
        if (was != DemonStage.Delay) ChurchDoors.Open(); // nobody stays locked in; MassTick shuts them once the church is empty
        GD.Print($"server: demon: cleared (was {was})");
    }

    /// <summary>Once a tick, after the Mass.</summary>
    void DemonTick(float dt)
    {
        if (_dStage == DemonStage.None) return;
        if (_mass is not { Running: true }) { DemonClear(); return; }
        _dTime -= dt;
        switch (_dStage)
        {
            case DemonStage.Delay:
                if (_dTime <= 0) DemonTransform();
                break;
            case DemonStage.Transforming:
                if (_dEnt == null) { DemonClear(); return; }
                if (_dTime <= 0)
                {
                    _dStage = DemonStage.Climbing; _dTime = Demon.Fight.ClimbS;
                    _dEnt.State = DemonState.Crawling;
                }
                break;
            case DemonStage.Climbing:
                if (_dEnt == null) { DemonClear(); return; }
                DemonClimb();
                if (_dTime <= 0)
                {
                    _dStage = DemonStage.Fighting;
                    _brain = new DemonBrain(Demon, _anchors!, new Random(), DemonSight, _dStartAnchor);
                    GD.Print("server: demon: the fight begins");
                }
                break;
            case DemonStage.Fighting:
                if (_dEnt == null) { DemonClear(); return; }
                DemonFight(dt);
                break;
            case DemonStage.Dying:
                StepFireballs(dt);
                if (_dTime <= 0)
                {
                    GD.Print("server: demon: the corpse is gone, the Mass is over");
                    _mass.End(); // phase Idle: DemonClear
                    DemonClear();
                }
                break;
        }
        if (_dEnt != null && _dStage != DemonStage.None) _dEnt.HitsLeft = _dStage == DemonStage.Dying ? 0 : _brain is { } b ? 1f - Math.Min(1f, b.HitsTaken / (float)Math.Max(1, b.HitsNeeded)) : 1f;
    }

    /// <summary>The delay is over: the dead pastor is taken away, everyone else dies in the fire, the demon is made.</summary>
    void DemonTransform()
    {
        if (_crowd != null)
        {
            if (_mass?.Pastor is { } pastor) _crowd.Remove(pastor);
            var died = _crowd.KillAll(citizens: false); // the citizens flee home (HolyMassEvent turns them out); the monks burn
            GD.Print($"server: demon: {died} of the monks burn to death");
        }
        _dEnt = new DemonEntity { Position = _dSpot, Normal = NVec3.UnitY, Forward = _dSpotForward, State = DemonState.Transforming };
        if (_match.Entities.Spawn(_dEnt) is not { } id) { GD.PrintErr("server: demon: the entity cap is reached; no demon"); _dEnt = null; DemonClear(); return; }
        _dEntId = id;
        _dStage = DemonStage.Transforming; _dTime = Demon.Fight.TransformS;
        _dDoorT = 0;
    }

    /// <summary>The leap from the floor to the first anchor: a smooth slide with a little arc, turning from upright to the surface.</summary>
    void DemonClimb()
    {
        var a = _anchors![_dStartAnchor];
        var t = 1f - Math.Clamp(_dTime / Demon.Fight.ClimbS, 0f, 1f);
        var k = t * t * (3 - 2 * t);
        var from = _dSpot;
        var to = a.Pos;
        var pos = NVec3.Lerp(from, to, k) + NVec3.UnitY * (MathF.Sin(k * MathF.PI) * 0.8f);
        var n = NVec3.Lerp(NVec3.UnitY, a.Normal, k);
        n = n.LengthSquared() > 1e-4f ? NVec3.Normalize(n) : a.Normal;
        var dir = to - from; dir -= n * NVec3.Dot(dir, n);
        _dEnt!.Position = pos; _dEnt.Normal = n;
        if (dir.LengthSquared() > 1e-4f) _dEnt.Forward = NVec3.Normalize(dir);
    }

    bool DemonSight(NVec3 a, NVec3 b)
    {
        var d = b - a;
        var len = d.Length();
        if (len < 0.05f) return true;
        var dir = d / len;
        return WallDistance(new Vector3(a.X, a.Y, a.Z), new Vector3(dir.X, dir.Y, dir.Z), len) >= len - 0.05f;
    }

    void DemonFight(float dt)
    {
        var inside = ChurchDoors.PlayersInside();
        _dPlayers.Clear();
        foreach (var id in inside)
            if (_byId.TryGetValue(id, out var c))
                _dPlayers.Add(new DemonPlayer(id, N(c.Body.GlobalPosition), true, N(c.Body.Velocity)));
        _dInside = _dPlayers.Count;

        _dEvents.Clear();
        _brain!.Step(dt, _dPlayers, _dEvents);
        var pose = _brain.Pose;
        _dEnt!.Position = pose.Position; _dEnt.Normal = pose.Normal; _dEnt.Forward = pose.Forward;
        _dEnt.State = _brain.Phase switch
        {
            DemonPhase.Waiting => DemonState.Waiting,
            DemonPhase.Dwelling => DemonState.Dwelling,
            DemonPhase.Crawling => DemonState.Crawling,
            DemonPhase.Telegraphing => DemonState.Telegraphing,
            DemonPhase.Roaring => DemonState.Roaring,
            DemonPhase.LeapWind => DemonState.Crouching,
            DemonPhase.Leaping => DemonState.Leaping,
            DemonPhase.Breathing => DemonState.Breathing,
            _ => DemonState.Dead,
        };
        DemonHandle(_dEvents);
        DemonDoors(dt);
        StepFireballs(dt);
    }

    void DemonHandle(List<DemonEvent> events)
    {
        foreach (var e in events)
            switch (e)
            {
                case DemonFireballLaunched l:
                    _balls.Add(l.Ball);
                    BroadcastEvent(EventKind.DemonFireball, DemonCodec.EncodeFireball(new DemonFireballMsg((ushort)l.Ball.Id, l.Ball.Pos, l.Ball.Vel)));
                    break;
                case DemonFlameStarted f:
                    BroadcastEvent(EventKind.DemonFlame, DemonCodec.EncodeFlame(f.Target));
                    break;
                case DemonFlameHit h:
                    FlameBurn(h.Player);
                    break;
                case DemonWiped:
                    GD.Print("server: demon: everyone inside is dead; it heals and waits");
                    break;
                case DemonDied d:
                    DemonKilled(d.Credited);
                    break;
            }
    }

    /// <summary>The demon is dead: the curse to everyone who hit it and still lives, the doors open (the church closes once everyone has left), the corpse lies a while.</summary>
    void DemonKilled(IReadOnlyList<ushort> credited)
    {
        _dStage = DemonStage.Dying; _dTime = Demon.Fight.CorpseS;
        if (_dEnt != null) { _dEnt.State = DemonState.Dead; _dEnt.HitsLeft = 0; }
        ChurchDoors.Open();
        _closeWhenEmpty = true;
        var cursed = 0;
        foreach (var id in credited)
        {
            if (!GrantCurse(id)) continue;
            cursed++;
            if (_byId.TryGetValue(id, out var c) && c.Peer != null) Send(c, new EventMsg((byte)EventKind.DemonCursed, Array.Empty<byte>()));
        }
        GD.Print($"server: demon: dead; {credited.Count} hit it, {cursed} are cursed");
    }

    /// <summary>Locked while someone is inside; open and waiting when nobody is.</summary>
    void DemonDoors(float dt)
    {
        if (_dInside > 0)
        {
            if (!ChurchDoors.IsOpen) { _dDoorT = 0; return; }
            _dDoorT += dt;
            if (_dDoorT >= Demon.Fight.DoorGraceS) { ChurchDoors.Close(); GD.Print("server: demon: the doors shut"); }
        }
        else
        {
            _dDoorT = 0;
            if (!ChurchDoors.IsOpen) { ChurchDoors.Open(); GD.Print("server: demon: nobody inside; the doors open and it waits"); }
        }
    }

    // ---- fireballs ----

    void StepFireballs(float dt)
    {
        if (_balls.Count == 0) return;
        var all = LiveTargets();
        var players = all.Where(t => _byId.ContainsKey(t.Id)).ToList();
        ReturnTargets(all);
        for (var i = _balls.Count - 1; i >= 0; i--)
        {
            var step = FireballFlight.Step(_balls[i], Demon.Fireball, dt, (o, d, len) => WallDistance(new Vector3(o.X, o.Y, o.Z), new Vector3(d.X, d.Y, d.Z), len), players);
            if (step.Outcome == FireballOutcome.Flying) { _balls[i] = step.Ball; continue; }
            var ball = _balls[i];
            _balls.RemoveAt(i);
            var fizzled = step.Outcome == FireballOutcome.Expired;
            BroadcastEvent(EventKind.DemonImpact, DemonCodec.EncodeImpact(new DemonImpactMsg((ushort)ball.Id, step.Impact, step.Outcome == FireballOutcome.Player, fizzled)));
            if (!fizzled) FireballBurst(step, players);
        }
    }

    /// <summary>A fireball's blast: the direct hit and the splash (the church's rooms shelter each other), each victim set ablaze and hurt as their own damage.</summary>
    void FireballBurst(FireballStep step, List<HitTarget> players)
    {
        BlastGlass(new Vector3(step.Impact.X, step.Impact.Y, step.Impact.Z), Demon.Fireball.Splash.RadiusM);
        ushort? direct = step.Outcome == FireballOutcome.Player ? step.Victim : null;
        foreach (var v in FireballBlast.Detonate(step.Impact, direct, players, Demon.Fireball, MapShelters, _burns, Demon.Burn))
        {
            if (!_byId.TryGetValue(v.Id, out var victim)) continue;
            var res = Deal(v.Id, SpecialWeapons.Burn, v.Id, MathF.Max(1f, MathF.Round(v.Damage)), false, false);
            if (res == DamageResult.Ignored) { _burns.Extinguish(v.Id); continue; } // spawn protected: no burn either
            if (res != DamageResult.Killed && v.Knockback != NVec3.Zero) victim.Body.AddImpulse(new Vector3(v.Knockback.X, v.Knockback.Y, v.Knockback.Z));
        }
    }

    /// <summary>A tick of the demon's flame reaches a player: its own damage and the fireball's burn (no kill credit), like a fireball's blast.</summary>
    void FlameBurn(ushort id)
    {
        if (!_byId.ContainsKey(id)) return;
        _burns.Ignite(id, BurnSet.NoSource, Demon.Burn);
        var res = Deal(id, SpecialWeapons.Burn, id, MathF.Max(1f, MathF.Round(Demon.Flame.DamagePerTick)), false, false);
        if (res == DamageResult.Ignored) _burns.Extinguish(id); // spawn protected: no burn either
    }

    // ---- being hit ----

    /// <summary>Adds the demon as a hit target while it can be hurt (once the fight has begun, until it dies).</summary>
    void AddDemonTarget(List<HitTarget> into)
    {
        if (_dEnt == null || _dStage != DemonStage.Fighting) return;
        var yaw = MathF.Atan2(-_dEnt.Forward.X, -_dEnt.Forward.Z);
        into.Add(DemonRules.Target(_dEnt.Position, _dEnt.Normal, Demon.BodyM, yaw, Demon.Scale));
    }

    DemonHitKind? DemonKindOf(byte weapon)
    {
        if (weapon < Weapons.Weapons.Count) return Weapons.Weapons[weapon].Splash != null ? DemonHitKind.Explosive : DemonHitKind.Bullet;
        return weapon is SpecialWeapons.Burn or SpecialWeapons.Fall or SpecialWeapons.Drown or SpecialWeapons.Void or SpecialWeapons.Debug ? null : DemonHitKind.Vehicle;
    }

    /// <summary>A hit on the demon (from <see cref="Deal"/>): counted by the brain, the shooter gets the hit marker. Ignored while it cannot be hurt.</summary>
    DamageResult DemonHit(ushort attacker, byte weapon, float damage)
    {
        // the brain starts after the leap to the wall: it cannot be hurt before that (and not after it died)
        if (_brain == null || _dStage != DemonStage.Fighting || DemonKindOf(weapon) is not { } kind || !_byId.ContainsKey(attacker)) return DamageResult.Ignored;
        var events = new List<DemonEvent>();
        var r = _brain.RegisterHit(attacker, kind, events);
        if (r == DemonHitResult.Ignored) return DamageResult.Ignored;
        DemonHandle(events);
        if (_byId.TryGetValue(attacker, out var shooter))
            Send(shooter, new EventMsg((byte)EventKind.Hit, WeaponCodec.Encode(new HitMsg(DemonRules.HitId, attacker, weapon, (ushort)damage, false, false, r == DemonHitResult.Killed))));
        return r == DemonHitResult.Killed ? DamageResult.Killed : DamageResult.Damaged;
    }

    /// <summary>"/event demon": the pastor turns now, without the three kills (admin test). Open doors and an empty church: it waits for the first player.</summary>
    void ChatDemon(Client c)
    {
        if (Demon.Hits.Base <= 0) { ChatTo(c, ChatLineKind.Error, "holy_mass_demon.json is not loaded."); return; }
        if (EnsureMass(c) is { } why) { ChatTo(c, ChatLineKind.Error, why); return; }
        if (_mass!.Phase == MassPhase.Void || DemonActive) { ChatTo(c, ChatLineKind.Error, "The demon is already here. /event current end removes it."); return; }
        if (_church?.Pastor == null || MapChurch == null) { ChatTo(c, ChatLineKind.Error, "The church has no pastor spot or doorway: export the map again."); return; }
        _mass.ForceTurn();
        if (!DemonActive) { _mass.End(); ChatTo(c, ChatLineKind.Error, "The demon could not start (see the server log)."); return; }
        ChatToAll(ChatLineKind.System, "", $"{c.Name} turned the pastor into the demon.");
    }
}
