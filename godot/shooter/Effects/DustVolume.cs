using Godot;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Effects;

namespace Drycastle.Shooter.Effects;

/// <summary>
/// One blast's dust, drawn as a volume (#345): a box around the dust whose shader (<c>shaders/dust_volume.gdshader</c>) walks every view ray through
/// the cloud, the column and the blast wave. This node only feeds the shader the blast's numbers for the moment (<see cref="DustRules"/>: sizes,
/// densities, how far the wave is) and keeps the box around them; it is hidden and stops processing when the dust is gone. A member of
/// <see cref="DustFx"/>'s pool: made once, restarted by <see cref="Start"/>.
/// </summary>
public partial class DustVolume : MeshInstance3D
{
    /// <summary>The column's height in blast radii: the first for a small blast, rising to the second for one of <see cref="ColumnTallRadius"/> metres and larger.</summary>
    public const float ColumnLowPerRadius = 1.2f, ColumnTallPerRadius = 3f, ColumnSmallRadius = 4f, ColumnTallRadius = 14f;
    /// <summary>The column's half width at its foot, in blast radii, and how much longer than the cloud it lasts.</summary>
    public const float ColumnRadiusPerRadius = 0.3f, ColumnLifeShare = 1.15f;
    /// <summary>How fast the dust thickens along a ray: this over the blast's radius, per metre at full density (a path of one radius hides about 90 %).</summary>
    public const float ExtinctionPerRadius = 2.3f;
    /// <summary>The wave looks the same for every blast: it is drawn as thick and as grainy as the dust of a blast of this radius.</summary>
    const float WaveLookRadius = 10f;
    /// <summary>The noise's features are about this many blast radii across.</summary>
    const float NoiseFeaturePerRadius = 1.6f;

    static readonly StringName POrigin = "origin", PTint = "tint", PAge = "age", PExtinction = "extinction", PNoiseScale = "noise_scale", PDrift = "drift",
        PCoreR = "core_r", PCoreTop = "core_top", PCloudR = "cloud_r", PCloudH = "cloud_h", PCloudA = "cloud_a", PColR = "col_r", PColTop = "col_top", PColA = "col_a",
        PWaveReach = "wave_reach", PWaveFront = "wave_front", PWaveSeconds = "wave_seconds", PWaveHang = "wave_hang", PWaveRise = "wave_rise", PWaveH = "wave_h", PNoise = "noise_tex",
        PWaveExtinction = "wave_extinction", PWaveNoiseScale = "wave_noise_scale",
        PGroundTex = "ground_tex", PGroundScale = "ground_scale", PGroundMin = "ground_min", PGroundMax = "ground_max";

    /// <summary>The ground under a wave is sampled on a grid of this many points a side, over the square the wave can reach: one ray each when the blast goes off.</summary>
    const int GroundSide = 33;
    /// <summary>The ground is looked for this far above and below the blast, in wave reaches (a wave does not climb a cliff).</summary>
    const float GroundSpanPerReach = 0.5f;

    readonly float[] _groundData = new float[GroundSide * GroundSide];
    readonly byte[] _groundBytes = new byte[GroundSide * GroundSide * sizeof(float)];
    Image? _groundImage;
    ImageTexture? _groundTex;
    PhysicsRayQueryParameters3D? _groundRay;
    float _groundMin, _groundMax;

    ShaderMaterial? _mat;
    Vector3 _at;
    float _radius, _columnHeight, _speed;
    double _born;

    /// <summary>When this volume's dust is gone (engine milliseconds): the pool takes an idle one first.</summary>
    public ulong EndMs { get; private set; }

    public static DustVolume Make(Shader? shader, Texture3D noise, BoxMesh box)
    {
        var v = new DustVolume { Name = "DustVolume", Mesh = box, Visible = false, CastShadow = ShadowCastingSetting.Off };
        if (shader != null)
        {
            v._mat = new ShaderMaterial { Shader = shader };
            v._mat.SetShaderParameter(PNoise, noise);
            v.MaterialOverride = v._mat;
        }
        v.SetProcess(false);
        return v;
    }

    /// <summary>The dust of a blast of <paramref name="radius"/> on the ground at <paramref name="at"/>, gone off at <paramref name="born"/> (client seconds), its wave leaving at <paramref name="speed"/>.</summary>
    public void Start(Vector3 at, float radius, double born, float speed)
    {
        if (_mat == null) return; // no shader: no dust is drawn (the veil still works)
        _at = at; _radius = radius; _born = born; _speed = speed;
        _groundMin = 0f; _groundMax = 0f;
        _columnHeight = radius * Mathf.Lerp(ColumnLowPerRadius, ColumnTallPerRadius, Mathf.Clamp((radius - ColumnSmallRadius) / (ColumnTallRadius - ColumnSmallRadius), 0f, 1f));
        EndMs = (ulong)((born + Seconds) * 1000.0);
        var front = DustRules.HasFront(radius);
        _mat.SetShaderParameter(POrigin, at);
        _mat.SetShaderParameter(PExtinction, ExtinctionPerRadius / radius);
        _mat.SetShaderParameter(PNoiseScale, 1f / (radius * NoiseFeaturePerRadius));
        _mat.SetShaderParameter(PColR, radius * ColumnRadiusPerRadius);
        _mat.SetShaderParameter(PWaveReach, front ? DustRules.FrontReach(radius) : 0f);
        _mat.SetShaderParameter(PWaveSeconds, DustRules.FrontSeconds(radius, speed));
        _mat.SetShaderParameter(PWaveHang, DustRules.FrontHang(radius));
        _mat.SetShaderParameter(PWaveRise, DustRules.FrontRiseSeconds);
        _mat.SetShaderParameter(PWaveH, DustRules.FrontHeight(radius));
        _mat.SetShaderParameter(PWaveExtinction, ExtinctionPerRadius / WaveLookRadius);
        _mat.SetShaderParameter(PWaveNoiseScale, 1f / (WaveLookRadius * NoiseFeaturePerRadius));
        if (front) SampleGround(DustRules.FrontReach(radius) + DustRules.FrontHeight(radius));
        Feed(0);
        Visible = true;
        SetProcess(true);
    }

