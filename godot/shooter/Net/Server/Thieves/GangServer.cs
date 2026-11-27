using Godot;
using Drycastle.Shooter.Net.Bots;
using Drycastle.Shooter.Net.Server.CarPool;
using Drycastle.Shooter.Net.Server.Combat;
using Drycastle.Shooter.Net.Server.Panes;
using Drycastle.Shooter.Net.Server.Shooting;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Corpses;
using Drycastle.ShooterCore.Events;
using Drycastle.ShooterCore.Items;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Progression;
using Drycastle.ShooterCore.Thieves;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Weapons;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Thieves;

/// <summary>
/// The Gang members of the Thieves event on the server (GLOSSARY "Gang member", "Raid"; docs/events/thieves/02): world entities (type 54) with a pure brain
/// (<see cref="GangMember"/>), owned by <see cref="ThiefGang"/>s that a <see cref="GangRoster"/> keeps. Like the citizens they are not bots, not in the roster and
/// not seen by the bots; unlike them they are enemies: turrets and killstreak weapons shoot them. This class is the roster's world (<see cref="IGangWorld"/>: the living players, a blow, the mesh).
/// <para>
/// They are hit targets (<see cref="AddTargets"/>, with a view tick where the shooter saw them: they run, so they have a pose ring each, like the Bigfoot: ADR 0013) and
/// <see cref="MatchSim.ApplyDamage"/> hands their ids to the roster, which counts the hit (a Hit count, not hit points). A kill pays a little XP and is not in the kill feed. A member's blow
/// reaches a player through the damage path under <see cref="SpecialWeapons.Gang"/>. The routes they walk come from the bots' navigation mesh: the route worker finds them, the oldest request first
/// (<see cref="RouteTick"/>), the answers are applied through <see cref="DeliverRoute"/>.
/// </para>
/// <para>
/// The Rival crew's (#495) are members of this roster too (<see cref="GangCrew.RivalCrew"/>): a Gunman's line of sight is <see cref="Clear"/> and its rounds <see cref="Shoot"/>, resolved against the walls and the
/// living players on server time (no rewind: ADR 0013 rewinds only a player's shot), hurting through the damage path under <see cref="SpecialWeapons.Gang"/> and shown with the shared Shot and PelletFan events from
/// no player (as the helicopter helper's). Their kills pay nothing and they have no voice.
/// </para>
/// <para>
/// Their audio (docs/events/thieves/09): who says what is decided by <see cref="GangAudio"/> and sent as <see cref="EventKind.ThiefVoice"/> to everyone; a member that carries a speaker has it in bit 6 of
/// its hp byte; when it is killed the speaker drops where it fell as the plain Item <see cref="SpeakerItemId"/> (bag only, silent).
/// </para>
/// </summary>
internal sealed class GangServer : IGangWorld
{
    /// <summary>Most routes in flight for the Gang, so bots and Gang members share the worker (<see cref="BotNav.MaxInFlight"/> is the total).</summary>
    const int MaxRoutesInFlight = 4;

    /// <summary>The id in items.json of the speaker a killed member drops.</summary>
    const string SpeakerItemId = "speaker";

    readonly ServerContext _ctx;
    readonly ThievesFile _data;
    readonly DealDamage _deal;
    readonly Func<BotNav?> _nav;
    readonly Func<long> _nextTicket;
    readonly Func<IWalkerGround> _walk;
    readonly ItemsFile _items;
    readonly WeaponsFile _weapons;
    readonly ShotWorld _shot;
    readonly TargetBook _targets;
    readonly ImpactFx _impacts;
    readonly CarDamage _carDamage;
    readonly GlassServer _glass;
    readonly Dictionary<string, int> _weaponIndex = new();
    readonly List<ThiefGang> _voiced = new();
    readonly CorpseBook _corpses;
    readonly Func<ushort, bool> _scores;
    readonly Action<IEnumerable<XpGrant>> _awardXp;
    readonly List<GangPlayer> _players = new();
    readonly List<GangPlayer> _listeners = new();
    GangAudio? _audio;
    double _preparedAt;
    bool _speakerItemWarned;

