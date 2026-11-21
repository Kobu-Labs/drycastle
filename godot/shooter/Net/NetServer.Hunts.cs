using Godot;
using Drycastle.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

/// <summary>
/// The Hunter's Log on the server (#336, docs/hunters-log.md). A player's kill of a weighed wild animal, and each credited player of a won Bigfoot
/// Event, is a <see cref="HuntRecord"/>: added to the in-memory <see cref="HuntBook"/> (so first-times, records, the records card and the Life List
/// are answered without the database) and queued to the store. The book is replayed from the store at start; a hunt made before it is read waits
/// for it. Guests, bots and sandbox players are not logged. A killer's longest kill of another player (#430, <see cref="PlayerKillBook"/>) is kept beside, loaded and queried with the hunts. The three queries are answered redacted for the asker (<see cref="HuntRedaction"/>); only
/// the table page reads the store, off the tick, and is answered on a later tick. Announcements are sent to everyone, redacted per recipient.
/// </summary>
public partial class NetServer
{
    /// <summary>Why a wild animal is dying, set by <see cref="Deal"/> (and the MOAB) around the one damage call so the keeper's Killed event can tell the weapon, how far the shot flew and where it entered.</summary>
    readonly record struct WildHit(byte Weapon, KillCause Cause, float? Distance, HuntShotMsg? Shot);

    WildHit? _wildHit;

    /// <summary>null until the store has been read (or at once with no store). A hunt that happens before waits in <see cref="_huntsEarly"/>.</summary>
    HuntBook? _huntBook;
    Task<IReadOnlyList<HuntRow>>? _huntLoad;
    Task<IReadOnlyList<PlayerKillRow>>? _killLoad;
    /// <summary>Set with <see cref="_huntBook"/>; a kill that happens before waits in <see cref="_killsEarly"/>.</summary>
    PlayerKillBook? _killBook;
    readonly List<PlayerKillRecord> _killsEarly = new();
    double _huntRetryAt;
    readonly List<HuntRecord> _huntsEarly = new();

    /// <summary>A table page whose read is still running. Only the task crosses threads; the answer is built on the tick.</summary>
    readonly List<(Task<HuntPage> Work, Client C, HuntsRequest Req)> _huntPages = new();

    const int MaxHuntPagesPerClient = 4;
    const double HuntLoadRetrySeconds = 5;

    /// <summary>Once a tick: loads the book, and answers the table pages whose read finished.</summary>
    void HuntTick()
    {
        if (_huntBook == null) LoadHuntBook();
        for (var i = _huntPages.Count - 1; i >= 0; i--)
        {
            var (work, c, req) = _huntPages[i];
            if (!work.IsCompleted) continue;
            _huntPages.RemoveAt(i);
            if (c.Peer is not { } peer || !_clients.ContainsKey(peer) || _huntBook is not { } book) continue;
            var viewer = book.CaughtBy(ViewerId(c));
            HuntsAnswer answer;
            if (work.IsCompletedSuccessfully)
            {
                var rows = work.Result.Rows.Select(r => HuntRedaction.View(r.Hunt, book.NameOf(r.Hunt.PlayerId) is { Length: > 0 } n ? n : r.Hunter, viewer)).ToList();
                answer = new HuntsAnswer(req.Req, HuntStatus.Ok, ViewerId(c), req.Page, (uint)work.Result.Total, rows);
            }
            else
            {
                GD.PrintErr($"server: hunt page for {c.Name} failed: {work.Exception?.GetBaseException().Message}");
                answer = new HuntsAnswer(req.Req, HuntStatus.Error, ViewerId(c), req.Page, 0, Array.Empty<HuntView>());
            }
            Send(c, new EventMsg((byte)EventKind.HuntsPage, HuntEvents.EncodePage(answer)));
        }
    }

