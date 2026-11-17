namespace Drycastle.ShooterCore;

/// <summary>Which page of the Hunter's Log screen shows. MyLog and PlayerLog are the first tab (the asker's own log, or another player's).</summary>
public enum HuntLogPage { MyLog, PlayerLog, Records, AllHunts }

/// <summary>What Esc / Back did: closed the screen, or went one level up to My Log.</summary>
public enum HuntLogBack { Close, ToMyLog }

/// <summary>
/// Where the Hunter's Log screen is (docs/hunters-log.md "UI"): the three tabs, and another player's log in place of My Log. A player's log keeps its
/// place on the first tab while another tab shows; Esc on it goes up to My Log, Esc anywhere else closes the screen. Pure: the screen asks, then draws.
/// </summary>
public sealed class HuntLogNav
{
    public static readonly StateMachineSpec<HuntLogPage> Spec = StateMachineSpec<HuntLogPage>.Define("HuntersLog.Page", "Which page of the Hunter's Log shows: the first tab (own log, or another player's), Records, All hunts.", HuntLogPage.MyLog)
        .Allow(HuntLogPage.MyLog, HuntLogPage.PlayerLog, HuntLogPage.Records, HuntLogPage.AllHunts)
        .Allow(HuntLogPage.PlayerLog, HuntLogPage.MyLog, HuntLogPage.PlayerLog, HuntLogPage.Records, HuntLogPage.AllHunts)
        .Allow(HuntLogPage.Records, HuntLogPage.MyLog, HuntLogPage.PlayerLog, HuntLogPage.AllHunts)
        .Allow(HuntLogPage.AllHunts, HuntLogPage.MyLog, HuntLogPage.PlayerLog, HuntLogPage.Records)
        .Note(HuntLogPage.MyLog, "The default, and where a screen opens. The asker's own log.")
        .Note(HuntLogPage.PlayerLog, "Another player's log, opened from a name. Esc goes up to My Log; another tab keeps it parked on the first tab.")
        .Build();

    readonly StateMachine<HuntLogPage> _fsm = new(Spec);

    public HuntLogPage Page => _fsm.Current;

    /// <summary>The player whose log the first tab shows: 0 is the asker's own.</summary>
    public long Subject { get; private set; }

    public string SubjectName { get; private set; } = "";

    public int TabIndex => Page switch { HuntLogPage.MyLog or HuntLogPage.PlayerLog => 0, HuntLogPage.Records => 1, _ => 2 };

    public bool ViewingOther => Subject != 0;

    /// <summary>Back to the default page, the asker's own log (the screen opens like this).</summary>
    public void Reset()
    {
        Subject = 0; SubjectName = "";
        _fsm.Reset(HuntLogPage.MyLog);
    }

    /// <summary>Tab 0 shows the own log or the parked player's; 1 Records; 2 All hunts.</summary>
    public void ShowTab(int index)
    {
        _fsm.Enter(index switch { 0 => ViewingOther ? HuntLogPage.PlayerLog : HuntLogPage.MyLog, 1 => HuntLogPage.Records, _ => HuntLogPage.AllHunts });
    }

    public void OpenOwn()
    {
        Subject = 0; SubjectName = "";
        _fsm.Enter(HuntLogPage.MyLog);
    }

    public void OpenPlayer(long id, string name)
    {
        if (id == 0) { OpenOwn(); return; }
        Subject = id; SubjectName = name;
        _fsm.Enter(HuntLogPage.PlayerLog);
    }

    /// <summary>Esc or a Back button: a player's log goes up to My Log, anything else closes.</summary>
    public HuntLogBack Back()
    {
        if (Page != HuntLogPage.PlayerLog) return HuntLogBack.Close;
        OpenOwn();
        return HuntLogBack.ToMyLog;
    }
}