    public GangServer(ServerContext ctx, ThievesFile data, WeaponsFile weapons, ItemsFile items, CorpseBook corpses, DealDamage deal, Func<BotNav?> nav, Func<long> nextTicket,
        Func<IWalkerGround> walk, Func<ushort, bool> scores, Action<IEnumerable<XpGrant>> awardXp, ShotWorld shot, TargetBook targets, ImpactFx impacts, CarDamage carDamage, GlassServer glass)
    {
        _carDamage = carDamage; _glass = glass;
        _ctx = ctx; _data = data; _items = items; _corpses = corpses; _deal = deal; _nav = nav; _nextTicket = nextTicket; _walk = walk; _scores = scores; _awardXp = awardXp;
        _weapons = weapons; _shot = shot; _targets = targets; _impacts = impacts;
        for (var i = 0; i < weapons.Weapons.Count; i++) _weaponIndex.TryAdd(weapons.Weapons[i].Id, i);
        GangRules.Register(WorldEntityRegistry.Shared);
        Roster = new GangRoster(data.Gang, this, ctx.Match.Entities, System.Environment.TickCount) { Log = s => GD.PrintErr($"server: {s}") };
        Roster.SourceOf = w => BigfootRules.SourceOf(w, weapons.Weapons.Count, i => weapons.Weapons[i].Kind == WeaponKind.Projectile);
        Roster.Killed += OnKilled;
        ctx.Match.Gang = Roster;
    }

    public GangRoster Roster { get; }

    // ---- the roster's world ----

    public IReadOnlyList<GangPlayer> Players => _players;

    public void Hurt(ushort player, float damage, ushort memberHitId) => _deal(memberHitId, SpecialWeapons.Gang, player, damage, false, false);

    public NVec3 Snap(NVec3 p) => _nav() is { } nav ? nav.Snap(new Vector3(p.X, p.Y, p.Z)).ToNumerics() : p;

    public IWalkerGround? Walk => _walk();

    /// <summary>True when no world surface (a wall, cover, a vehicle's hull) lies between the two points: the same stop as a bullet's.</summary>
    public bool Clear(NVec3 from, NVec3 to)
    {
        var d = NVec3.Distance(from, to);
        if (d <= 0) return true;
        var dir = (to - from) / d;
        var origin = new Vector3(from.X, from.Y, from.Z);
        // the vehicle a rider's crew shoots from, or a viewer sits in, is not in the way of the line, nor is the vehicle the one looked at rides in: a Gunman sees a player in a car (its round then stops on the hull and hurts the car)
        return _shot.WorldHitSkipping(origin, new Vector3(dir.X, dir.Y, dir.Z), d, out _, MountAt(origin) ?? RidingAt(from), RidingAt(to)) >= d - 0.05f;
    }

    /// <summary>The vehicle a living player rides who stands at <paramref name="p"/> (their feet within a metre of it, sideways, and up to their eye above), or null.</summary>
    ServerCar? RidingAt(NVec3 p)
    {
        foreach (var c in _ctx.Players.Values)
        {
            if (c.Car is not { } car) continue;
            var f = c.Body.GlobalPosition;
            var dx = f.X - p.X; var dz = f.Z - p.Z;
            if (dx * dx + dz * dz <= 0.36f && p.Y >= f.Y - 0.5f && p.Y <= f.Y + 2.5f) return car;
        }
        return null;
    }

    // ---- riders ----

    readonly List<ServerCar> _mounts = new();
    readonly List<ushort> _mountIds = new();
    readonly List<RoundHull> _hulls = new();

    /// <summary>A vehicle whose crew shoots from its seats (a Rival chase car): a member's line of sight and its rounds start inside the hull, which must not stop them (<see cref="MountAt"/>).</summary>
    public void Mount(ServerCar car)
    {
        if (_mounts.Contains(car)) return;
        _mounts.Add(car); _mountIds.Add(car.Id);
    }

    /// <summary>The vehicle no longer carries a crew.</summary>
    public void Unmount(ServerCar car)
    {
        if (_mounts.Remove(car)) _mountIds.Remove(car.Id);
    }

    /// <summary>The mounted vehicle a ray that starts at <paramref name="from"/> starts in, or null: its own hull is not in the way of a rider's sight or round.</summary>
    ServerCar? MountAt(Vector3 from)
    {
        foreach (var car in _mounts)
        {
            if (car.Destroyed) continue;
            var p = car.GlobalPosition;
            var reach = car.HalfLength + 0.5f;
            if (MathF.Abs(p.X - from.X) <= reach && MathF.Abs(p.Z - from.Z) <= reach && from.Y > p.Y - 0.5f && from.Y < p.Y + car.Def.Height + 1f) return car;
        }
        return null;
    }

    /// <summary>
    /// Where a round from <paramref name="from"/> stops: the first wall or hull (<see cref="GangRound"/>: a rider's round passes its own vehicle and every other that carries a crew). <see cref="_hulls"/> holds the hulls of this shot.
    /// </summary>
    float RoundStop(Vector3 from, Vector3 dir, float range, ServerCar? own, out ServerCar? car)
    {
        var wall = _shot.Query.Wall(from, dir, range);
        var stop = GangRound.Stop(new NVec3(from.X, from.Y, from.Z), new NVec3(dir.X, dir.Y, dir.Z), range, wall, _hulls, own?.Id, _mountIds);
        car = stop.Hull is { } id ? _shot.CarOf(id) : null;
        return stop.Distance;
    }

