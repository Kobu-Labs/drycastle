using System.Numerics;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Intel;
using Drycastle.ShooterCore.Jobs;

namespace Drycastle.ShooterCore.Cartel;

/// <summary>
/// cartel.json <c>pallet</c>: the Cartel pallet's bag spots and how a client draws it (docs/cartel.md "Delivery"; GLOSSARY "Cartel pallet"). Spots are offsets in the pallet's own frame (metres from its
/// centre, Godot ground axes before its turn), the bags lie on them at <see cref="TopM"/>. The model is the Gang Warfare pallet (its size read from the glb); the size here only feeds the box
/// drawn without the private pack and must match the marker primitive (<c>prim:cartel_pallet</c>, pipeline kits/primitives.py).
/// </summary>
public sealed class CartelPalletData
{
    /// <summary>Metres from the pallet's base to the top of its deck, where a bag lies.</summary>
    public float TopM { get; set; } = 0.235f;
    /// <summary>One [x, z] per bag spot, in the pallet's frame (x along its width, z along its depth); at most four make the bags a Delivery can have at once.</summary>
    public List<float[]> Spots { get; set; } = new() { new[] { -0.36f, -0.33f }, new[] { 0.36f, -0.33f }, new[] { -0.36f, 0.33f }, new[] { 0.36f, 0.33f } };
    /// <summary>The model under assets-private/synty/glb (no .glb).</summary>
    public string Model { get; set; } = "gang-warfare/SM_Prop_Pallet_01";
    /// <summary>The atlas under synty/ the model wears.</summary>
    public string Atlas { get; set; } = "gang-warfare/textures/PolygonGangWarfare_01_A.png";
    /// <summary>The pallet's size [x, y (up), z] in metres, and the colour of the box drawn without the pack.</summary>
    public float[] SizeM { get; set; } = { 1.441f, 0.235f, 1.418f };
    public string Colour { get; set; } = "#8a6a45";

    internal CartelPalletData Validate()
    {
        static DataFileException Bad(string text) => new($"cartel.json: pallet.{text}");
        if (!(float.IsFinite(TopM) && TopM > 0)) throw Bad("top_m must be more than 0");
        if (Spots.Count == 0) throw Bad("spots must list at least one spot");
        foreach (var s in Spots) if (s.Length != 2 || !float.IsFinite(s[0]) || !float.IsFinite(s[1])) throw Bad("spots are [x, z] pairs");
        if (Model == "" || Atlas == "") throw Bad("model and atlas are named");
        if (SizeM.Length != 3 || SizeM.Any(v => !(float.IsFinite(v) && v > 0))) throw Bad("size_m needs 3 numbers, each more than 0");
        return this;
    }
}

/// <summary>
/// Where the Cartel pallet stands: the first placed object of <see cref="CartelZones.PalletAsset"/> (the Contact's marker's twin), its origin the terrain height at the base's centre and its turn
/// about the vertical axis in degrees (Godot's Y rotation).
/// </summary>
public readonly record struct PalletPose(Vector3 Position, float YawDeg)
{
    /// <summary>The turn in radians (a bag lying on a spot lies with it).</summary>
    public float YawRad => YawDeg * MathF.PI / 180f;

    /// <summary>Where bag spot <paramref name="i"/> is in the world: its offset turned with the pallet, on the deck.</summary>
    public Vector3 SpotAt(CartelPalletData data, int i)
    {
        var o = IntelRoom.Rotate(new Vector2(data.Spots[i][0], data.Spots[i][1]), YawDeg);
        return new Vector3(Position.X + o.X, Position.Y + data.TopM, Position.Z + o.Y);
    }
}

/// <summary>
/// The pallet's bag spots the running Jobs hold (docs/cartel.md "Delivery"): a Delivery claims the first free one in the book under <see cref="Key"/> (<c>"pallet:2"</c>, the index in
/// <see cref="CartelPalletData.Spots"/>) and its end frees it, so two Deliveries never share a spot.
/// </summary>
public static class CartelPallet
{
    /// <summary>The book's name of spot <paramref name="index"/>.</summary>
    public static string Key(int index) => $"pallet:{index}";

    /// <summary>The first spot no Job holds, or -1 when the pallet is full.</summary>
    public static int FreeSpot(JobBook book, CartelPalletData data)
    {
        for (var i = 0; i < data.Spots.Count; i++) if (!book.IsReserved(Key(i))) return i;
        return -1;
    }

    /// <summary>Claims the first free spot for <paramref name="job"/> (the book frees it when the Job ends or its start is refused); its index, or -1 when the pallet is full.</summary>
    public static int Claim(Job job, JobBook book, CartelPalletData data)
    {
        var i = FreeSpot(book, data);
        if (i >= 0) book.Reserve(job, Key(i));
        return i;
    }
}
