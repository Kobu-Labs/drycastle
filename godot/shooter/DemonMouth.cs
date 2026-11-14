using Godot;

namespace Drycastle.Shooter;

/// <summary>
/// The demon's mouth, made in code (the pack's head is one rigid piece: the rig has a Jaw bone and no vertex on it). At load the triangles of the lower
/// jaw (the head's triangles under the mouth line, in front of the hinge) get vertices of their own, weighted to the Jaw bone, so the face is whole while
/// the mouth is shut and splits along that line when the jaw turns. Behind the split: a dark red cavity and an upper row of teeth on the head, a lower row
/// and a tongue of three joints on the jaw, and a glow. This modifier turns the jaw (<see cref="Target"/>: 0 shut, 1 wide) over whatever pose is on, and
/// moves the tongue. Lengths are in the model's space (scale 1): they grow with the model. The mouth line and the sizes are read off the pack's mesh.
/// </summary>
public sealed partial class DemonMouth : SkeletonModifier3D
{
    /// <summary>The mouth line at the lips (height, how far forward), how much it rises towards the hinge per metre back, and where the jaw ends at the back.</summary>
    const float LipY = 1.517f, LipZ = 0.19f, RiseBack = 0.15f, HingeZ = 0.02f, ChinY = 1.30f;
    const float MaxAngle = 0.62f, OpenPerS = 9f;

    /// <summary>0 shut .. 1 wide open. Set by the view every frame.</summary>
    public float Target;

    int _jaw = -1;
    float _open;
    double _t;
    readonly List<Node3D> _tongue = new();
    Node3D? _tongueRoot;
    OmniLight3D? _glow;

    public float Open => _open;

