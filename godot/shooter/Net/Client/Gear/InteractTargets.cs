using Drycastle.ShooterCore.Hud;

namespace Drycastle.Shooter.Net.Client.Gear;

/// <summary>
/// What the interact key could act on, on foot, from the last render: the landed crate, the Duffel, the safe's Drill, the ground item and the corpse in reach, and whether the corpse
/// chooser is up. Each feature sets its own; who has the key is <see cref="InteractArbiter"/>'s rule over them: a care package in reach owns it first, a ground item nearer than a
/// corpse before the corpse.
/// </summary>
internal sealed class InteractTargets
{
    /// <summary>The landed care package within reach.</summary>
    public ushort? Crate;
    /// <summary>The ground item within reach and how far it is (null when none), and whether it is nearer than the corpse in reach.</summary>
    public ushort? GroundItem;
    public float GroundItemDistance;
    public bool GroundItemNearest;
    /// <summary>The corpse within reach (null when none, or when the player cannot take one).</summary>
    public ushort? Corpse;
    /// <summary>The lying Duffel the server says this player's key would pick up (null when none).</summary>
    public ushort? Duffel;
    /// <summary>True while the server says this player's hold would set or fix a safe's Drill (and until a completed hold's key is let go).</summary>
    public Func<bool> DrillInReach = () => false;
    /// <summary>True while the corpse chooser is up.</summary>
    public Func<bool> ChooserOpen = () => false;

    /// <summary>Who has the interact key on foot: the crate, the Duffel, the Drill, the ground item, the corpse.</summary>
    public InteractOwner Owner => InteractArbiter.Owner(Crate != null, GroundItem != null, GroundItemNearest, Corpse != null, ChooserOpen(), duffelInReach: Duffel != null, drillInReach: DrillInReach());

    /// <summary>The corpse's prompt is shown (not while another has the key or the chooser is up).</summary>
    public bool CorpseShowsPrompt => InteractArbiter.CorpseShowsPrompt(Crate != null, GroundItem != null, GroundItemNearest, true, ChooserOpen(), Duffel != null, DrillInReach());
}