    /// <summary>
    /// A Gunman's round, or a shotgun's pellets round its line (the weapon's pellet_spread): each flies to the first wall or hull, or the first living player on it (where they stand now: no rewind), whom it
    /// hurts by the weapon's damage with falloff times the Gunman's damage_scale. Everyone sees and hears it as a Shot (a PelletFan for a shotgun) from no player; a round that stops on a wall puffs dust.
    /// </summary>
    public void Shoot(GangShot s)
    {
        if (_ctx.Match.Phase != MatchPhase.Running || !_weaponIndex.TryGetValue(s.Weapon.Weapon, out var wi)) return;
        var w = _weapons.Weapons[wi];
        var range = MathF.Min(s.RangeM * 1.5f, w.Range > 0 ? w.Range : ShotServer.MaxRange); // a little past its range: the target may step back as the round leaves
        var from = new Vector3(s.From.X, s.From.Y, s.From.Z);
        var own = MountAt(from); // a rider of a moving car shoots out through its own hull, and through the hulls of the other cars that carry a crew
        _hulls.Clear();
        _shot.Hulls(_hulls);
        var targets = _targets.Rent();
        try
        {
            _shot.AddPlayers(targets);
            targets.RemoveAll(_shot.IsArmouredRider); // no round reaches an armoured seat (ADR 0035)
            if (w.Pellets > 1)
            {
                var dirs = HitMath.PelletDirections(s.Dir, w.PelletSpread, w.Pellets, s.Seed);
                var walls = new float[dirs.Length];
                var cars = new ServerCar?[dirs.Length];
                for (var i = 0; i < dirs.Length; i++) walls[i] = RoundStop(from, new Vector3(dirs[i].X, dirs[i].Y, dirs[i].Z), range, own, out cars[i]);
                _ctx.Broadcast(EventKind.PelletFan, WeaponCodec.Encode(new PelletFanMsg(0, (byte)wi, s.From, s.Dir, s.Seed, w.PelletSpread)));
                foreach (var (id, t) in HitMath.ResolvePellets(w, s.From, dirs, range, walls, targets, s.MemberHitId))
                    if (_ctx.Players.ContainsKey(id)) _deal(s.MemberHitId, SpecialWeapons.Gang, id, t.Damage * s.Weapon.DamageScale, t.Head, false);
                var carDamage = new Dictionary<ServerCar, float>(); // a pellet that stopped on a vehicle's hull (no player nearer on its line) hurts it, summed per vehicle
                for (var i = 0; i < dirs.Length; i++)
                {
                    if (cars[i] is not { } car || walls[i] > range || HitMath.Raycast(s.From, dirs[i], range, walls[i], targets, s.MemberHitId) != null) continue;
                    var damage = HitMath.PelletDamage(w, walls[i], false) * s.Weapon.DamageScale;
                    carDamage[car] = carDamage.GetValueOrDefault(car) + damage;
                    _glass.Shot(car, from, new Vector3(dirs[i].X, dirs[i].Y, dirs[i].Z), walls[i], damage);
                }
                foreach (var (car, amount) in carDamage) _carDamage.Bullet(car, amount, 0, SpecialWeapons.Gang);
                return;
            }
            var wall = RoundStop(from, new Vector3(s.Dir.X, s.Dir.Y, s.Dir.Z), range, own, out var shotCar);
            var hit = HitMath.Raycast(s.From, s.Dir, range, wall, targets, s.MemberHitId);
            var end = s.From + s.Dir * (hit?.Distance ?? MathF.Min(wall, range));
            _ctx.Broadcast(EventKind.Shot, WeaponCodec.Encode(new ShotMsg(0, (byte)wi, s.From, end)));
            if (hit is { } h && _ctx.Players.ContainsKey(h.Id))
            {
                _impacts.Broadcast(end, ImpactSize.Puff, body: true);
                _deal(s.MemberHitId, SpecialWeapons.Gang, h.Id, HitMath.HitscanDamage(w, h.Distance, h.Head) * s.Weapon.DamageScale, h.Head, false);
            }
            else if (shotCar != null && wall <= range)
            {
                // the round stopped on a vehicle's hull: it hurts it as a player's round of the weapon does (the same damage, its pane too), times the Gunman's damage_scale and vehicles.json bullet_damage_multiplier
                var damage = HitMath.HitscanDamage(w, wall, false) * s.Weapon.DamageScale;
                _carDamage.Bullet(shotCar, damage, 0, SpecialWeapons.Gang);
                _glass.Shot(shotCar, from, new Vector3(s.Dir.X, s.Dir.Y, s.Dir.Z), wall, damage);
            }
            else if (wall <= range) _impacts.BulletPuff(end);
        }
        finally { _targets.Return(targets); }
    }

