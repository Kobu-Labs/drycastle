namespace Drycastle.ShooterCore;

/// <summary>
/// intel.json: where the intel room is and how its screen behaves. The room is the command post (the floor three levels
/// down) of one bunker, the base; the screen hangs on the wall the pipeline reserved for it (<c>intel</c> in bunkers.json, see
/// <see cref="IntelAnchor"/>).
/// </summary>
public class IntelFile : IVersioned
{
    public int Version { get; set; }
    /// <summary>The id of the bunker (bunkers.json) that is the base. A map without it has no intel room; the announcement still reaches everyone.</summary>
    public string BaseBunker { get; set; } = "";
    /// <summary>With several events on the board the screen shows one at a time, each for this many seconds.</summary>
    public double RotateSeconds { get; set; } = 10;
    /// <summary>Further than this from the screen (m) it is not drawn at all: no per-frame cost anywhere else in the village.</summary>
    public float ViewRangeM { get; set; } = 14f;

    public static IntelFile Parse(string json) => Validate(DataFile.Parse<IntelFile>(json, "intel.json"));

    public static IntelFile Validate(IntelFile f)
    {
        if (f.RotateSeconds < 2) throw new DataFileException("intel.json: rotate_seconds must be at least 2");
        if (f.ViewRangeM < 3) throw new DataFileException("intel.json: view_range_m must be at least 3");
        return f;
    }
}
