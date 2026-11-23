using Drycastle.ShooterCore.Net;

namespace Drycastle.ShooterCore.Hunts;

/// <summary>
/// The client's side of the Hunter's Log queries (docs/hunters-log.md "Queries"), Godot-free: the three requests, each with its <see cref="HuntLoader{T}"/>
/// (stale answers dropped by the echoed request id, a timeout), the table's query state, who the asker is, and the players seen so far. The network sends
/// through <c>send</c> and feeds the answers back; the screen reads the loaders and redraws on <see cref="Changed"/>.
/// </summary>
public sealed class HuntLogData
{
    readonly Action<EventKind, byte[]> _send;

    public HuntLoader<PlayerLogView> Log { get; } = new();
    public HuntLoader<RecordsAnswer> Records { get; } = new();
    public HuntLoader<HuntsAnswer> Hunts { get; } = new();
    public HuntTableState Table { get; } = new();
    public HuntPeople People { get; } = new();

    /// <summary>The asker's own player id in the log (0 for a guest); known after the first answer.</summary>
    public long ViewerId { get; private set; }
    public bool ViewerKnown { get; private set; }

    /// <summary>Whose log <see cref="Log"/> holds or waits for: 0 the asker's own.</summary>
    public long LogSubject { get; private set; }

    public event Action? Changed;

    public HuntLogData(Action<EventKind, byte[]> send) => _send = send;

    public void RequestLog(long subject)
    {
        if (subject != LogSubject) Log.Clear(); // another player's log is not shown in place of the last one while it loads
        LogSubject = subject;
        _send(EventKind.PlayerLogRequest, HuntEvents.EncodePlayerRequest(new PlayerLogRequest(Log.Begin(), subject)));
        Changed?.Invoke();
    }

    public void RequestRecords()
    {
        _send(EventKind.RecordsRequest, HuntEvents.EncodeRecordsRequest(Records.Begin()));
        Changed?.Invoke();
    }

    /// <summary>Asks for the page the table's state names. Different filters or sort than the rows held: the held rows are dropped first (the caller says with <paramref name="drop"/>).</summary>
    public void RequestHunts(bool drop = true)
    {
        if (drop) Hunts.Clear();
        _send(EventKind.HuntsRequest, HuntEvents.EncodeRequest(Table.ToRequest(Hunts.Begin())));
        Changed?.Invoke();
    }

    public void OnLog(PlayerLogAnswer a)
    {
        Learn(a.ViewerId);
        if (!Log.Accept(a.Req, a.Status, a.Log)) return;
        if (a.Log is { } l)
        {
            People.Learn(l.PlayerId, l.Name);
            People.Learn(l.Heaviest); People.Learn(l.Longest);
        }
        Changed?.Invoke();
    }

    public void OnRecords(RecordsAnswer a)
    {
        Learn(a.ViewerId);
        if (!Records.Accept(a.Req, a.Status, a.Status == HuntStatus.Ok ? a : null)) return;
        foreach (var c in a.Records) { People.Learn(c.Heaviest); People.Learn(c.Longest); People.Learn(c.First); }
        Changed?.Invoke();
    }

    public void OnPage(HuntsAnswer a)
    {
        Learn(a.ViewerId);
        if (!Hunts.Accept(a.Req, a.Status, a.Status == HuntStatus.Ok ? a : null)) return;
        foreach (var h in a.Rows) People.Learn(h);
        // the book shrank under the player, or a stale page number: go to the last page that exists
        if (a.Status == HuntStatus.Ok && a.Rows.Count == 0 && a.Total > 0 && Table.Page >= HuntTableState.PageCount(a.Total))
        {
            Table.SetPage(HuntTableState.PageCount(a.Total) - 1);
            RequestHunts();
            return;
        }
        Changed?.Invoke();
    }

    /// <summary>Per frame while the screen shows: a request nobody answers fails after its timeout.</summary>
    public void Tick(float dt)
    {
        var failed = Log.Tick(dt);
        failed |= Records.Tick(dt);
        failed |= Hunts.Tick(dt);
        if (failed) Changed?.Invoke();
    }

    void Learn(long viewer)
    {
        ViewerId = viewer;
        ViewerKnown = true;
    }
}
