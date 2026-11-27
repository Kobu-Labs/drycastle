using Godot;
using Drycastle.Shooter.Audio;
using Drycastle.Shooter.Menu;
using Drycastle.Shooter.Net.Client.HudFeeds;
using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Hud;

namespace Drycastle.Shooter.Cartel;

/// <summary>
/// The Contact's panel (docs/cartel.md "Contact"; GLOSSARY "Contact"): the interact key at the Contact opens it, Esc and its Close button close it, and walking out of his reach
/// closes it too. Non-pausing like the Hunter's Log: a card centred on 60 % of the screen's width in the flat HUD style (not a collection log, so no skin), the world live
/// around it, the mouse released, the legs working, firing not. What it shows is <see cref="ContactPanelModel"/>'s: a member reads their Cartel rank, their Respect and the bar
/// to the next rank, the job list by kind and tier (a locked tier is disabled and says the rank it needs), Start for the picked job and tier (the leader's; anyone else reads why
/// not, "Your leader starts jobs"), and Quit with a confirmation; anyone else reads what the Cartel is and Join (a guest reads why not). It rebuilds when the standing, the Job,
/// the leadership or the round changes; the server checks every request again and says a refusal in the chat. A started Job closes the panel.
/// </summary>
public sealed partial class ContactPanel : Control
{
    readonly CartelClient _cartel;
    readonly PanelContainer _card;
    readonly VBoxContainer _body;
    readonly Button _close;
    PanelFade _fade;
    (CartelStatusMsg? Status, TrackerPhase Phase, int Job, bool Leads, bool Round, bool Admin)? _shown;
    (string Kind, int Tier)? _pick;
    /// <summary>Where the panel's footer is: Start and Quit, or the question whether to quit.</summary>
    ContactFooter _footer = ContactFooter.Start;
    Control? _firstFocus;
    /// <summary>The picked tier's button of the last build.</summary>
    Button? _picked;

    enum ContactFooter { Start, ConfirmQuit }

    public ContactPanel(CartelClient cartel)
    {
        _cartel = cartel;
        Visible = false;
        SetProcess(false);
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);

        _card = new PanelContainer { ThemeTypeVariation = "MenuCard", MouseFilter = MouseFilterEnum.Stop, CustomMinimumSize = new Vector2(ShooterTheme.ContactMinW, 0) };
        _card.AnchorLeft = (1f - ShooterTheme.LogWidthShare) / 2; _card.AnchorRight = 1f - _card.AnchorLeft;
        _card.AnchorTop = _card.AnchorBottom = 0.5f;
        _card.GrowHorizontal = GrowDirection.Both; _card.GrowVertical = GrowDirection.Both;
        AddChild(_card);

