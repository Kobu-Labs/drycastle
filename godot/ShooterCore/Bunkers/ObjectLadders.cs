using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>
/// object_ladders.json (#372): a ladder that hangs on every placed object of an asset (the hunter outposts' guard tower), laid out in the
/// object's own frame like the intel room (<see cref="IntelRoom"/>). Bunker ladders come from bunkers.json; these come from where the objects
/// of objects.json stand, so placing a tower in the editor is all it takes.
/// </summary>
public class ObjectLaddersFile : IVersioned
{
    public int Version { get; set; }
    public List<ObjectLadderDef> Ladders { get; set; } = new();

    public static ObjectLaddersFile Parse(string json) => Validate(DataFile.Parse<ObjectLaddersFile>(json, "object_ladders.json"));

    public static ObjectLaddersFile Validate(ObjectLaddersFile f)
    {
        foreach (var l in f.Ladders) l.Validate();
        return f;
    }

    /// <summary>The ladders of an object of <paramref name="asset"/> standing at <paramref name="origin"/> (ground x, z) and <paramref name="y"/>, turned and scaled; none for an asset without one.</summary>
    public IEnumerable<Ladder> Place(string asset, Vector2 origin, float y, float yawDeg, float scale)
    {
        foreach (var l in Ladders)
            if (l.Asset == asset) yield return l.Place(origin, y, yawDeg, scale);
    }
}

public class ObjectLadderDef
{
    /// <summary>The asset id of the placed objects that carry the ladder.</summary>
    public string Asset { get; set; } = "";
    /// <summary>Where the climber stands: [x, z] in the object's frame (before its turn and scale).</summary>
    public float[] At { get; set; } = Array.Empty<float>();
    /// <summary>The way the climber faces, toward the wall: a unit vector [x, z] in the object's frame. At the top they step off this way.</summary>
    public float[] Facing { get; set; } = Array.Empty<float>();
    /// <summary>Heights above the object's origin: the ground the ladder starts on and the deck it ends at.</summary>
    public float BottomM { get; set; }
    public float TopM { get; set; }

    public void Validate()
    {
        if (Asset == "") throw new DataFileException("object_ladders.json: a ladder has no asset");
        if (At.Length != 2 || Facing.Length != 2) throw new DataFileException($"object_ladders.json: the ladder of {Asset} needs at and facing as [x, z]");
        if (MathF.Abs(Facing[0] * Facing[0] + Facing[1] * Facing[1] - 1f) > 0.05f) throw new DataFileException($"object_ladders.json: the facing of the ladder of {Asset} is not a unit vector");
        if (TopM <= BottomM) throw new DataFileException($"object_ladders.json: the ladder of {Asset} has top_m <= bottom_m");
    }

    public Ladder Place(Vector2 origin, float y, float yawDeg, float scale)
    {
        if (scale <= 0) scale = 1f;
        var rail = origin + IntelRoom.Rotate(new Vector2(At[0], At[1]), yawDeg) * scale;
        var facing = Vector2.Normalize(IntelRoom.Rotate(new Vector2(Facing[0], Facing[1]), yawDeg));
        return new Ladder(rail, facing, y + BottomM * scale, y + TopM * scale);
    }
}