    void LoadHuntBook()
    {
        if (_huntLoad == null)
        {
            if (_elapsed < _huntRetryAt) return;
            if (Progression == null) { _huntBook = new HuntBook(HuntTierOf); _killBook = new PlayerKillBook(); return; } // no store: every player is a guest, nothing is logged
            _huntLoad = Progression.Hunts.GetAllAsync();
            _killLoad = Progression.Hunts.GetPlayerKillsAsync();
            return;
        }
        if (!_huntLoad.IsCompleted || !_killLoad!.IsCompleted) return;
        if (!_huntLoad.IsCompletedSuccessfully || !_killLoad.IsCompletedSuccessfully)
        {
            GD.PrintErr($"server: the hunter's log could not be read, retrying: {(_huntLoad.Exception ?? _killLoad.Exception)?.GetBaseException().Message}");
            _huntLoad = null;
            _killLoad = null;
            _huntRetryAt = _elapsed + HuntLoadRetrySeconds;
            return;
        }
        var rows = _huntLoad.Result;
        var book = HuntBook.From(rows, HuntTierOf);
        var killRows = _killLoad.Result;
        var kills = PlayerKillBook.From(killRows);
        _huntLoad = null;
        _killLoad = null;
        _huntBook = book;
        _killBook = kills;
        foreach (var r in killRows) { book.SetName(r.Kill.KillerId, r.Killer); book.SetName(r.Kill.VictimId, r.Victim); } // a killer with no hunt still has a log to open
        foreach (var c in _clients.Values)
            if (c.Profile is { Persist: true } p) SetLogName(p.DbId, c.Name);
        foreach (var h in _huntsEarly) CommitHunt(book, h, announce: false);
        _huntsEarly.Clear();
        foreach (var k in _killsEarly) CommitPlayerKill(kills, k, announce: false);
        _killsEarly.Clear();
        GD.Print($"server: hunter's log loaded, {rows.Count} hunts replayed");
    }

    /// <summary>A stored player's name for the log's hunters, killers and victims (a name read before the books are set when they load).</summary>
    void SetLogName(long playerId, string name)
    {
        _huntBook?.SetName(playerId, name);
        _killBook?.SetName(playerId, name);
    }

    /// <summary>The rarity of a hunt's weight by the ranges of animals.json as they are now (a changed range re-tiers the old hunts at the next start).</summary>
    HuntTier HuntTierOf(HuntRecord h) => HuntRarity.Of(AnimalData, h);

    /// <summary>The player id a viewer is known by in the log: their stored profile's, 0 for a guest.</summary>
    static long ViewerId(Client c) => c.Profile is { Persist: true } p ? p.DbId : 0;

    // ---- recording ----

    /// <summary>The Killed event of the wild animal keeper: a player killed a weighed animal (or the Unicorn, or burned the Phoenix with the second hit), with the weapon <see cref="Deal"/> noted.</summary>
    void OnWildKilled(Animal a, ushort by)
    {
        if (_wildHit is not { } hit || !_byId.TryGetValue(by, out var c) || c.IsBot || !Scores(by) || c.Profile is not { Persist: true } p) return;
        var weapon = hit.Weapon == SpecialWeapons.Vehicle && hit.Cause == KillCause.Roadkill ? HuntWeapons.RunOver : WeaponKey(hit.Weapon);
        var distance = hit.Distance ?? NVec3.Distance(N(c.Body.GlobalPosition), a.Position);
        var variant = a.Variant >= 1 && a.Variant <= a.Def.Variants.Count ? a.Def.Variants[a.Variant - 1].Id : "";
        var diagram = hit.Shot is { } shot ? HitDiagram.From(shot) : (HitDiagram?)null;
        var h = HuntRecord.ForAnimal(p.DbId, a.Def.Id, variant, a.WeightKg, distance, weapon, diagram, a.Position.X, a.Position.Y, a.Position.Z, DateTime.UtcNow)
            ?? (Mysteries.IsAnimal(a.Def.Id) ? HuntRecord.ForMysteryAnimal(p.DbId, a.Def.Id, distance, weapon, diagram, a.Position.X, a.Position.Y, a.Position.Z, DateTime.UtcNow) : null); // a mystery that is an animal (Unicorn, Phoenix): no weight
        if (h != null) RecordHunt(h);
    }

