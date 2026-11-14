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
    const float LipY = 1.553f, LipZ = 0.2f, RiseBack = 0.1f, HingeZ = 0.02f, ChinY = 1.30f;
    /// <summary>Half the muzzle's width (the mane beside it stays on the head), and the heights between which the beard under the jaw follows it less and less.</summary>
    const float HalfWidth = 0.08f, BeardY0 = 1.36f, BeardY1 = 1.50f, BeardMin = 0.35f;
    const float MaxAngle = 0.5f, OpenPerS = 9f;

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

        // the inside, low-poly like the rest: a vaulted palate that narrows and darkens into the throat (on the head); a jaw trough with a gum
        // ridge, cheeks that rise into the muzzle while the mouth is shut and come down with the jaw, and a fur-coloured underside (on the jaw)
        var flesh = new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true, Roughness = 0.45f, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            EmissionEnabled = true, Emission = new Color(0.8f, 0.08f, 0.02f), EmissionEnergyMultiplier = 0.18f,
        };
        Color gum = new(0.62f, 0.10f, 0.10f), red = new(0.36f, 0.03f, 0.03f), dark = new(0.07f, 0.0f, 0.0f), fur = new(0.2f, 0.2f, 0.22f);
        float[] xs = { -HalfMouth, -0.027f, 0f, 0.027f, HalfMouth };
        float Front(float x) => 0.178f - x * x / (0.055f * 0.055f) * 0.04f; // the inside of the lips, an arc
        const float BackZ = 0.03f;

        // palate: rows from the lips back; it vaults in the middle, then drops and narrows to the throat
        var pal = new Shell();
        Vector3 Palate(int c, int r)
        {
            var t = r / 3f;
            var x = xs[c] * (1f + 0.65f * t); // wider towards the hinge, as the jaw's opening is
            var vault = 0.016f * (1f - xs[c] * xs[c] / (HalfMouth * HalfMouth)) * (1f - t * t);
            return new Vector3(x, LipY - 0.004f + vault - 0.05f * t * t * t, Mathf.Lerp(Front(xs[c]), BackZ, t));
        }
        for (var r = 0; r < 3; r++)
            for (var c = 0; c < 4; c++)
                pal.Quad(Palate(c, r), Palate(c + 1, r), Palate(c + 1, r + 1), Palate(c, r + 1), Shade(gum, dark, r / 3f), Shade(gum, dark, (r + 1) / 3f));
        // the throat: from the palate's back edge down, black in the middle
        for (var c = 0; c < 4; c++)
        {
            Vector3 a = Palate(c, 3), b = Palate(c + 1, 3);
            pal.Quad(a, b, new Vector3(b.X * 1.25f, 1.40f, BackZ - 0.02f), new Vector3(a.X * 1.25f, 1.40f, BackZ - 0.02f), dark, Colors.Black);
        }
        Add(onHead, pal.Mesh(flesh), toHead.Origin, toHead.Basis);

        // jaw: floor (dished), gum ridge along the lips, cheeks at the sides towards the hinge, underside in the fur's grey
        var jawS = new Shell();
        Vector3 Floor(int c, int r)
        {
            var t = r / 3f;
            var dish = 0.008f * (1f - xs[c] * xs[c] / (HalfMouth * HalfMouth));
            return new Vector3(xs[c] * (1f + 0.65f * t), JawY - dish * (1f - t) - 0.02f * t * t, Mathf.Lerp(Front(xs[c]) - 0.012f, BackZ + 0.01f, t));
        }
        for (var r = 0; r < 3; r++)
            for (var c = 0; c < 4; c++)
            {
                jawS.Quad(Floor(c, r), Floor(c + 1, r), Floor(c + 1, r + 1), Floor(c, r + 1), Shade(red, dark, r / 3f), Shade(red, dark, (r + 1) / 3f));
                var d = new Vector3(0, 0.004f, 0);
                jawS.Quad(Floor(c, r) - d, Floor(c + 1, r) - d, Floor(c + 1, r + 1) - d, Floor(c, r + 1) - d, fur, fur);
            }
        for (var c = 0; c < 4; c++) // the gum ridge, where the lower teeth stand
        {
            Vector3 a = Floor(c, 0), b = Floor(c + 1, 0), up = new(0, 0.012f, 0.006f);
            jawS.Quad(a, b, b + up, a + up, red, gum);
        }
        foreach (var c in new[] { 0, 4 }) // cheeks
            for (var r = 0; r < 3; r++)
            {
                Vector3 a = Floor(c, r), b = Floor(c, r + 1);
                float ha = 0.012f + 0.03f * (r / 3f), hb = 0.012f + 0.03f * ((r + 1) / 3f);
                jawS.Quad(a, b, b + new Vector3(0, hb, 0), a + new Vector3(0, ha, 0), Shade(red, dark, r / 3f), Shade(gum, dark, (r + 1) / 3f));
            }
        Add(onJaw, jawS.Mesh(flesh), toJaw.Origin, toJaw.Basis);

        m._glow = new OmniLight3D { LightColor = new Color(1f, 0.35f, 0.1f), LightEnergy = 0, OmniRange = 1.6f, ShadowEnabled = false, Position = toHead * new Vector3(0, 1.53f, 0.1f) };
        onHead.AddChild(m._glow);

        // teeth: uneven cones along the arc, the second pair out from the middle long (fangs); the lower row a little behind the upper
        var bone = new StandardMaterial3D { AlbedoColor = new Color(0.9f, 0.84f, 0.66f), Roughness = 0.4f };
        var rng = new Random(7);
        for (var i = -4; i <= 4; i++)
        {
            var x = i * 0.0115f;
            var z = Front(x) - 0.006f;
            var fang = Math.Abs(i) == 2;
            float up = (fang ? 0.05f : 0.022f) * (0.85f + 0.3f * rng.NextSingle()), low = (fang ? 0.032f : 0.018f) * (0.85f + 0.3f * rng.NextSingle());
            var lean = new Basis(Vector3.Right, 0.18f * (rng.NextSingle() - 0.3f)) * new Basis(Vector3.Back, 0.25f * (rng.NextSingle() - 0.5f) - x * 2.5f);
            Add(onHead, Cone(up, fang ? 0.008f : 0.0055f, bone), toHead * new Vector3(x, LipY + 0.004f - up / 2, z), lean * new Basis(Vector3.Forward, MathF.PI));
            Add(onJaw, Cone(low, fang ? 0.007f : 0.005f, bone), toJaw * new Vector3(x * 0.92f, JawY + 0.01f + low / 2, z - 0.014f), lean);
        }

        // the tongue: four tapering joints with a ridge, forked at the tip; it stays inside while the mouth is shut
        var meat = new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 0.3f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
        Color t0 = new(0.42f, 0.03f, 0.07f), t1 = new(0.6f, 0.09f, 0.13f);
        m._tongueRoot = new Node3D { Position = toJaw * new Vector3(0, JawY + 0.004f, 0.045f) };
        onJaw.AddChild(m._tongueRoot);
        var parent = m._tongueRoot;
        const int Joints = 4;
        const float SegL = 0.04f;
        for (var k = 0; k < Joints; k++)
        {
            var seg = new Node3D { Position = new Vector3(0, 0, k == 0 ? 0 : SegL) };
            parent.AddChild(seg);
            float w0 = 0.027f - 0.004f * k, w1 = 0.027f - 0.004f * (k + 1), h0 = 0.012f - 0.002f * k, h1 = 0.012f - 0.002f * (k + 1);
            Color c0 = Shade(t0, t1, k / (float)Joints), c1 = Shade(t0, t1, (k + 1) / (float)Joints);
            var sh = new Shell();
            if (k < Joints - 1)
            {
                Vector3 l0 = new(-w0, 0, 0), r0 = new(w0, 0, 0), m0 = new(0, h0, 0), l1 = new(-w1, 0, SegL), r1 = new(w1, 0, SegL), m1 = new(0, h1, SegL);
                sh.Quad(l0, m0, m1, l1, c0, c1); sh.Quad(m0, r0, r1, m1, c0, c1); sh.Quad(r0, l0, l1, r1, c0.Darkened(0.3f), c1.Darkened(0.3f));
            }
            else
                foreach (var side in new[] { -1f, 1f }) // the fork
                {
                    Vector3 a = new(0, h0 * 0.5f, 0), b = new(side * w0, 0, 0), tip = new(side * w0 * 1.3f, 0.002f, SegL * 1.4f), mid = new(side * w0 * 0.25f, 0.001f, SegL * 0.45f);
                    sh.Tri(a, b, tip, c0, c0, c1); sh.Tri(a, tip, mid, c0, c1, c0);
                }
            seg.AddChild(new MeshInstance3D { Mesh = sh.Mesh(meat), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
            m._tongue.Add(seg);
            parent = seg;
        }
        return m;
    }

    /// <summary>Half the width of the inside of the mouth, and the height of the jaw's floor (the lower lip's top is the mouth line).</summary>
    const float HalfMouth = 0.046f, JawY = 1.53f;

    static Color Shade(Color a, Color b, float t) => a.Lerp(b, Mathf.Clamp(t, 0f, 1f));

    /// <summary>A flat-shaded, vertex-coloured shell built a triangle at a time.</summary>
    sealed class Shell
    {
        readonly SurfaceTool _st = new();
        public Shell() => _st.Begin(Godot.Mesh.PrimitiveType.Triangles);
        public void Tri(Vector3 a, Vector3 b, Vector3 c, Color ca, Color cb, Color cc)
        {
            _st.SetColor(ca); _st.AddVertex(a); _st.SetColor(cb); _st.AddVertex(b); _st.SetColor(cc); _st.AddVertex(c);
        }
        /// <summary>a, b on the near edge (colour <paramref name="near"/>), c, d on the far one.</summary>
        public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color near, Color far)
        {
            Tri(a, b, c, near, near, far); Tri(a, c, d, near, far, far);
        }
        public ArrayMesh Mesh(Material mat)
        {
            _st.GenerateNormals();
            var m = _st.Commit();
            m.SurfaceSetMaterial(0, mat);
            return m;
        }
    }

    static MeshInstance3D Cone(float length, float radius, Material mat) =>
        new() { Mesh = new CylinderMesh { TopRadius = 0, BottomRadius = radius, Height = length, RadialSegments = 4, Rings = 0, Material = mat }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };

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

    static bool InJaw(Vector3 c) => MathF.Abs(c.X) < HalfWidth && c.Z > HingeZ && c.Y > ChinY && c.Y < LipY + (LipZ - c.Z) * RiseBack;

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
                // the beard hangs from the jaw and follows it less towards its tip, so it does not swing into the chest
                var w = Mathf.Clamp((verts[src[j]].Y - BeardY0) / (BeardY1 - BeardY0), BeardMin, 1f);
                bones[(n + j) * per + k] = k == 0 ? jawBind : k == 1 ? headBind : 0;
                weights[(n + j) * per + k] = k == 0 ? w : k == 1 ? 1f - w : 0f;
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
        if (_glow != null) _glow.LightEnergy = 1.0f * _open;
        if (_tongueRoot == null) return;
        _tongueRoot.Scale = new Vector3(1, 1, 0.7f + 0.45f * _open); // it comes out past the teeth
        for (var k = 0; k < _tongue.Count; k++)
            _tongue[k].Rotation = new Vector3(
                (0.09f + 0.14f * MathF.Sin((float)_t * 6.1f + k * 1.1f)) * _open, // it droops over the lip and waves
                0.13f * MathF.Sin((float)_t * 3.7f + k * 1.4f) * _open, 0);
    }
}