    /// <summary>Splits the jaw off the head mesh under <paramref name="model"/> and hangs the mouth on <paramref name="skel"/>. Null when the rig has no Jaw or Head.</summary>
    public static DemonMouth? Install(Node3D model, Skeleton3D skel)
    {
        int jaw = skel.FindBone("Jaw"), head = skel.FindBone("Head");
        if (jaw < 0 || head < 0) return null;
        var moved = 0;
        foreach (var mi in model.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>())
            if (mi.Mesh is ArrayMesh am && Bind(mi.Skin, skel, head) is var hb and >= 0 && Bind(mi.Skin, skel, jaw) is var jb and >= 0)
                moved += SplitJaw(mi, am, hb, jb);
        if (moved == 0) { GD.PrintErr("demon: no jaw triangles found on the head, the mouth stays shut"); return null; }

        var m = new DemonMouth { Name = "Mouth", _jaw = jaw };
        skel.AddChild(m);
        var onHead = new BoneAttachment3D { BoneName = "Head", Name = "MouthHead" };
        var onJaw = new BoneAttachment3D { BoneName = "Jaw", Name = "MouthJaw" };
        skel.AddChild(onHead);
        skel.AddChild(onJaw);
        var toHead = skel.GetBoneGlobalRest(head).AffineInverse();
        var toJaw = skel.GetBoneGlobalRest(jaw).AffineInverse();

        // the cavity: a closed dark box inside the head, so the split shows a throat and not the world behind the head
        var flesh = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.22f, 0.02f, 0.02f), Roughness = 0.5f, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            EmissionEnabled = true, Emission = new Color(0.9f, 0.12f, 0.02f), EmissionEnergyMultiplier = 0.35f,
        };
        Add(onHead, new BoxMesh { Size = new Vector3(0.17f, 0.12f, 0.14f), Material = flesh }, toHead * new Vector3(0, 1.49f, 0.07f), Basis.Identity);
        m._glow = new OmniLight3D { LightColor = new Color(1f, 0.35f, 0.1f), LightEnergy = 0, OmniRange = 1.6f, ShadowEnabled = false, Position = toHead * new Vector3(0, 1.50f, 0.12f) };
        onHead.AddChild(m._glow);

        // teeth: two rows of cones along the lip's arc, the second pair out from the middle long (fangs); the lower row a little behind the upper
        var bone = new StandardMaterial3D { AlbedoColor = new Color(0.93f, 0.88f, 0.74f), Roughness = 0.4f };
        for (var i = -4; i <= 4; i++)
        {
            var x = i * 0.02f;
            var z = 0.168f - x * x / (0.09f * 0.09f) * 0.045f;
            var fang = Math.Abs(i) == 2;
            float up = fang ? 0.058f : 0.03f, low = fang ? 0.045f : 0.026f;
            Add(onHead, Cone(up, bone), toHead * new Vector3(x, LipY + 0.006f - up / 2, z), new Basis(Vector3.Forward, MathF.PI));
            Add(onJaw, Cone(low, bone), toJaw * new Vector3(x, LipY - 0.012f + low / 2, z - 0.012f), Basis.Identity);
        }

        // the tongue: three joints from the back of the jaw, lying forward; it stays inside while the mouth is shut
        var meat = new StandardMaterial3D { AlbedoColor = new Color(0.45f, 0.05f, 0.09f), Roughness = 0.35f };
        m._tongueRoot = new Node3D { Position = toJaw * new Vector3(0, 1.487f, 0.055f) };
        onJaw.AddChild(m._tongueRoot);
        var parent = m._tongueRoot;
        for (var k = 0; k < 3; k++)
        {
            var seg = new Node3D { Position = new Vector3(0, 0, k == 0 ? 0 : 0.06f) };
            parent.AddChild(seg);
            var w = 0.05f - 0.012f * k;
            seg.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(w, 0.012f, 0.064f), Material = meat }, Position = new Vector3(0, 0, 0.03f), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
            m._tongue.Add(seg);
            parent = seg;
        }
        return m;
    }

    static MeshInstance3D Cone(float length, Material mat) =>
        new() { Mesh = new CylinderMesh { TopRadius = 0, BottomRadius = 0.009f, Height = length, RadialSegments = 5, Rings = 0, Material = mat }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };

    static void Add(Node3D parent, Mesh mesh, Vector3 at, Basis basis) =>
        Add(parent, new MeshInstance3D { Mesh = mesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off }, at, basis);

    static void Add(Node3D parent, MeshInstance3D mi, Vector3 at, Basis basis)
    {
        mi.Transform = new Transform3D(basis, at);
        parent.AddChild(mi);
    }

    /// <summary>The index the mesh's bone array uses for <paramref name="bone"/>: its bind in the skin, or the bone itself without a skin. -1 when the skin lacks it.</summary>
    static int Bind(Skin? skin, Skeleton3D skel, int bone)
    {
        if (skin == null) return bone;
        var name = skel.GetBoneName(bone);
        for (var i = 0; i < skin.GetBindCount(); i++)
            if (skin.GetBindName(i) == name || (skin.GetBindName(i).IsEmpty && skin.GetBindBone(i) == bone)) return i;
        return -1;
    }

    static bool InJaw(Vector3 c) => c.Z > HingeZ && c.Y > ChinY && c.Y < LipY + (LipZ - c.Z) * RiseBack;

    /// <summary>Gives the jaw's triangles vertices of their own on the Jaw bone, in a copy of the mesh. Returns how many vertices that made.</summary>
    static int SplitJaw(MeshInstance3D mi, ArrayMesh mesh, int headBind, int jawBind)
    {
        var copy = new ArrayMesh();
        var total = 0;
        for (var s = 0; s < mesh.GetSurfaceCount(); s++)
        {
            var arrays = mesh.SurfaceGetArrays(s);
            total += SplitSurface(arrays, headBind, jawBind);
            var eight = (mesh.SurfaceGetFormat(s) & Mesh.ArrayFormat.FlagUse8BoneWeights) != 0;
            copy.AddSurfaceFromArrays(mesh.SurfaceGetPrimitiveType(s), arrays, flags: eight ? Mesh.ArrayFormat.FlagUse8BoneWeights : 0);
            copy.SurfaceSetMaterial(s, mesh.SurfaceGetMaterial(s));
            copy.SurfaceSetName(s, mesh.SurfaceGetName(s));
        }
        if (total > 0) mi.Mesh = copy;
        return total;
    }

    static int SplitSurface(Godot.Collections.Array arrays, int headBind, int jawBind)
    {
        const int V = (int)Mesh.ArrayType.Vertex, I = (int)Mesh.ArrayType.Index, B = (int)Mesh.ArrayType.Bones, W = (int)Mesh.ArrayType.Weights;
        if (arrays[V].VariantType != Variant.Type.PackedVector3Array || arrays[I].VariantType != Variant.Type.PackedInt32Array
            || arrays[B].VariantType != Variant.Type.PackedInt32Array || arrays[W].VariantType != Variant.Type.PackedFloat32Array) return 0;
        var verts = arrays[V].AsVector3Array();
        var idx = arrays[I].AsInt32Array();
        var bones = arrays[B].AsInt32Array();
        var weights = arrays[W].AsFloat32Array();
        var n = verts.Length;
        if (n == 0) return 0;
        var per = bones.Length / n;

        bool OnHead(int v)
        {
            var w = 0f;
            for (var k = 0; k < per; k++) if (bones[v * per + k] == headBind) w += weights[v * per + k];
            return w > 0.5f;
        }

        var map = new Dictionary<int, int>();
        var src = new List<int>(); // the vertex each new one copies
        for (var t = 0; t + 2 < idx.Length; t += 3)
        {
            int a = idx[t], b = idx[t + 1], c = idx[t + 2];
            if (!OnHead(a) || !OnHead(b) || !OnHead(c) || !InJaw((verts[a] + verts[b] + verts[c]) / 3f)) continue;
            for (var k = 0; k < 3; k++)
            {
                var v = idx[t + k];
                if (!map.TryGetValue(v, out var nv)) { nv = n + src.Count; map[v] = nv; src.Add(v); }
                idx[t + k] = nv;
            }
        }
        if (src.Count == 0) return 0;

        for (var ty = 0; ty < (int)Mesh.ArrayType.Max; ty++)
        {
            if (ty == I) continue;
            var a = arrays[ty];
            switch (a.VariantType)
            {
                case Variant.Type.PackedVector3Array: arrays[ty] = Grow(a.AsVector3Array(), n, src); break;
                case Variant.Type.PackedVector2Array: arrays[ty] = Grow(a.AsVector2Array(), n, src); break;
                case Variant.Type.PackedColorArray: arrays[ty] = Grow(a.AsColorArray(), n, src); break;
                case Variant.Type.PackedFloat32Array: arrays[ty] = Grow(a.AsFloat32Array(), n, src); break;
                case Variant.Type.PackedFloat64Array: arrays[ty] = Grow(a.AsFloat64Array(), n, src); break;
                case Variant.Type.PackedInt32Array: arrays[ty] = Grow(a.AsInt32Array(), n, src); break;
                case Variant.Type.PackedByteArray: arrays[ty] = Grow(a.AsByteArray(), n, src); break;
            }
        }
        bones = arrays[B].AsInt32Array();
        weights = arrays[W].AsFloat32Array();
        for (var j = 0; j < src.Count; j++)
            for (var k = 0; k < per; k++)
            {
                bones[(n + j) * per + k] = k == 0 ? jawBind : 0;
                weights[(n + j) * per + k] = k == 0 ? 1f : 0f;
            }
        arrays[B] = bones;
        arrays[W] = weights;
        arrays[I] = idx;
        return src.Count;
    }

    /// <summary>The per-vertex array with the copies appended (any stride: tangents, weights and bones have several values a vertex).</summary>
    static T[] Grow<T>(T[] x, int n, List<int> src)
    {
        var stride = x.Length / n;
        if (stride == 0) return x;
        var r = new T[x.Length + src.Count * stride];
        Array.Copy(x, r, x.Length);
        for (var j = 0; j < src.Count; j++) Array.Copy(x, src[j] * stride, r, (n + j) * stride, stride);
        return r;
    }

    public override void _ProcessModificationWithDelta(double delta)
    {
        var skel = GetSkeleton();
        if (skel == null || _jaw < 0) return;
        _t += delta;
        _open = Mathf.MoveToward(_open, Mathf.Clamp(Target, 0f, 1f), (float)delta * OpenPerS * 0.5f);
        // wide open it trembles a little
        var a = MaxAngle * _open * (1f + 0.06f * MathF.Sin((float)_t * 31f) * _open);
        skel.SetBonePoseRotation(_jaw, skel.GetBonePoseRotation(_jaw) * new Quaternion(Vector3.Right, a));
        if (_glow != null) _glow.LightEnergy = 1.6f * _open;
        if (_tongueRoot == null) return;
        _tongueRoot.Scale = new Vector3(1, 1, 0.55f + 1.0f * _open); // it comes out past the teeth
        for (var k = 0; k < _tongue.Count; k++)
            _tongue[k].Rotation = new Vector3(
                (-0.12f + 0.28f * MathF.Sin((float)_t * 6.1f + k * 1.1f)) * _open,
                0.32f * MathF.Sin((float)_t * 3.7f + k * 1.4f) * _open, 0);
    }
}