    // ---- the tick ----

    /// <summary>Once a tick before the entities tick: the living players the members look at, then who each fights.</summary>
    public void Prepare()
    {
        var dt = (float)(_ctx.Now - _preparedAt);
        _preparedAt = _ctx.Now;
        if (Roster.Gangs.Count == 0) return;
        _players.Clear();
        foreach (var c in _ctx.Players.Values)
            if (_ctx.IsAlive(c.Id)) _players.Add(new GangPlayer(c.Id, c.Body.GlobalPosition.ToNumerics(), c.Car == null));
        Roster.Update(dt);
    }

    /// <summary>
    /// Once a tick after the entities tick: waiting route requests go to the worker, the oldest first, while the Gang's share of it and the worker have room. The members ask for routes when they chase,
    /// follow or wander; a request goes out once, its answer is applied through <see cref="DeliverRoute"/>.
    /// </summary>
    public void RouteTick()
    {
        if (_nav() is not { } nav || !Roster.AnyRouteWaiting) return;
        var sent = Roster.RoutesInFlight;
        while (sent < MaxRoutesInFlight && nav.HasRoom)
        {
            var ticket = _nextTicket();
            if (!Roster.BeginRoute(ticket, out var from, out var to)) break;
            nav.Submit(ticket, new Vector3(from.X, from.Y, from.Z), new Vector3(to.X, to.Y, to.Z));
            sent++;
        }
    }

    /// <summary>The route worker's answer for <paramref name="ticket"/>: true when a member of the Gang asked for it.</summary>
    public bool DeliverRoute(long ticket, IReadOnlyList<NVec3> route) => Roster.DeliverRoute(ticket, route);

    /// <summary>The living members as hit targets, only while damage can apply to them (<see cref="MatchSim.ApplyDamage"/> ignores them outside Running). With a view tick: where the shooter saw them.</summary>
    public void AddTargets(List<HitTarget> into, uint? view = null)
    {
        if (_ctx.Match.Phase == MatchPhase.Running) Roster.AddTargets(into, view);
    }

    public void RecordPoses(uint tick) => Roster.RecordPoses(tick);

    // ---- what a kill does ----

    /// <summary>A member was killed by a hit: a little XP for the killer (its gang's kill_xp: none for the Rival crew), no kill feed, streak or assist; a speaker it carried lies where it fell.</summary>
    void OnKilled(GangKilled k)
    {
        GD.Print($"server: #{k.Killer} killed a {k.Kind} (hit id {k.HitId})");
        DropSpeaker(k);
        var xp = Roster.GangOf(k.HitId)?.Config.KillXp ?? _data.Gang.KillXp;
        if (k.Killer != 0 && xp > 0 && _scores(k.Killer)) _awardXp(new[] { new XpGrant(k.Killer, xp, XpReason.GangKill) });
    }

    void DropSpeaker(GangKilled k)
    {
        GangMember? member = null;
        foreach (var g in Roster.Gangs)
            foreach (var m in g.Members) if (m.HitId == k.HitId) member = m;
        if (member is not { Speaker: true }) return;
        var item = _items.IndexOf(SpeakerItemId);
        if (item < 0)
        {
            if (!_speakerItemWarned) GD.PrintErr($"server: items.json has no '{SpeakerItemId}': a killed member's speaker is not dropped");
            _speakerItemWarned = true;
            return;
        }
        if (_corpses.Drop(0, k.Where, new ItemStack(item)) == null) GD.PrintErr("server: the world refused the speaker item (entity cap)");
    }

    // ---- audio ----

    /// <summary>Once a tick: the voice lines of the Raids (the Rival crew has none). Humans only listen: bots do not hear a thing.</summary>
    public void AudioTick(float dt)
    {
        if (Roster.Gangs.Count == 0) return;
        _audio ??= new GangAudio(_data.Audio, System.Environment.TickCount);
        _listeners.Clear();
        foreach (var c in _ctx.Players.Values)
            if (c is { IsBot: false, Joined: true } && _ctx.IsAlive(c.Id)) _listeners.Add(new GangPlayer(c.Id, c.Body.GlobalPosition.ToNumerics(), c.Car == null));
        _voiced.Clear();
        foreach (var g in Roster.Gangs) if (g.Crew == GangCrew.Raid) _voiced.Add(g);
        _audio.Update(dt, _voiced, _listeners, Say);
    }

    void Say(GangMember m, byte line)
    {
        foreach (var (id, entity) in _ctx.Match.Entities.OfType(GangRules.TypeId))
            if (entity == m) { _ctx.Broadcast(EventKind.ThiefVoice, GangVoiceCodec.Encode(id, line)); return; }
    }
}
