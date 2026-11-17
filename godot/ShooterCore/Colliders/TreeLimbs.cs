using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>
/// One stretch of a pack model's wood (a trunk or a branch), in the model's own metres: a square ring of half-size <c>Half0</c> centred (U0, V0) at
/// height Z0 and one of half-size <c>Half1</c> centred (U1, V1) at Z1. U is the model's X, V minus its Z (Godot axes), heights are above the model's
/// origin (its foot). Z0 of 0 marks the foot: that solid starts below the ground, like any trunk.
/// </summary>
public readonly record struct Limb(float Z0, float Z1, float Half0, float Half1, float U0, float V0, float U1, float V1);

/// <summary>The limbs of one pack model and its native height (what the loader measures: the model is scaled so this is the code-built tree's height). No limbs: nothing solid (a bush).</summary>
public sealed record LimbModel(string Pack, string Name, float HeightM, IReadOnlyList<Limb> Limbs)
{
    /// <summary>No limb lies further than this from the model's axis, native metres.</summary>
    public float ReachM { get; } = Limbs.Count == 0 ? 0f : Limbs.Max(l => MathF.Max(Corner(l.U0, l.V0, l.Half0), Corner(l.U1, l.V1, l.Half1)));

    static float Corner(float u, float v, float half) => MathF.Sqrt((MathF.Abs(u) + half) * (MathF.Abs(u) + half) + (MathF.Abs(v) + half) * (MathF.Abs(v) + half));
}

/// <summary>
/// tree_limbs.json: where the wood of every pack tree model is (#375). Written by scripts/tree_collider_audit.py --write from the models of the private
/// pack, so the server and a client without the pack build the same solids; regenerate when pack_trees.json names a new tree model.
/// </summary>
public sealed class TreeLimbsFile : IVersioned
{
    public int Version { get; set; }
    public List<Entry> Models { get; set; } = new();

    public sealed class Entry
    {
        public string Pack { get; set; } = "nature";
        public string Name { get; set; } = "";
        public float HeightM { get; set; }
        /// <summary>Each [z0, z1, half0, half1, u0, v0, u1, v1], see <see cref="Limb"/>.</summary>
        public List<float[]> Limbs { get; set; } = new();
    }

    public static IReadOnlyList<LimbModel> Parse(string json)
    {
        var f = DataFile.Parse<TreeLimbsFile>(json, "tree_limbs.json");
        var models = new List<LimbModel>(f.Models.Count);
        foreach (var e in f.Models)
        {
            if (e.Name.Length == 0) throw new DataFileException("tree_limbs.json: a model has no name");
            if (!(e.HeightM > 0)) throw new DataFileException($"tree_limbs.json: model '{e.Name}' height_m must be above 0");
            var limbs = new List<Limb>(e.Limbs.Count);
            foreach (var l in e.Limbs)
            {
                if (l.Length != 8) throw new DataFileException($"tree_limbs.json: model '{e.Name}': a limb is 8 numbers");
                if (!(l[1] > l[0]) || !(l[2] > 0) || !(l[3] > 0)) throw new DataFileException($"tree_limbs.json: model '{e.Name}': a limb must rise and both its rings must have a size");
                limbs.Add(new Limb(l[0], l[1], l[2], l[3], l[4], l[5], l[6], l[7]));
            }
            models.Add(new LimbModel(e.Pack, e.Name, e.HeightM, limbs));
        }
        return models;
    }
}

/// <summary>The solids of a tree that is drawn as a pack model: its limbs, scaled and turned as the model is. All trunk solids (wood, solid for everyone); the leaves have none.</summary>
public static class TreeLimbs
{
    /// <summary>Depth of a trunk's foot below the ground, metres (as the code-built trunk).</summary>
    public const float FootM = 0.2f;

    /// <summary>Steps of the model scale, as a ratio: a tree's limbs are built at the nearest step, so trees of nearly the same size share their shapes (<see cref="SolidShapeKeys"/>).</summary>
    public const float ScaleStep = 0.01f;

    /// <summary>The scale a model of <paramref name="heightM"/> is given to stand <paramref name="codeHeightM"/> tall, at the nearest <see cref="ScaleStep"/>.</summary>
    public static float Scale(float codeHeightM, float heightM)
    {
        if (!(heightM > 1e-4f) || !(codeHeightM > 0)) return 1f;
        return MathF.Exp(MathF.Round(MathF.Log(codeHeightM / heightM) / ScaleStep) * ScaleStep);
    }

    public static List<SolidPart> Parts(TreeInstance t, LimbModel model, float codeHeightM)
    {
        var k = Scale(codeHeightM, model.HeightM);
        var (s, c) = MathF.SinCos(t.Rot);
        var parts = new List<SolidPart>(model.Limbs.Count);
        foreach (var l in model.Limbs)
            parts.Add(new SolidPart(PartRole.Trunk, PartShape.Limb,
                t.X + (c * l.U0 - s * l.V0) * k, t.Y + (s * l.U0 + c * l.V0) * k,
                l.Z0 <= 0 ? t.Z - FootM : t.Z + l.Z0 * k, t.Z + l.Z1 * k,
                l.Half0 * k, l.Half0 * k, t.Rot, l.Half1 * k, (l.U1 - l.U0) * k, (l.V1 - l.V0) * k));
        return parts;
    }

    /// <summary>No solid of the tree lies further than this from its foot, metres in plan.</summary>
    public static float Reach(LimbModel model, float codeHeightM) => model.ReachM * Scale(codeHeightM, model.HeightM);
}
