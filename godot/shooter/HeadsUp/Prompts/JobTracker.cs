using Godot;
using Drycastle.Shooter.Maps;
using Drycastle.Shooter.Menu;
using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Jobs;
using Drycastle.ShooterCore.Menu;

namespace Drycastle.Shooter.HeadsUp.Prompts;

/// <summary>
/// The Job tracker (docs/cartel.md "Contact": the HUD tracker; #494): a panel at the right edge under the kill feed, only for the players of a Job's party (only they get its
/// messages). Running: the kind, the tier, the objective line, the time left and who leads; its marker goes to the minimap, the tactical map and the world
/// (<see cref="MapMarkers.Job"/>). At the end the same panel is the end card: done or failed in words, the kind and tier, and the Respect paid or the reason. One fact per line;
/// the text is rewritten only when the tracker's revision, the whole second or the interact key's binding changes, so nothing allocates per frame. The server writes the key as
/// <see cref="JobText.Interact"/>; the line shows the bound key's name. A party member who earns nothing reads why (not in the Cartel: join it; a guest: no saved profile).
/// </summary>
internal sealed class JobTracker
{
    readonly PanelContainer _panel;
    readonly Label _title, _tier, _line, _time, _note;
    readonly MapMarkers _markers;
    CartelTracker? _tracker;
    Func<string, string> _kindName = k => k;
    Func<ushort, string> _nameOf = id => id.ToString();
    int _revision = -1, _second = -1;
    /// <summary>The interact key's binding the line was last written with, and its name.</summary>
    Binding? _interactKey;
    string _interactName = "";
    Binding? _throwKey;
    string _throwName = "";

    public JobTracker(Control root, MapMarkers markers)
    {
        _markers = markers;
        _panel = HudUi.Place(HudUi.P("HudPanel"), 1, 0, -ShooterTheme.Margin, ShooterTheme.Margin + 36 * ShooterTheme.Space, ShooterTheme.JobTrackerW, 0);
        _panel.Visible = false;
        root.AddChild(_panel);
        var v = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        v.AddThemeConstantOverride("separation", ShooterTheme.Space / 2);
        _panel.AddChild(v);
        v.AddChild(_title = HudUi.L("", "HudHead"));
        v.AddChild(_tier = HudUi.L("", "HudSmall"));
        v.AddChild(_line = HudUi.L("", "HudTextSmall"));
        _line.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        v.AddChild(_time = HudUi.L("", "HudAccent"));
        v.AddChild(_note = HudUi.L("", "HudSmall"));
        _note.AutowrapMode = TextServer.AutowrapMode.WordSmart;
    }

    /// <param name="kindName">What players read for a kind id.</param>
    /// <param name="nameOf">A player's name (the leader line).</param>
    public void Attach(CartelTracker tracker, Func<string, string> kindName, Func<ushort, string> nameOf)
    {
        _tracker = tracker; _kindName = kindName; _nameOf = nameOf;
        _revision = -1;
    }

    /// <summary>Every frame: the marker follows the tracker; the text is rewritten when it changed.</summary>
    public void Tick()
    {
        if (_tracker is not { } t) return;
        _markers.Job = t.Marker;
        var key = GameSettings.Current.Keys.Get("interact");
        if (_interactKey != key) { _interactKey = key; _interactName = InputBindings.Name(key); _revision = -1; } // rebound: the line names the new key
        var throwKey = GameSettings.Current.Keys.Get("duffel_throw");
        if (_throwKey != throwKey) { _throwKey = throwKey; _throwName = InputBindings.Name(throwKey); _revision = -1; }
        if (t.Revision != _revision) { _revision = t.Revision; _second = -1; Write(t); }
        if (t.Phase != TrackerPhase.Running) return;
        var s = (int)MathF.Ceiling(t.SecondsLeft);
        if (s == _second) return;
        _second = s;
        HudUi.Set(_time, Strings.F("cartel.track.left", CartelTracker.Clock(t.SecondsLeft)));
    }

    void Write(CartelTracker t)
    {
        _panel.Visible = t.Phase != TrackerPhase.Hidden;
        if (t.Phase == TrackerPhase.Hidden) return;
        var j = t.Job;
        var kind = _kindName(j.Kind);
        if (t.Phase == TrackerPhase.Running)
        {
            HudUi.Tint(_title, null);
            HudUi.Set(_title, kind);
            HudUi.Set(_tier, Strings.F("cartel.track.tier", j.Tier));
            HudUi.Set(_line, JobText.Fill(t.Line, _interactName, _throwName));
            _line.Visible = t.Line.Length > 0; // empty: the kind's name says it all
            _time.Visible = true;
            HudUi.Set(_note, JobDone.WhyNoRespect(j.Flags) is { } why and not NoRespectReason.Sandbox ? Strings.T(CartelWords.NoRespect.Key(why)) // sandbox: the panel says it, the leader line stays
                : j.Flags.HasFlag(CartelJobFlags.Leader) ? Strings.T("cartel.track.lead") : Strings.F("cartel.track.led", _nameOf(j.Leader)));
            return;
        }
        var done = j.State == JobState.Succeeded;
        HudUi.Tint(_title, done ? ShooterTheme.Good : ShooterTheme.Danger); // the words say it too
        HudUi.Set(_title, Strings.T(done ? "cartel.end.done" : "cartel.end.failed"));
        HudUi.Set(_tier, Strings.F("cartel.end.kind", kind, j.Tier));
        HudUi.Set(_line, done ? (j.RespectPaid > 0 ? Strings.F("cartel.end.paid", j.RespectPaid) : Strings.T("cartel.end.unpaid")) : Strings.T(CartelWords.Fail.Key(j.Reason)));
        _line.Visible = true;
        _time.Visible = false;
        HudUi.Set(_note, done && j.RespectPaid == 0 && JobDone.WhyNoRespect(j.Flags) is { } unpaid ? Strings.T(CartelWords.NoRespect.Key(unpaid)) : "");
    }
}