    /// <summary>
    /// The ground's height (relative to the blast) on a grid over the square of half side <paramref name="half"/> around it, into the shader's texture: the wave
    /// runs over it instead of level. A point with no ground in range takes the blast's own height. Roofs count as ground: the wave washes over a house.
    /// </summary>
    void SampleGround(float half)
    {
        var space = GetWorld3D().DirectSpaceState;
        _groundRay ??= new PhysicsRayQueryParameters3D { CollisionMask = CollisionLayers.World };
        var span = MathF.Max(8f, half * GroundSpanPerReach);
        _groundMin = 0f; _groundMax = 0f;
        for (var j = 0; j < GroundSide; j++)
            for (var i = 0; i < GroundSide; i++)
            {
                var x = _at.X + (i / (float)(GroundSide - 1) * 2f - 1f) * half;
                var z = _at.Z + (j / (float)(GroundSide - 1) * 2f - 1f) * half;
                _groundRay.From = new Vector3(x, _at.Y + span, z);
                _groundRay.To = new Vector3(x, _at.Y - span, z);
                var hit = space.IntersectRay(_groundRay);
                var y = hit.Count == 0 ? 0f : hit["position"].AsVector3().Y - _at.Y;
                _groundData[j * GroundSide + i] = y;
                _groundMin = MathF.Min(_groundMin, y); _groundMax = MathF.Max(_groundMax, y);
            }
        Buffer.BlockCopy(_groundData, 0, _groundBytes, 0, _groundBytes.Length);
        if (_groundImage == null)
        {
            _groundImage = Image.CreateFromData(GroundSide, GroundSide, false, Image.Format.Rf, _groundBytes);
            _groundTex = ImageTexture.CreateFromImage(_groundImage);
            _mat!.SetShaderParameter(PGroundTex, _groundTex);
        }
        else
        {
            _groundImage.SetData(GroundSide, GroundSide, false, Image.Format.Rf, _groundBytes);
            _groundTex!.Update(_groundImage);
        }
        // texel centres sit on the grid points: the outermost points are half a texel in from the texture's edge
        _mat!.SetShaderParameter(PGroundScale, (GroundSide - 1) / (GroundSide * 2f * half));
        _mat.SetShaderParameter(PGroundMin, _groundMin);
        _mat.SetShaderParameter(PGroundMax, _groundMax);
    }

    float Seconds => MathF.Max(DustRules.Seconds(_radius, _speed), DustRules.CloudSeconds(_radius) * ColumnLifeShare);

    public override void _Process(double delta)
    {
        var age = Time.GetTicksMsec() / 1000.0 - _born;
        if (age >= Seconds) { Visible = false; SetProcess(false); return; }
        Feed(age);
    }

    void Feed(double age)
    {
        var mat = _mat!;
        var r = _radius;
        var life = DustRules.CloudSeconds(r);
        var cloudR = DustRules.CloudRadius(r, age);
        var cloudH = DustRules.CloudHeight(r, age);
        // the column: it climbs fast and slows to its height, and outlasts the cloud a little
        var columnLife = life * ColumnLifeShare;
        var u = Mathf.Clamp((float)age / columnLife, 0f, 1f);
        var colTop = _columnHeight * (1f - (1f - u) * (1f - u));
        var colA = Mathf.Min(1f, (float)age / 0.3f) * (1f - Mathf.SmoothStep(0.5f, 1f, u));
        var colR = r * ColumnRadiusPerRadius;
        var front = DustRules.HasFront(r);
        var waveFront = front ? DustRules.FrontRadius(r, age, _speed) : 0f;
        var waveH = DustRules.FrontHeight(r);

        var coreR = MathF.Max(cloudR, colR * 2.4f) * 1.25f;
        var coreTop = MathF.Max(cloudH, colTop) * 1.1f + 0.5f;
        var c = DustFx.Colour;
        mat.SetShaderParameter(PTint, c);
        mat.SetShaderParameter(PAge, (float)age);
        mat.SetShaderParameter(PDrift, DustFx.Wind * (float)age * 0.02f + new Vector3(0, -(float)age * 0.03f, 0)); // the grain slides downwind and the dust seems to rise through it
        mat.SetShaderParameter(PCoreR, coreR);
        mat.SetShaderParameter(PCoreTop, coreTop);
        mat.SetShaderParameter(PCloudR, cloudR);
        mat.SetShaderParameter(PCloudH, cloudH);
        mat.SetShaderParameter(PCloudA, DustRules.CloudOpacity(r, age));
        mat.SetShaderParameter(PColTop, colTop);
        mat.SetShaderParameter(PColA, colA);
        mat.SetShaderParameter(PWaveFront, waveFront);

        // the box around all of it: the mesh is a cube of half side 1
        var half = MathF.Max(coreR, front ? waveFront + waveH : 0f) + 1f;
        var top = MathF.Max(coreTop, front ? _groundMax + waveH * 1.1f : 0f) + 1f;
        var bottom = (front ? _groundMin : 0f) - 1f;
        Position = _at + new Vector3(0, (top + bottom) * 0.5f, 0);
        Scale = new Vector3(half, (top - bottom) * 0.5f, half);
    }
}
