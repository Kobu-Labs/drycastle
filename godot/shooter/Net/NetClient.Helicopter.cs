using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The helicopter helper on the client: the targeting warning for the player it is about to fire on (a red dot on the
/// helicopter for its one second, the words, and a UI cue). The helicopter itself and its tracers come from the aircraft
/// rendering and the shared Shot event.
/// </summary>
public partial class NetClient
{
    HelperWarningOverlay? _helperWarn;

    /// <summary>EventKind.HelperWarning, sent to this player only: the red dot and cue for the helicopter's one-second warning.</summary>
    void OnHelperWarning(HelperWarning w)
    {
        if (w.Target != _myId) return;
        if (Sfx.Enabled) Sfx.Play2D("helper.warning");
        _helperWarn ??= new HelperWarningOverlay();
        if (!_helperWarn.IsInsideTree()) AddChild(_helperWarn);
        var helicopter = w.Helicopter;
        _helperWarn.Show(() => _aircraft.TryGetValue(helicopter, out var view) ? view.Model.GlobalPosition : null, w.Seconds);
    }
}