        var v = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        v.AddThemeConstantOverride("separation", ShooterTheme.Space * 2);
        _card.AddChild(v);
        var head = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        head.AddChild(new Label { Text = Strings.T("cartel.title"), ThemeTypeVariation = "HudBig", SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore });
        _close = new Button { Text = Strings.T("cartel.close"), TooltipText = Strings.T("cartel.close.tip"), FocusMode = FocusModeEnum.All, SizeFlagsVertical = SizeFlags.ShrinkCenter };
        _close.Pressed += () => { Sfx.Ui("ui.back"); Close(); };
        head.AddChild(_close);
        v.AddChild(head);
        _body = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        _body.AddThemeConstantOverride("separation", ShooterTheme.Space);
        v.AddChild(_body);
    }

    public bool IsOpen => _fade.Wanted;

    public void Open()
    {
        if (IsOpen) return;
        _footer = ContactFooter.Start;
        _shown = null; // built fresh: the standing may have changed while it was shut
        _fade.Show(GameSettings.Current.ReduceMotion);
        Visible = true;
        MouseBehaviorRecursive = MouseBehaviorRecursiveEnum.Inherited;
        ApplyAlpha();
        SetProcess(true);
        Refresh();
        Callable.From(FocusFirst).CallDeferred();
    }

    /// <summary>Starts hiding it (the shell's fade time; none with reduce motion).</summary>
    public void Close()
    {
        if (!IsOpen) return;
        var finished = _fade.Hide(GameSettings.Current.ReduceMotion);
        MouseBehaviorRecursive = MouseBehaviorRecursiveEnum.Disabled; // the fading panel takes no clicks
        GetViewport().GuiReleaseFocus();
        if (finished) FinishClose();
    }

    /// <summary>Esc: the quit question goes back to the footer first, else the panel closes. True when it was open (the Esc is used).</summary>
    public bool HandleBack()
    {
        if (!IsOpen) return false;
        Sfx.Ui("ui.back");
        if (_footer == ContactFooter.ConfirmQuit) { _footer = ContactFooter.Start; Rebuild(_cartel.Model()); Callable.From(FocusFirst).CallDeferred(); return true; }
        Close();
        return true;
    }

    void FinishClose()
    {
        ApplyAlpha();
        Visible = false;
        SetProcess(false);
    }

    void ApplyAlpha() => Modulate = new Color(1, 1, 1, _fade.Alpha);

    public override void _Process(double delta)
    {
        var (changed, finished) = _fade.Tick((float)delta, UiShell.FadeTime);
        if (changed) ApplyAlpha();
        if (finished) { FinishClose(); return; }
        if (IsOpen) Refresh();
    }

    /// <summary>Rebuilds when what it shows changed; a Job that started closes it (the player goes to do it).</summary>
    void Refresh()
    {
        var now = _cartel.Signature;
        if (_shown == now) return;
        var started = _shown is { } was && was.Phase != TrackerPhase.Running && now.Phase == TrackerPhase.Running;
        _shown = now;
        if (started) { Close(); return; }
        if (now.Status?.Membership != CartelMembership.Member) _footer = ContactFooter.Start;
        Rebuild(_cartel.Model());
        if (GetViewport().GuiGetFocusOwner() is not { } f || !IsAncestorOf(f) || !f.IsVisibleInTree()) Callable.From(FocusFirst).CallDeferred();
    }

    void FocusFirst()
    {
        if (!IsOpen) return;
        var target = _firstFocus is { } c && IsInstanceValid(c) && c.IsVisibleInTree() && c is not BaseButton { Disabled: true } ? c : _close;
        target.GrabFocus();
    }

    // ---- content ----

    void Rebuild(ContactPanelModel m)
    {
        foreach (var c in _body.GetChildren()) { _body.RemoveChild(c); c.QueueFree(); }
        _firstFocus = null; _picked = null;
        if (!m.Loaded) { _body.AddChild(Text(Strings.T("cartel.loading"), "HudDim")); return; }
        if (m.IsMember) Member(m);
        else Outsider(m);
    }

    void Member(ContactPanelModel m)
    {
        _body.AddChild(Text(Strings.T("cartel.rank.caption"), "HudSmall", upper: true));
        _body.AddChild(Text(m.RankName, "HudHead"));
        _body.AddChild(Text(Strings.F("cartel.respect", m.Respect), "HudTextSmall"));
        _body.AddChild(new ProgressBar { MaxValue = 1, Step = 0, Value = m.BarFraction, ShowPercentage = false, CustomMinimumSize = new Vector2(0, ShooterTheme.Space * 2), MouseFilter = MouseFilterEnum.Ignore });
        _body.AddChild(Text(m.NextRankName is { } next ? Strings.F("cartel.respect.next", m.RespectToNext, next) : Strings.T("cartel.respect.top"), "HudDim"));
        if (m.Sandbox) _body.AddChild(Text(Strings.T("cartel.sandbox"), "HudWarn"));

        _body.AddChild(Gap());
        _body.AddChild(Text(Strings.T("cartel.jobs.caption"), "HudSmall", upper: true));
        if (m.Jobs.Count == 0) _body.AddChild(Text(Strings.T("cartel.jobs.none"), "HudDim"));
        if (_pick is not { } p || !m.Jobs.Any(j => j.Kind == p.Kind && j.Tiers.Any(t => t.Tier == p.Tier && t.Open))) _pick = m.FirstChoice;
        var group = new ButtonGroup();
        foreach (var job in m.Jobs)
        {
            _body.AddChild(Text(job.Name, "HudHead"));
            var row = new HFlowContainer { MouseFilter = MouseFilterEnum.Ignore };
            row.AddThemeConstantOverride("h_separation", ShooterTheme.Space);
            row.AddThemeConstantOverride("v_separation", ShooterTheme.Space);
            foreach (var t in job.Tiers) row.AddChild(TierButton(job, t, group));
            _body.AddChild(row);
        }

        _body.AddChild(Gap());
        if (_footer == ContactFooter.ConfirmQuit) { ConfirmQuit(m); return; }
        var foot = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        foot.AddThemeConstantOverride("separation", ShooterTheme.Space * 2);
        var pick = _pick is { } sel ? m.Jobs.FirstOrDefault(j => j.Kind == sel.Kind) : null;
        var start = new Button
        {
            Text = pick != null ? Strings.F("cartel.start", pick.Name, _pick!.Value.Tier) : Strings.T("cartel.start.pick"),
            ThemeTypeVariation = "MenuPrimary", FocusMode = FocusModeEnum.All, Disabled = m.StartBlock != ContactStartBlock.None || pick == null,
        };
        start.Pressed += () =>
        {
            if (_pick is not { } s) return;
            Sfx.Ui("ui.accept");
            _cartel.Request(new CartelRequest(CartelAction.StartJob, s.Kind, (byte)s.Tier));
        };
        foot.AddChild(start);
        var why = Text(BlockWords(m.StartBlock), "HudDim");
        why.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        why.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        why.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        foot.AddChild(why);
        var quit = new Button { Text = Strings.T("cartel.quit"), ThemeTypeVariation = "MenuDanger", FocusMode = FocusModeEnum.All };
        quit.Pressed += () => { Sfx.Ui("ui.click"); _footer = ContactFooter.ConfirmQuit; Rebuild(_cartel.Model()); Callable.From(FocusFirst).CallDeferred(); };
        foot.AddChild(quit);
        _body.AddChild(foot);
        _firstFocus = !start.Disabled ? start : _picked ?? (Control)_close;
    }

    /// <summary>The quit question in place of the footer: what is kept, what is lost (the Job they lead), Quit or Stay. Stay has the focus.</summary>
    void ConfirmQuit(ContactPanelModel m)
    {
        _body.AddChild(Text(Strings.T("cartel.quit.ask"), "HudTextSmall", wrap: true));
        if (_cartel.Tracker.Phase == TrackerPhase.Running && _cartel.Leads) _body.AddChild(Text(Strings.T("cartel.quit.fails"), "HudWarn", wrap: true));
        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", ShooterTheme.Space * 2);
        var yes = new Button { Text = Strings.T("cartel.quit.confirm"), ThemeTypeVariation = "MenuDanger", FocusMode = FocusModeEnum.All };
        yes.Pressed += () => { Sfx.Ui("ui.accept"); _footer = ContactFooter.Start; _cartel.Request(new CartelRequest(CartelAction.Quit)); };
        var no = new Button { Text = Strings.T("cartel.quit.cancel"), FocusMode = FocusModeEnum.All };
        no.Pressed += () => { HandleBack(); };
        row.AddChild(no);
        row.AddChild(yes);
        _body.AddChild(row);
        _firstFocus = no;
    }

    void Outsider(ContactPanelModel m)
    {
        _body.AddChild(Text(Strings.T("cartel.about"), "HudTextSmall", wrap: true));
        _body.AddChild(Text(Strings.T("cartel.about.squad"), "HudDim", wrap: true));
        if (m.Membership == CartelMembership.Quit) _body.AddChild(Text(Strings.F("cartel.quit.was", m.RankName, m.Respect), "HudTextSmall", wrap: true));
        if (m.Guest) _body.AddChild(Text(Strings.T("cartel.guest"), "HudWarn", wrap: true));
        _body.AddChild(Gap());
        var join = new Button
        {
            Text = Strings.T(m.Membership == CartelMembership.Quit ? "cartel.rejoin" : "cartel.join"),
            ThemeTypeVariation = "MenuPrimary", FocusMode = FocusModeEnum.All, Disabled = m.Guest, SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
        };
        join.Pressed += () => { Sfx.Ui("ui.accept"); _cartel.Request(new CartelRequest(CartelAction.Join)); };
        _body.AddChild(join);
        _firstFocus = join.Disabled ? _close : join;
    }

    /// <summary>One tier of a kind: a toggle in the list's group, its number and what it pays on two lines; locked, it is disabled and says the rank it needs (words, not only the dimming).</summary>
    Button TierButton(ContactJob job, ContactTier t, ButtonGroup group)
    {
        var b = new Button
        {
            Text = Strings.F("cartel.tier", t.Tier) + "\n" + (t.Open ? Strings.F("cartel.tier.pays", t.Respect) : Strings.F("cartel.tier.needs", t.NeedsRank)),
            ToggleMode = true, ButtonGroup = group, ThemeTypeVariation = "MenuTab", FocusMode = FocusModeEnum.All, Disabled = !t.Open,
            CustomMinimumSize = new Vector2(ShooterTheme.ContactTierW, 0),
            TooltipText = t.Open ? "" : Strings.F("cartel.tier.locked.tip", t.Tier, t.NeedsRank),
        };
        if (_pick is { } p && p.Kind == job.Kind && p.Tier == t.Tier) { b.ButtonPressed = true; _picked = b; }
        b.Toggled += on =>
        {
            if (!on) return;
            Sfx.Ui("ui.click");
            _pick = (job.Kind, t.Tier);
            Rebuild(_cartel.Model()); // Start names the new pick
            if (_picked is { } now) Callable.From(() => { if (IsInstanceValid(now)) now.GrabFocus(); }).CallDeferred(); // the focus stays on the tier just picked
        };
        return b;
    }

    static string BlockWords(ContactStartBlock b) => b switch
    {
        ContactStartBlock.NotMember => Strings.T("cartel.block.notmember"),
        ContactStartBlock.NotLeader => Strings.T("cartel.block.notleader"),
        ContactStartBlock.NoRound => Strings.T("cartel.block.noround"),
        ContactStartBlock.JobRunning => Strings.T("cartel.block.running"),
        ContactStartBlock.Loading => Strings.T("cartel.loading"),
        _ => " ", // the line keeps its place: the footer does not move when a reason comes or goes
    };

    static Label Text(string text, string variation, bool upper = false, bool wrap = false) => new()
    {
        Text = text, ThemeTypeVariation = variation, Uppercase = upper, MouseFilter = MouseFilterEnum.Ignore,
        AutowrapMode = wrap ? TextServer.AutowrapMode.WordSmart : TextServer.AutowrapMode.Off,
    };

    static Control Gap() => new() { CustomMinimumSize = new Vector2(0, ShooterTheme.Space), MouseFilter = MouseFilterEnum.Ignore };
}
