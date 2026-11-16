using Godot;
using Drycastle.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

/// <summary>
/// The Hunter's Log on the server (#336, docs/hunters-log.md). A player's kill of a weighed wild animal, and each credited player of a won Bigfoot
/// Event, is a <see cref="HuntRecord"/>: added to the in-memory <see cref="HuntBook"/> (so first-times, records, the records card and the Life List
/// are answered without the database) and queued to the store. The book is replayed from the store at start; a hunt made before it is read waits
/// for it. Guests, bots and sandbox players are not logged. The three queries are answered redacted for the asker (<see cref="HuntRedaction"/>); only
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
            if (Progression == null) { _huntBook = new HuntBook(); return; } // no store: every player is a guest, nothing is logged
            _huntLoad = Progression.Hunts.GetAllAsync();
            return;
        }
        if (!_huntLoad.IsCompleted) return;
        if (!_huntLoad.IsCompletedSuccessfully)
        {
            GD.PrintErr($"server: the hunter's log could not be read, retrying: {_huntLoad.Exception?.GetBaseException().Message}");
            _huntLoad = null;
            _huntRetryAt = _elapsed + HuntLoadRetrySeconds;
            return;
        }
        var rows = _huntLoad.Result;
        var book = HuntBook.From(rows);
        _huntLoad = null;
        _huntBook = book;
        foreach (var c in _clients.Values)
            if (c.Profile is { Persist: true } p) book.SetName(p.DbId, c.Name);
        foreach (var h in _huntsEarly) CommitHunt(book, h, announce: false);
        _huntsEarly.Clear();
        GD.Print($"server: hunter's log loaded, {rows.Count} hunts replayed");
    }

    /// <summary>The player id a viewer is known by in the log: their stored profile's, 0 for a guest.</summary>
    static long ViewerId(Client c) => c.Profile is { Persist: true } p ? p.DbId : 0;

    // ---- recording ----

    /// <summary>The Killed event of the wild animal keeper: a player killed a weighed animal, with the weapon <see cref="Deal"/> noted.</summary>
    void OnWildKilled(Animal a, ushort by)
    {
        if (_wildHit is not { } hit || !_byId.TryGetValue(by, out var c) || c.IsBot || !Scores(by) || c.Profile is not { Persist: true } p) return;
        var weapon = hit.Weapon == SpecialWeapons.Vehicle && hit.Cause == KillCause.Roadkill ? HuntWeapons.RunOver : WeaponKey(hit.Weapon);
        var distance = hit.Distance ?? NVec3.Distance(N(c.Body.GlobalPosition), a.Position);
        var variant = a.Variant >= 1 && a.Variant <= a.Def.Variants.Count ? a.Def.Variants[a.Variant - 1].Id : "";
        var diagram = hit.Shot is { } shot ? HitDiagram.From(shot) : (HitDiagram?)null;
        if (HuntRecord.ForAnimal(p.DbId, a.Def.Id, variant, a.WeightKg, distance, weapon, diagram, a.Position.X, a.Position.Y, a.Position.Z, DateTime.UtcNow) is { } h) RecordHunt(h);
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
            ? new RecordsAnswer(req, HuntStatus.Ok, ViewerId(c), book.Records(book.CaughtBy(ViewerId(c))))
            : new RecordsAnswer(req, HuntStatus.Error, ViewerId(c), Array.Empty<SpeciesRecordView>());
        Send(c, new EventMsg((byte)EventKind.Records, HuntEvents.EncodeRecords(answer)));
    }

    void OnPlayerLogRequest(Client c, PlayerLogRequest q)
    {
        var me = ViewerId(c);
        PlayerLogAnswer answer;
        if (_huntBook is not { } book) answer = new PlayerLogAnswer(q.Req, HuntStatus.Error, me, null);
        else if (book.PlayerLog(q.PlayerId == 0 ? me : q.PlayerId, book.CaughtBy(me)) is { } log) answer = new PlayerLogAnswer(q.Req, HuntStatus.Ok, me, log);
        else answer = new PlayerLogAnswer(q.Req, HuntStatus.Unknown, me, null);
        Send(c, new EventMsg((byte)EventKind.PlayerLog, HuntEvents.EncodePlayerLog(answer)));
    }
}
