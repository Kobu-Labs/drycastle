using Godot;

namespace Suchohrad.Shooter;

/// <summary>
/// The control-mode switch (ticket #152): while the owner flies a streak's point of view (a gunship turret, the attack
/// missile), the whole HUD layer is hidden and the streak's own overlay (reticle, weapon, timer, kills, hit marker) is the
/// only thing on screen. Hiding the layer changes no layout, so restoring it is instant when the session ends (death, leave,
/// timeout). The caller computes the mode each frame from the control state, so it cannot get stuck.
/// </summary>
public partial class Hud
{
    /// <summary>True while the regular HUD is hidden for a control session.</summary>
    public bool ControlMode { get; private set; }

    /// <summary>Hides the regular HUD for a control session (on) or shows it again (off). Idempotent; cheap to call every frame.</summary>
    public void SetControlMode(bool on)
    {
        if (ControlMode == on) return;
        ControlMode = on;
        Visible = !on;
    }
}
