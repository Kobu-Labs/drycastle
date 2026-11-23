using Godot;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Events;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Progression;
using Drycastle.ShooterCore.Thieves;
using Drycastle.ShooterCore.Weapons;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

/// <summary>
/// The Gang members of the Thieves event on the server (GLOSSARY "Gang member", "Raid"; docs/events/thieves/02): world entities (type 54) with a pure brain
/// (<see cref="GangMember"/>), owned by <see cref="ThiefGang"/>s that a <see cref="GangRoster"/> keeps. Like the citizens they are not bots, not in the roster and
/// not seen by the bots; unlike them they are enemies: turrets and killstreak weapons shoot them (they are not in <see cref="NonEnemyTargets"/>).
/// <para>
/// They are hit targets: <see cref="LiveTargets"/> lists the living ones under ids of their own (<see cref="GangRules.FirstHitId"/> up), <see cref="RewoundTargets"/> lists them
/// where the shooter's screen showed them (they run, so they have a pose ring each, like the Bigfoot: ADR 0013), and <see cref="MatchSim.ApplyDamage"/> hands those ids to
/// the roster, which counts the hit (a Hit count, not hit points). A kill pays a little XP and is not in the kill feed. A vehicle runs one over = dead
/// (<see cref="RunOverTargets"/>). A member's blow reaches a player through <see cref="Deal"/> under <see cref="SpecialWeapons.Gang"/>.
/// </para>
/// <para>
/// The routes they walk come from the same navigation mesh as the bots'; the route worker finds them, the oldest request first (<see cref="GangsTick"/>), the answers are applied at <see cref="NavDrain"/>.
/// </para>
/// </summary>
public partial class NetServer
{
    GangRoster? _gangs;
    readonly List<GangPlayer> _gangPlayers = new();
    double _gangsAt;

    /// <summary>The Raids of the Thieves event (null until <c>_Ready</c>). Ticket 03 makes one per van; ticket 04 reads their members.</summary>
    public GangRoster? Gangs => _gangs;

    sealed class GangWorld : IGangWorld
    {
        readonly NetServer _s;
        public GangWorld(NetServer server) => _s = server;
        public IReadOnlyList<GangPlayer> Players => _s._gangPlayers;
        public void Hurt(ushort player, float damage, ushort memberHitId) => _s.Deal(memberHitId, SpecialWeapons.Gang, player, damage, false, false);
        public NVec3 Snap(NVec3 p) => _s._nav == null ? p : N(_s._nav.Snap(new Vector3(p.X, p.Y, p.Z)));
        public IWalkerGround? Walk => _s.WalkerWorld;
    }

    /// <summary>Called once from <see cref="InitThieves"/>: the entity type, the roster behind the match's hit routing, the weight of a weapon, the XP of a kill.</summary>
    void InitGangs()
    {
        GangRules.Register(WorldEntityRegistry.Shared);
        _gangs = new GangRoster(ThievesData.Gang, new GangWorld(this), _match.Entities, System.Environment.TickCount) { Log = s => GD.PrintErr($"server: {s}") };
        _gangs.SourceOf = w => BigfootRules.SourceOf(w, Weapons.Weapons.Count, i => Weapons.Weapons[i].Kind == WeaponKind.Projectile);
        _gangs.Killed += OnGangKilled;
        _match.Gang = _gangs;
    }

    /// <summary>A member was killed by a hit: a little XP for the killer, no kill feed, streak or assist.</summary>
    void OnGangKilled(GangKilled k)
    {
        GD.Print($"server: #{k.Killer} killed a {k.Kind} (hit id {k.HitId})");
        GangSpeakerDrop(k); // a speaker it carried lies where it fell (ticket 09)
        if (k.Killer != 0 && ThievesData.Gang.KillXp > 0 && Scores(k.Killer)) AwardXp(new[] { new XpGrant(k.Killer, ThievesData.Gang.KillXp, XpReason.GangKill) });
    }

    /// <summary>Once a tick before the entities tick (next to <see cref="BigfootPrepare"/>): the living players the members look at, then who each fights.</summary>
    void GangsPrepare()
    {
        var dt = (float)(_elapsed - _gangsAt);
        _gangsAt = _elapsed;
        if (_gangs == null || _gangs.Gangs.Count == 0) return;
        _gangPlayers.Clear();
        foreach (var c in _byId.Values)
            if (_match.Get(c.Id)?.Life == PlayerLife.Alive) _gangPlayers.Add(new GangPlayer(c.Id, N(c.Body.GlobalPosition), c.Car == null));
        _gangs.Update(dt);
    }

    /// <summary>
    /// Once a tick after the entities tick: waiting route requests go to the worker, the oldest first, while the Gang's share of it (<see cref="MaxGangRoutesInFlight"/>) and
    /// the worker have room. The members ask for routes when they chase, follow or wander; a request goes out once, its answer is applied at <see cref="NavDrain"/>.
    /// </summary>
    void GangsTick()
    {
        if (_gangs == null || _nav == null || !_gangs.AnyRouteWaiting) return;
        var sent = _gangs.RoutesInFlight;
        while (sent < MaxGangRoutesInFlight && _nav.HasRoom)
        {
            var ticket = NextNavTicket();
            if (!_gangs.BeginRoute(ticket, out var from, out var to)) break;
            _nav.Submit(ticket, new Vector3(from.X, from.Y, from.Z), new Vector3(to.X, to.Y, to.Z));
            sent++;
        }
    }

    /// <summary>The living members as hit targets, only while damage can apply to them (<see cref="MatchSim.ApplyDamage"/> ignores them outside Running). With a view tick: where the shooter saw them.</summary>
    void AddGangTargets(List<HitTarget> into, uint? view = null)
    {
        if (_gangs != null && _match.Phase == MatchPhase.Running) _gangs.AddTargets(into, view);
    }

    void CaptureGangPoses() => _gangs?.RecordPoses(_tick);

    /// <summary>The event ended (by an admin) or a new round began: every Raid goes, vans and members, the dead too.</summary>
    void ClearGangs() { ClearRaids(); _gangs?.Clear(); }
}
