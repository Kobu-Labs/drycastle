using Drycastle.ShooterCore.Fsm;

namespace Drycastle.ShooterCore.Hud;

/// <summary>What the MOAB banner shows.</summary>
public enum MoabBannerPhase
{
    /// <summary>Nothing: no panel.</summary>
    Idle,
    /// <summary>"MOAB inbound" with the seconds left until detonation.</summary>
    Countdown,
    /// <summary>"MOAB by X" after the detonation.</summary>
    Blast,
    /// <summary>"MOAB cancelled" after the countdown was stopped.</summary>
    Cancelled,
}

/// <summary>
/// The MOAB banner's state without the Godot nodes (ticket #129): the phase, its deadline, and the full-screen flash that rides on the detonation. It replaces three
/// timers with -1 sentinels (<c>_countdownEnds</c>, <c>_bannerEnds</c>, <c>_flashEnds</c>) whose combinations meant the phase, and the panel's visibility that was
/// recomputed in four places: the panel shows exactly when the phase is not Idle. Deadlines are on the caller's wall clock (seconds).
/// </summary>
public sealed class MoabBannerModel
{
    public static readonly StateMachineSpec<MoabBannerPhase> Spec = StateMachineSpec<MoabBannerPhase>.Define("Hud.MoabBanner", "The MOAB's top-centre banner: countdown, then the blast or the cancelled line.", MoabBannerPhase.Idle)
        .Allow(MoabBannerPhase.Idle, MoabBannerPhase.Countdown, MoabBannerPhase.Blast, MoabBannerPhase.Cancelled)
        .Allow(MoabBannerPhase.Countdown, MoabBannerPhase.Idle, MoabBannerPhase.Blast, MoabBannerPhase.Cancelled)
        .Allow(MoabBannerPhase.Blast, MoabBannerPhase.Idle, MoabBannerPhase.Countdown, MoabBannerPhase.Cancelled)
        .Allow(MoabBannerPhase.Cancelled, MoabBannerPhase.Idle, MoabBannerPhase.Countdown, MoabBannerPhase.Blast)
        .Note(MoabBannerPhase.Idle, "No panel. The countdown running out without a Blast event (the server's) also ends here.")
        .Note(MoabBannerPhase.Countdown, "Armed: the seconds left, rounded up, until detonation. Armed again restarts it; a banner still showing is replaced.")
        .Note(MoabBannerPhase.Blast, "Detonation: the owner's name for 3 s; the flash (0.6 s, none with reduce motion) runs on its own clock and may outlive a re-armed banner.")
        .Note(MoabBannerPhase.Cancelled, "The countdown was stopped: the line for 3 s.")
        .Build();

    /// <summary>How long the "MOAB by X" and "cancelled" banners stay.</summary>
    public const double BannerSeconds = 3.0;
    public const double FlashSeconds = 0.6;

    readonly StateMachine<MoabBannerPhase> _phase = new(Spec);
    double _ends;           // the countdown's detonation time, or the banner's end, whichever the phase is
    bool _flashing;
    double _flashEnds;

    public MoabBannerPhase Phase => _phase.Current;
    public string Owner { get; private set; } = "";

    /// <summary>The panel is up exactly when something is shown.</summary>
    public bool PanelVisible => Phase != MoabBannerPhase.Idle;

    public bool FlashActive => _flashing;

    /// <summary>Whole seconds until detonation, rounded up so the banner reads 5, 4, 3, 2, 1 (0 outside the countdown).</summary>
    public int SecondsLeft(double now) => Phase == MoabBannerPhase.Countdown ? (int)Math.Ceiling(Math.Max(0, _ends - now)) : 0;

    /// <summary>The flash's alpha, 0.85 fading to 0 over <see cref="FlashSeconds"/>.</summary>
    public float FlashAlpha(double now) => 0.85f * (float)Math.Clamp((_flashEnds - now) / FlashSeconds, 0, 1);

    /// <summary>The countdown starts (again). A running flash is left alone.</summary>
    public void Armed(double now, double seconds)
    {
        _phase.Enter(MoabBannerPhase.Countdown);
        _ends = now + Math.Max(0, seconds);
    }

    /// <summary>Detonation: the banner with the owner, and the flash unless reduce motion.</summary>
    public void Blast(double now, string owner, bool reduceMotion)
    {
        _phase.Enter(MoabBannerPhase.Blast);
        _ends = now + BannerSeconds;
        Owner = owner;
        _flashing = !reduceMotion;
        _flashEnds = reduceMotion ? -1 : now + FlashSeconds;
    }

    /// <summary>The countdown was stopped. A running flash is left alone.</summary>
    public void Cancelled(double now)
    {
        _phase.Enter(MoabBannerPhase.Cancelled);
        _ends = now + BannerSeconds;
    }

    /// <summary>One frame: the countdown running out or a banner's time up goes to Idle; the flash ends on its own clock.</summary>
    public void Tick(double now)
    {
        if (Phase != MoabBannerPhase.Idle && now >= _ends) _phase.Enter(MoabBannerPhase.Idle);
        if (_flashing && now >= _flashEnds) _flashing = false;
    }
}
