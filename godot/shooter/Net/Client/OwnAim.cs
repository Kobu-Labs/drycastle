using Godot;
using Drycastle.Shooter.Audio;
using Drycastle.Shooter.Menu;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Weapons;

namespace Drycastle.Shooter.Net.Client;

/// <summary>
/// The own aim: the recoil offset, the sniper's breath and the zero dialled on a scope. The server runs the same recoil and breath states from the same inputs, so a shot
/// leaves where the client drew it. What the camera shows of them (the kick, the sway) is set here; the HUD reads the scope and the breath through the views.
/// </summary>
internal sealed class OwnAim
{
    readonly ClientContext _ctx;
    readonly LoadoutClient _loadouts;
    readonly Func<bool> _binoculars;
    RecoilState _recoil = new(0);
    BreathState _breath = new(0);
    readonly BreathCues _breathCues = new();
    readonly ZeroDial _zero = new();

    /// <param name="binoculars">The binoculars are up: the scope and the weapon's sway are not theirs.</param>
    public OwnAim(ClientContext ctx, LoadoutClient loadouts, Func<bool> binoculars) { _ctx = ctx; _loadouts = loadouts; _binoculars = binoculars; }

    /// <summary>A spawn: the recoil, breath and zero start afresh for this player.</summary>
    public void Reset()
    {
        _recoil = new RecoilState((ulong)_ctx.MyId);
        _breath = new BreathState((ulong)_ctx.MyId);
        _breathCues.Reset();
        _zero.Reset();
    }

    /// <summary>One hitscan shot: the recoil climbs and the breath's aim error is drawn, as on the server. Returns the climb's stance multiplier, which the viewmodel kicks by too.</summary>
    public float Shot(WeaponDef w, bool aiming)
    {
        // a seat steadies it no more than standing does, as on the server
        var mult = RecoilState.Multiplier(w.Recoil, aiming, _ctx.View.InArmedSeat ? Stance.Stand : _ctx.Me!.State.Stance);
        _recoil.Shot(w.Recoil, mult);
        _breath.AimError(_breath.SwayDegrees(w)); // the server's per-shot aim error; kept in step (the tracer follows the camera, like recoil)
        return mult;
    }

    /// <summary>This input tick: the recoil recovers and the breath advances.</summary>
    public void Tick(MoveInput mi)
    {
        TickRecoil(mi);
        TickBreath(mi);
    }

    /// <summary>The scope that is up, for the HUD's mil-dots and zero: its FOV and the zero dialled on the weapon (0: no bullet drop).</summary>
    public (float FovDeg, float ZeroM) ScopeView() =>
        _ctx.Me is { ZoomFov: > 0 } me && !_binoculars() && _ctx.Ammo is { } a && _ctx.CurrentWeapon is { } w && BulletDrop.HasDrop(w) ? (me.ZoomFov, _zero.Of(a.Slot - 1, w)) : (0f, 0f);

    /// <summary>
    /// The mouse wheel while a scope with bullet drop is up: one click of the zero dial (up: farther). The server is told the new zero and resolves the
    /// next shots with it. False when the wheel is not the dial's (no such scope up), so it cycles the weapons as ever.
    /// </summary>
    public bool DialZero(int clicks)
    {
        if (_ctx.Me is not { ZoomFov: > 0 } || _binoculars() || _ctx.Ammo is not { } a || _ctx.CurrentWeapon is not { } w || !BulletDrop.HasDrop(w)) return false;
        var was = _zero.Of(a.Slot - 1, w);
        var now = BulletDrop.Dial(w, was, clicks);
        if (now == was) return true; // at the end of the dial
        _zero.Set(a.Slot - 1, w, now);
        _ctx.SendHuman(EventKind.SetZero, BulletDrop.EncodeZero(a.Slot - 1, now));
        Sfx.Ui("ui.hover");
        return true;
    }

    /// <summary>Recovers the recoil offset for this tick and shows it on the camera (the server fires with the same offset).</summary>
    void TickRecoil(MoveInput mi)
    {
        if (_ctx.Me == null || _ctx.CurrentWeapon is not { } w) return;
        _recoil.Tick(_ctx.Now, mi.Has(MoveButtons.Fire), w.Recoil);
        _ctx.Me.AimKick = new Vector2(_recoil.PitchRad, _recoil.YawRad);
    }

    /// <summary>Advances the sniper breath (the server runs the same state from the same input), plays its sounds (inhale, heartbeat, exhale) and shows the cosmetic sway on the camera.</summary>
    void TickBreath(MoveInput mi)
    {
        if (_ctx.Me == null || _ctx.CurrentWeapon is not { } w) return;
        var scoped = mi.Has(MoveButtons.Aim) && w.ZoomFov > 0 && _ctx.View.Mode.CanScope;
        _breath.Tick(_ctx.Now, scoped, mi.Has(MoveButtons.Sprint), w, _loadouts.Perks.HoldBreathTime);
        var cue = _breathCues.Tick(_ctx.Now, _breath.Phase, _breath.Meter);
        if (Sfx.Enabled)
        {
            if (cue.Inhale) Sfx.Play2D("breath.in");
            if (cue.Beat) Sfx.Play2D("breath.heartbeat", cue.BeatDb);
            if (cue.ForcedExhale) Sfx.Play2D("breath.out");
            else if (cue.Exhale) Sfx.Play2D("breath.out", -8f);
        }
        var v = BreathState.VisualSway(_ctx.Now, _breath.SwayDegrees(w));
        // the binoculars are held steady: the weapon's sway is not theirs (it would show at their zoom with a sniper rifle in the hotbar)
        _ctx.Me.ViewSway = GameSettings.Current.ReduceMotion || _binoculars() ? Vector2.Zero : new Vector2(v.X, v.Y);
    }

    /// <summary>The breath while scoped with a breath hold (the HUD reads it each frame for the key hint).</summary>
    public (bool Show, float Meter, BreathState.BreathPhase Phase) BreathView()
    {
        if (_ctx.Me is not { ZoomFov: > 0 } || _binoculars() || _ctx.CurrentWeapon is not { } w || BreathState.BudgetSeconds(w, _loadouts.Perks.HoldBreathTime) <= 0)
            return (false, 0f, BreathState.BreathPhase.Ready);
        return (true, _breath.Meter, _breath.Phase);
    }

}
