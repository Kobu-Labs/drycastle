using Godot;
using Drycastle.Shooter.Net.Bots;
using Drycastle.Shooter.Net.Server.Combat;
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
    readonly CorpseBook _corpses;
    readonly Func<ushort, bool> _scores;
    readonly Action<IEnumerable<XpGrant>> _awardXp;
    readonly List<GangPlayer> _players = new();
    readonly List<GangPlayer> _listeners = new();
    GangAudio? _audio;
    double _preparedAt;
    bool _speakerItemWarned;

    public GangServer(ServerContext ctx, ThievesFile data, WeaponsFile weapons, ItemsFile items, CorpseBook corpses, DealDamage deal, Func<BotNav?> nav, Func<long> nextTicket,
        Func<IWalkerGround> walk, Func<ushort, bool> scores, Action<IEnumerable<XpGrant>> awardXp)
    {
        _ctx = ctx; _data = data; _items = items; _corpses = corpses; _deal = deal; _nav = nav; _nextTicket = nextTicket; _walk = walk; _scores = scores; _awardXp = awardXp;
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

    /// <summary>A member was killed by a hit: a little XP for the killer, no kill feed, streak or assist; a speaker it carried lies where it fell.</summary>
    void OnKilled(GangKilled k)
    {
        GD.Print($"server: #{k.Killer} killed a {k.Kind} (hit id {k.HitId})");
        DropSpeaker(k);
        if (k.Killer != 0 && _data.Gang.KillXp > 0 && _scores(k.Killer)) _awardXp(new[] { new XpGrant(k.Killer, _data.Gang.KillXp, XpReason.GangKill) });
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

    /// <summary>Once a tick: the voice lines. Humans only listen: bots do not hear a thing.</summary>
    public void AudioTick(float dt)
    {
        if (Roster.Gangs.Count == 0) return;
        _audio ??= new GangAudio(_data.Audio, System.Environment.TickCount);
        _listeners.Clear();
        foreach (var c in _ctx.Players.Values)
            if (c is { IsBot: false, Joined: true } && _ctx.IsAlive(c.Id)) _listeners.Add(new GangPlayer(c.Id, c.Body.GlobalPosition.ToNumerics(), c.Car == null));
        _audio.Update(dt, Roster.Gangs, _listeners, Say);
    }

    void Say(GangMember m, byte line)
    {
        foreach (var (id, entity) in _ctx.Match.Entities.OfType(GangRules.TypeId))
            if (entity == m) { _ctx.Broadcast(EventKind.ThiefVoice, GangVoiceCodec.Encode(id, line)); return; }
    }
}
