namespace Drycastle.ShooterCore;

/// <summary>
/// intel.json: where the intel room is and how its screen behaves. The room is on a placed object of one asset (<see cref="BaseAsset"/>,
/// the military hangar; the first such object of the map), its screen laid out in that object's own frame (<see cref="Room"/>,
/// <see cref="IntelRoom.Place"/>). A map without the object falls back to the command post of the bunker <see cref="BaseBunker"/>
/// (the wall the pipeline reserved for it: <c>intel</c> in bunkers.json, <see cref="IntelAnchor"/>). Neither: no intel room.
/// </summary>
public class IntelFile : IVersioned
{
    public int Version { get; set; }
    /// <summary>The asset id of the placed object that is the base (the first one of the map, by object id). Empty: no object anchor.</summary>
    public string BaseAsset { get; set; } = "";
    /// <summary>Where the screen is, in the frame of the <see cref="BaseAsset"/> object. Required with a base asset.</summary>
    public IntelRoomFile? Room { get; set; }
    /// <summary>Fallback when the map has no <see cref="BaseAsset"/> object: the id of the bunker (bunkers.json) whose command post is the base. Empty: no fallback.</summary>
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
        if (f.BaseAsset != "")
        {
            if (f.Room == null) throw new DataFileException("intel.json: base_asset needs a room");
            f.Room.Validate();
        }
        return f;
    }
}

/// <summary>
/// The intel screen in the frame of its object: x and z as the object's own ground axes (what the prefab or placeholder
/// has, before the object's rotation and scale), heights above the object's origin. Nothing here looks at the model, so
/// the room is the same with the real prefab as with the placeholder box of a player who does not have it.
/// </summary>
public class IntelRoomFile
{
    /// <summary>The floor's top above the object's origin (m).</summary>
    public float FloorM { get; set; }
    /// <summary>The middle of the screen on its wall: [x, z] in the object's frame.</summary>
    public float[] At { get; set; } = Array.Empty<float>();
    /// <summary>The way the screen faces, into the room: a unit vector [x, z] in the object's frame.</summary>
    public float[] Facing { get; set; } = Array.Empty<float>();
    /// <summary>The screen's lower edge above the floor (m).</summary>
    public float BottomM { get; set; } = 1.0f;
    /// <summary>The screen's size (m). 3.0 by 1.1 is what <c>make shooter-intel-shots</c> checks; the content lays out to any width from 1.6.</summary>
    public float WidthM { get; set; } = 3.0f;
    public float HeightM { get; set; } = 1.1f;
    /// <summary>Parts of the object to open (the node names of a prefab: a door), so players can walk in: they are hidden and stop colliding. Nothing happens for a part the object does not have (a placeholder).</summary>
    public List<string> OpenParts { get; set; } = new();

    public void Validate()
    {
        if (At.Length != 2) throw new DataFileException("intel.json: room.at must be [x, z]");
        if (Facing.Length != 2 || MathF.Abs(Facing[0] * Facing[0] + Facing[1] * Facing[1] - 1f) > 0.05f)
            throw new DataFileException("intel.json: room.facing must be a unit vector [x, z]");
        if (WidthM < 1.6f) throw new DataFileException("intel.json: room.width_m must be at least 1.6");
        if (HeightM < 0.8f) throw new DataFileException("intel.json: room.height_m must be at least 0.8");
        if (BottomM < 0) throw new DataFileException("intel.json: room.bottom_m must not be negative");
    }
}
