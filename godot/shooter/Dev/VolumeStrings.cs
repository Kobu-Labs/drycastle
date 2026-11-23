namespace Drycastle.Shooter.Dev;

/// <summary>English strings of the F3 bounding-volume overlay (#479).</summary>
public static class VolumeStrings
{
    public static readonly (string Key, string Text)[] Table =
    {
        ("bind.debug_volumes", "Bounding volumes (debug)"),
        ("bind.debug_volumes_prev", "Bounding volumes: previous type"),
        ("bind.debug_volumes_next", "Bounding volumes: next type"),
        ("volumes.hit", "Hit shape"), ("volumes.body", "Body shape"), ("volumes.static", "Static colliders"), ("volumes.all", "ALL"),
        ("volumes.line", "Volumes ([ ]): {0}"),
    };
}