    /// <summary>A mystery creature's Event was won: every credited player who has a stored profile has caught it.</summary>
    void RecordMystery(IEnumerable<ushort> credited, string mystery, NVec3 at)
    {
        foreach (var id in credited)
            if (_byId.TryGetValue(id, out var c) && !c.IsBot && Scores(id) && c.Profile is { Persist: true } p)
                RecordHunt(HuntRecord.ForMystery(p.DbId, mystery, at.X, at.Y, at.Z, DateTime.UtcNow));
    }

    void RecordHunt(HuntRecord h)
    {
        if (Progression == null) return;
        if (_huntBook is not { } book) { _huntsEarly.Add(h); return; }
        CommitHunt(book, h, announce: true);
    }

    void CommitHunt(HuntBook book, HuntRecord h, bool announce)
    {
        var outcome = book.Add(h);
        _ = Progression!.Hunts.AppendAsync(h); // queued behind the tick: never waited for
        GD.Print($"server: hunt: #{h.PlayerId} {h.Exact} {h.Kg?.ToString("F1") ?? "-"} kg, {h.DistanceM?.ToString("F1") ?? "-"} m, {h.Weapon}{(outcome.Announce ? $" (first {outcome.FirstTime}, heaviest {outcome.Heaviest}, longest {outcome.Longest})" : "")}");
        if (announce && outcome.Announce) AnnounceHunt(book, h, outcome);
    }

    /// <summary>One line for a first and/or a record, to everyone; each recipient gets the hunt redacted for what they have caught.</summary>
    void AnnounceHunt(HuntBook book, HuntRecord h, HuntOutcome o)
    {
        var flags = (o.FirstTime ? HuntAnnounceFlags.First : 0) | (o.Heaviest ? HuntAnnounceFlags.Heaviest : 0) | (o.Longest ? HuntAnnounceFlags.Longest : 0);
        foreach (var c in _clients.Values)
        {
            if (!c.Joined || c.IsBot) continue;
            var view = book.View(h, book.CaughtBy(ViewerId(c)))!;
            Send(c, new EventMsg((byte)EventKind.HuntAnnounce, HuntEvents.EncodeAnnounce(new HuntAnnounceMsg(flags, view))));
        }
    }

    /// <summary>
    /// A hit that killed a player (called by <see cref="Deal"/>): a stored player's enemy shot with a hitscan weapon, from <paramref name="distance"/> (the damage calls of grenades, vehicles and
    /// melee pass none), kept when it is the killer's longest and announced when it is the server's. Guests, bots, sandbox players and teammates count for nothing.
    /// </summary>
    void RecordPlayerKill(ushort killerId, ushort victimId, byte weapon, float distance)
    {
        if (!_byId.TryGetValue(killerId, out var killer) || !_byId.TryGetValue(victimId, out var victim) || killer.IsBot || victim.IsBot || !Scores(killerId) || !Scores(victimId)
            || !BotEnemies.IsEnemy(_match, killerId, victimId) || killer.Profile is not { Persist: true } kp || victim.Profile is not { Persist: true } vp) return;
        var at = N(victim.Body.GlobalPosition);
        if (PlayerKillRecord.For(kp.DbId, vp.DbId, distance, WeaponKey(weapon), at.X, at.Y, at.Z, DateTime.UtcNow) is not { } kill) return;
        SetLogName(kp.DbId, killer.Name);
        SetLogName(vp.DbId, victim.Name);
        if (Progression == null) return;
        if (_killBook is not { } book) { _killsEarly.Add(kill); return; }
        CommitPlayerKill(book, kill, announce: true);
    }

