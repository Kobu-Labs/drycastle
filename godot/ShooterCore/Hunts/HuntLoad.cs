namespace Drycastle.ShooterCore;

/// <summary>Where one query of the Hunter's Log is: not asked, waiting, answered, failed to read, or no such player.</summary>
public enum HuntLoad { Idle, Loading, Ready, Failed, Missing }

/// <summary>The spec of every <see cref="HuntLoader{T}"/> (one machine per query kind; the generic class cannot hold a static spec the catalog finds).</summary>
public static class HuntLoadMachine
{
    public static readonly StateMachineSpec<HuntLoad> Spec = StateMachineSpec<HuntLoad>.Define("HuntersLog.Load", "One query of the Hunter's Log: asked, answered, failed (Retry) or no such player.", HuntLoad.Idle)
        .Allow(HuntLoad.Idle, HuntLoad.Loading)
        .Allow(HuntLoad.Loading, HuntLoad.Ready, HuntLoad.Failed, HuntLoad.Missing)
        .Allow(HuntLoad.Ready, HuntLoad.Loading)
        .Allow(HuntLoad.Failed, HuntLoad.Loading)
        .Allow(HuntLoad.Missing, HuntLoad.Loading)
        .Note(HuntLoad.Loading, "Waiting for the answer carrying this request id; an answer to an older request is dropped. Gives up (Failed) after the timeout.")
        .Note(HuntLoad.Failed, "The server could not read it, or never answered: the screen offers Retry.")
        .Note(HuntLoad.Missing, "The server knows no such player (a guest's own log, or a player that was never logged).")
        .Build();
}

/// <summary>
/// One query of the Hunter's Log and its answer: the request id a stale answer is dropped by, the load phase with a give-up clock, and the last data
/// (kept while a newer request is out, so a screen can show it rather than a spinner).
/// </summary>
public sealed class HuntLoader<T> where T : class
{
    public const float TimeoutSeconds = 10;

    readonly StateMachine<HuntLoad> _fsm = new(HuntLoadMachine.Spec);
    ushort _req;

    public HuntLoad Phase => _fsm.Current;
    public T? Data { get; private set; }
    public bool HasData => Data != null;
    public ushort Req => _req;

    /// <summary>Starts a request: returns its id (never 0), which the answer must echo. A request already out is replaced (its answer is now stale).</summary>
    public ushort Begin()
    {
        _req = (ushort)(_req == ushort.MaxValue ? 1 : _req + 1);
        if (!_fsm.Enter(HuntLoad.Loading, TimeoutSeconds)) _fsm.Restart(TimeoutSeconds);
        return _req;
    }

    /// <summary>Takes an answer. False (dropped) when it echoes an older request or none is out.</summary>
    public bool Accept(ushort req, HuntStatus status, T? data)
    {
        if (req != _req || !_fsm.Is(HuntLoad.Loading)) return false;
        switch (status)
        {
            case HuntStatus.Ok when data != null: Data = data; _fsm.Enter(HuntLoad.Ready); break;
            case HuntStatus.Unknown: Data = null; _fsm.Enter(HuntLoad.Missing); break;
            default: _fsm.Enter(HuntLoad.Failed); break;
        }
        return true;
    }

    /// <summary>Per frame while the screen shows: a request that is never answered fails after <see cref="TimeoutSeconds"/>. True when it just did.</summary>
    public bool Tick(float dt)
    {
        if (!_fsm.Is(HuntLoad.Loading) || !_fsm.Tick(dt)) return false;
        _fsm.Enter(HuntLoad.Failed);
        return true;
    }

    /// <summary>Forgets the data and the request (a different question is about to be asked).</summary>
    public void Clear()
    {
        Data = null;
        _req = (ushort)(_req == ushort.MaxValue ? 1 : _req + 1); // an answer to the old question is stale
        _fsm.Reset(HuntLoad.Idle);
    }

    /// <summary>What the screen draws: the data (also a stale copy while a newer request is out), a spinner, or an error / missing box.</summary>
    public LoadView Show => Phase switch
    {
        HuntLoad.Failed => LoadView.Error,
        HuntLoad.Missing => LoadView.Missing,
        _ => HasData ? LoadView.Data : LoadView.Spinner,
    };

}

/// <summary>What a pane of the Hunter's Log draws for a query (<see cref="HuntLoader{T}.Show"/>).</summary>
public enum LoadView { Spinner, Data, Error, Missing }
