using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

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
    /// <summary>The noise's features are about this many blast radii across.</summary>
    const float NoiseFeaturePerRadius = 1.6f;

    static readonly StringName POrigin = "origin", PTint = "tint", PAge = "age", PExtinction = "extinction", PNoiseScale = "noise_scale", PDrift = "drift",
        PCoreR = "core_r", PCoreTop = "core_top", PCloudR = "cloud_r", PCloudH = "cloud_h", PCloudA = "cloud_a", PColR = "col_r", PColTop = "col_top", PColA = "col_a",
        PWaveReach = "wave_reach", PWaveFront = "wave_front", PWaveSeconds = "wave_seconds", PWaveHang = "wave_hang", PWaveRise = "wave_rise", PWaveH = "wave_h", PNoise = "noise_tex";

    ShaderMaterial? _mat;
    Vector3 _at;
    float _radius, _columnHeight;
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

    /// <summary>The dust of a blast of <paramref name="radius"/> on the ground at <paramref name="at"/>, gone off at <paramref name="born"/> (client seconds).</summary>
    public void Start(Vector3 at, float radius, double born)
    {
        if (_mat == null) return; // no shader: no dust is drawn (the veil still works)
        _at = at; _radius = radius; _born = born;
        _columnHeight = radius * Mathf.Lerp(ColumnLowPerRadius, ColumnTallPerRadius, Mathf.Clamp((radius - ColumnSmallRadius) / (ColumnTallRadius - ColumnSmallRadius), 0f, 1f));
        EndMs = (ulong)((born + Seconds) * 1000.0);
        var front = DustRules.HasFront(radius);
        _mat.SetShaderParameter(POrigin, at);
        _mat.SetShaderParameter(PExtinction, ExtinctionPerRadius / radius);
        _mat.SetShaderParameter(PNoiseScale, 1f / (radius * NoiseFeaturePerRadius));
        _mat.SetShaderParameter(PColR, radius * ColumnRadiusPerRadius);
        _mat.SetShaderParameter(PWaveReach, front ? DustRules.FrontReach(radius) : 0f);
        _mat.SetShaderParameter(PWaveSeconds, DustRules.FrontSeconds(radius));
        _mat.SetShaderParameter(PWaveHang, DustRules.FrontHang(radius));
        _mat.SetShaderParameter(PWaveRise, DustRules.FrontRiseSeconds);
        _mat.SetShaderParameter(PWaveH, DustRules.FrontHeight(radius));
        Feed(0);
        Visible = true;
        SetProcess(true);
    }

    float Seconds => MathF.Max(DustRules.Seconds(_radius), DustRules.CloudSeconds(_radius) * ColumnLifeShare);

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
        var waveFront = front ? DustRules.FrontRadius(r, age) : 0f;
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
        var top = MathF.Max(coreTop, front ? waveH * 1.4f : 0f) + 1f;
        Position = _at + new Vector3(0, top * 0.5f - 0.5f, 0);
        Scale = new Vector3(half, top * 0.5f + 0.5f, half);
    }
}
