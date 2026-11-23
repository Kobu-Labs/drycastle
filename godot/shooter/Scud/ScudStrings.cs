using Drycastle.Shooter.Menu;

namespace Drycastle.Shooter.Scud;

/// <summary>English strings of the Scud truck: the targeting screen, the crew's status, the warnings and the notices. Registered with <see cref="Strings.Register"/>. Keys are prefixed scud.</summary>
public static class ScudStrings
{
    public static readonly (string Key, string Text)[] Table =
    {
        // controls (Settings, Controls)
        ("bind.scud_screen", "Scud screen (open / close)"), ("bind.scud_confirm", "Scud: confirm launch"), ("bind.scud_abort", "Scud: withdraw / abort"),
        // the screen
        ("scud.title", "SCUD TARGETING"),
        ("scud.role.operator", "OPERATOR"), ("scud.role.watch", "VIEW ONLY  operator's camera"),
        ("scud.status.targeting", "SELECT TARGET"), ("scud.status.targeting.watch", "OPERATOR IS SELECTING A TARGET"),
        ("scud.status.proposed", "LOCK PLACED  every seat must confirm"),
        ("scud.status.erecting", "RACK RISING  {0} s"), ("scud.status.launching", "IGNITION  {0} s"),
        ("scud.status.pending", "OPENING SCREEN"),
        ("scud.crew.title", "CREW"), ("scud.crew.you", "{0} (you)"), ("scud.crew.confirmed", "CONFIRMED"), ("scud.crew.waiting", "WAITING"),
        ("scud.seat.driver", "Driver"), ("scud.seat.operator", "Operator"), ("scud.seat.other", "Seat {0}"),
        ("scud.reticle", "GRID {0}   {1} m"), ("scud.reticle.close", "TOO CLOSE  minimum {0} m"), ("scud.reticle.none", "GRID {0}"),
        ("scud.lock", "LOCK  GRID {0}   {1} m"), ("scud.truck", "TRUCK"), ("scud.north", "N"), ("scud.scale", "{0} m"),
        ("scud.zoom", "{0}x"),
        // hints
        ("scud.hint.targeting", "{0}  Lock point     {1}  Pan     Mouse wheel  Zoom     {2}  Close screen"),
        ("scud.hint.proposed", "{0}  Confirm launch     {1}  Withdraw confirmation     {2}  Move the lock     {3}  Close screen"),
        ("scud.hint.erecting", "{0}  Abort launch     {1}  Hide screen"),
        ("scud.hint.launching", "{0}  Hide screen"),
        ("scud.hint.watch.targeting", "{0}  Close view"),
        ("scud.hint.watch.proposed", "{0}  Confirm launch     {1}  Withdraw confirmation     {2}  Close view"),
        ("scud.hint.watch.under_way", "{0}  Close view"),
        ("scud.hint.open", "{0}  Open the Scud screen"), ("scud.hint.watch", "{0}  Watch the operator's screen"), ("scud.hint.watch.none", "The operator has no screen open"),
        // the truck's own status line, with the screen closed
        ("scud.line.stowed", "SCUD READY"), ("scud.line.proposed", "SCUD  LOCK PLACED  {0}  Confirm"), ("scud.line.erecting", "SCUD  RACK RISING  {0} s"),
        ("scud.line.launching", "SCUD  IGNITION  {0} s"), ("scud.line.spent", "SCUD  LAUNCHED"), ("scud.line.lowering", "SCUD  RACK LOWERING"),
        ("scud.line.empty", "SCUD  EMPTY  needs a reload"), ("scud.line.reloading", "SCUD  RELOADING  {0} s"), ("scud.line.aborting", "SCUD  LAUNCH ABORTED"),
        // notices
        ("scud.notice.too_close", "Too close: the minimum range is {0} m"), ("scud.notice.off_map", "That point is off the map"),
        ("scud.notice.moving", "The truck must stand still to launch"), ("scud.notice.not_operator", "Only the operator can do that"),
        ("scud.notice.wrong_state", "The launcher cannot do that now"),
        ("scud.notice.crew_changed", "Launch proposal cancelled: the crew changed"), ("scud.notice.withdrawn", "Launch proposal cancelled: a confirmation was withdrawn"),
        ("scud.notice.moved", "Launch proposal cancelled: the truck moved"), ("scud.notice.timeout", "Launch proposal timed out"), ("scud.notice.retarget", "Lock moved: confirm again"),
        ("scud.notice.load_started", "Loading the rocket from the cart: stay put"), ("scud.notice.load_cancelled", "Loading cancelled: the cart or the truck moved. The cart keeps its rocket"),
        ("scud.hint.load", "Hold {0}: load the rocket from the cart"), ("scud.notice.event_found", "You found the Scud truck! +{0} XP"), ("scud.line.loading", "Loading the rocket from the cart ({0} s)"),
        // the warning for everyone near the target
        ("scud.incoming", "INCOMING SCUD   {0} s"), ("scud.incoming.where", "GRID {0}   {1} m from you   Get clear or take shelter"),
        // kill feed
        ("weapon.scud", "Scud missile"),
    };
}
