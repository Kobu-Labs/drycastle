using Godot;
using Drycastle.Shooter.Menu;
using Drycastle.ShooterCore.Cartel;

namespace Drycastle.Shooter.HeadsUp.Prompts;

/// <summary>
/// The "Job done" banner (docs/cartel.md "Job done"): when a Cartel Job the player's party ran succeeds, big words in the upper middle of the screen for about <see cref="JobDone.Seconds"/>:
/// "Job done" and under it the Respect this player earned ("+N Respect"), or why they earned none (a guest, not in the Cartel, sandbox) or no second line. On a plate, so it reads over any scene;
/// the title is green and says it in words too. It fades in and out over a fraction of a second (not with reduce motion: it just shows and hides). The text is rewritten only when a new banner starts.
/// </summary>
internal sealed class JobDoneBanner
{
    /// <summary>The fade at either end, seconds.</summary>
    const float FadeSeconds = 0.3f;

    readonly PanelContainer _panel;
    readonly Label _title, _detail;
    JobDone? _done;
    int _revision = -1;

    public JobDoneBanner(Control root)
    {
        _panel = HudUi.Place(HudUi.P("HudPanel"), 0.5f, 0.24f, 0, 0, 0, 0);
        _panel.GrowHorizontal = Control.GrowDirection.Both;
        _panel.Visible = false;
        root.AddChild(_panel);
        var v = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        v.AddThemeConstantOverride("separation", ShooterTheme.Space);
        _panel.AddChild(v);
        v.AddChild(_title = HudUi.L("", "HudDisplay", HorizontalAlignment.Center, ShooterTheme.Space * 40));
        HudUi.Tint(_title, ShooterTheme.Good);
        v.AddChild(_detail = HudUi.L("", "HudBig", HorizontalAlignment.Center));
        _detail.AutowrapMode = TextServer.AutowrapMode.WordSmart;
    }

    public void Attach(JobDone done) { _done = done; _revision = -1; }

    /// <summary>Every frame: shows while the banner is up, with its fade.</summary>
    public void Tick()
    {
        if (_done is not { Showing: true } d) { _panel.Visible = false; return; }
        if (d.Revision != _revision)
        {
            _revision = d.Revision;
            HudUi.Set(_title, Strings.T("cartel.end.done"));
            var detail = d.Detail;
            _detail.Visible = !detail.Hidden;
            HudUi.Set(_detail, detail.Earned ? Strings.F("cartel.end.paid", detail.Respect) : detail.Why is { } why ? Strings.T(CartelWords.NoRespect.Key(why)) : "");
        }
        _panel.Visible = true;
        var t = d.Elapsed;
        HudUi.Fade(_panel, GameSettings.Current.ReduceMotion ? 1f : Mathf.Clamp(Mathf.Min(t, JobDone.Seconds - t) / FadeSeconds, 0f, 1f));
    }
}
