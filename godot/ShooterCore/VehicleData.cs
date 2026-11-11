namespace Suchohrad.ShooterCore;

/// <summary>vehicles.json. Rules from the vehicles ticket (#77); driving numbers are placeholders tuned by driving.</summary>
public class VehiclesFile : IVersioned
{
    public int Version { get; set; }
    public float RunOverMinSpeed { get; set; } = 8;
    public float RunOverDamagePerSpeed { get; set; } = 10;
    public float RunOverMaxDamage { get; set; } = 100;
    public float AbandonReturnTime { get; set; } = 30;
    public List<VehicleDef> Vehicles { get; set; } = new();

    public static VehiclesFile Load(string path)
    {
        return Validate(DataFile.Load<VehiclesFile>(path));
    }

    public static VehiclesFile Validate(VehiclesFile f)
    {
        if (f.Vehicles.Count == 0) throw new DataFileException("vehicles.json: no vehicles");
        if (f.Vehicles.Count > 16) throw new DataFileException("vehicles.json: at most 16 vehicle types (the type travels in 4 bits)");
        foreach (var v in f.Vehicles)
        {
            if (v.Variants is < 1 or > 16) throw new DataFileException($"vehicles.json: {v.Id}: variants must be 1..16");
            if (v.Paints.Count > 0 && v.PaintMaterial == "") throw new DataFileException($"vehicles.json: {v.Id}: paints need paint_material");
        }
        return f;
    }
}

public class VehicleDef
{
    public string Id { get; set; } = "";
    public float TopSpeed { get; set; }
    public float Acceleration { get; set; }
    public float Steering { get; set; }
    public float Mass { get; set; }
    /// <summary>How many of this type are placed at match start.</summary>
    public int SpawnCount { get; set; }
    /// <summary>Half extents of the footprint (server collision box and run-over test), metres.</summary>
    public float HalfWidth { get; set; } = 1.1f;
    public float HalfLength { get; set; } = 2.15f;
    /// <summary>Model file relative to assets/ (empty: the code-built car with the Kenney sedan). Scale, yaw and offset fit the raw node to the game (forward -Z, origin at the ground centre).</summary>
    public string Model { get; set; } = "";
    public float ModelScale { get; set; } = 1;
    public float ModelYawDeg { get; set; }
    /// <summary>Offset applied to the model node before scale, as [x, y, z].</summary>
    public float[] ModelOffset { get; set; } = { 0, 0, 0 };
    /// <summary>The model's own material that takes the colour variants, and the model's own materials whose colours the variants are (index = colour byte).</summary>
    public string PaintMaterial { get; set; } = "";
    public List<string> Paints { get; set; } = new();
    /// <summary>Number of colour variants: the model's paints, or the shared car palette.</summary>
    public int Variants => Paints.Count > 0 ? Paints.Count : VehicleColours.Palette.Length;
}