    void CommitPlayerKill(PlayerKillBook book, PlayerKillRecord kill, bool announce)
    {
        var outcome = book.Add(kill);
        if (!outcome.Own) return;
        _ = Progression!.Hunts.SavePlayerKillAsync(kill); // queued behind the tick: never waited for
        GD.Print($"server: player kill: #{kill.KillerId} -> #{kill.VictimId} {kill.DistanceM:F1} m, {kill.Weapon}{(outcome.Server ? " (server record)" : "")}");
        if (!announce || !outcome.Server) return;
        var view = book.View(kill)!;
        var hunt = new HuntView(view.KillerId, view.Killer, "", "", "", null, view.DistanceM, view.Weapon, null, view.AtUnix, false); // the announcement's shape: who, how far
        foreach (var c in _clients.Values)
            if (c.Joined && !c.IsBot) Send(c, new EventMsg((byte)EventKind.HuntAnnounce, HuntEvents.EncodeAnnounce(new HuntAnnounceMsg(HuntAnnounceFlags.PlayerKill, hunt))));
    }

    // ---- queries ----

    void OnHuntRequest(Client c, EventMsg m)
    {
        switch ((EventKind)m.Kind)
        {
            case EventKind.HuntsRequest:
                if (HuntEvents.DecodeRequest(m.Payload) is { } hq) OnHuntsRequest(c, hq);
                break;
            case EventKind.RecordsRequest:
                if (HuntEvents.DecodeRecordsRequest(m.Payload) is { } req) OnRecordsRequest(c, req);
                break;
            case EventKind.PlayerLogRequest:
                if (HuntEvents.DecodePlayerRequest(m.Payload) is { } pq) OnPlayerLogRequest(c, pq);
                break;
        }
    }

    void OnHuntsRequest(Client c, HuntsRequest r)
    {
        void Answer(HuntStatus status, uint total = 0) =>
            Send(c, new EventMsg((byte)EventKind.HuntsPage, HuntEvents.EncodePage(new HuntsAnswer(r.Req, status, ViewerId(c), r.Page, total, Array.Empty<HuntView>()))));
        if (_huntBook is not { } book || _huntPages.Count(p => p.C == c) >= MaxHuntPagesPerClient) { Answer(HuntStatus.Error); return; }
        if (Progression == null) { Answer(HuntStatus.Ok); return; }
        var viewer = book.CaughtBy(ViewerId(c));
        var query = new HuntQuery(r.Page, HuntRedaction.FilterFor(r.Species, viewer), r.Weapon, r.PlayerId, r.Sort, r.Descending, HuntRedaction.HiddenFor(viewer));
        _huntPages.Add((Progression.Hunts.PageAsync(query), c, r));
    }

    void OnRecordsRequest(Client c, ushort req)
    {
        var answer = _huntBook is { } book
            ? new RecordsAnswer(req, HuntStatus.Ok, ViewerId(c), book.Records(book.CaughtBy(ViewerId(c))), _killBook?.Server)
            : new RecordsAnswer(req, HuntStatus.Error, ViewerId(c), Array.Empty<SpeciesRecordView>());
        Send(c, new EventMsg((byte)EventKind.Records, HuntEvents.EncodeRecords(answer)));
    }

    void OnPlayerLogRequest(Client c, PlayerLogRequest q)
    {
        var me = ViewerId(c);
        PlayerLogAnswer answer;
        if (_huntBook is not { } book) answer = new PlayerLogAnswer(q.Req, HuntStatus.Error, me, null);
        else if (book.PlayerLog(q.PlayerId == 0 ? me : q.PlayerId, book.CaughtBy(me)) is { } log) answer = new PlayerLogAnswer(q.Req, HuntStatus.Ok, me, log with { Kill = _killBook?.Of(log.PlayerId) });
        else answer = new PlayerLogAnswer(q.Req, HuntStatus.Unknown, me, null);
        Send(c, new EventMsg((byte)EventKind.PlayerLog, HuntEvents.EncodePlayerLog(answer)));
    }
}
