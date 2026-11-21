using Godot;

namespace Drycastle.Shooter;

/// <summary>
/// Zombite (crystals.md): crystals of rotting flesh, grey-green with dark veins and chips of bone (<c>shaders/crystal_veined.gdshader</c>), blood stains, no glow. Flies circle a tip. Patches of dirt on the
/// rock with hands in them: every 10-15 s a hand slowly reaches out, twitches and sinks back (a pure function of time). Tunables: data/crystals.json "zombite".
/// </summary>
public sealed partial class ZombiteFx : CrystalFx
{
    public override string Id => "zombite";

    const int MaxHands = 5, MaxStains = 8;

    StandardMaterial3D _rock = null!, _blood = null!, _dirt = null!, _skin = null!;
    ShaderMaterial _crystal = null!;
    FlySwarm _flies = null!;
    readonly Node3D[] _hands = new Node3D[MaxHands], _arms = new Node3D[MaxHands];
    readonly MeshInstance3D[] _patches = new MeshInstance3D[MaxHands], _stains = new MeshInstance3D[MaxStains];

    protected override void Build()
    {
        _rock = CrystalAssets.Rock(Colors.White, 0.95f);
        _crystal = Veined();
        Body.SetSurfaceOverrideMaterial(0, _rock);
        Body.SetSurfaceOverrideMaterial(1, _crystal);
        _blood = new StandardMaterial3D { Roughness = 0.5f };
        _dirt = new StandardMaterial3D { Roughness = 1f };
        _skin = new StandardMaterial3D { Roughness = 0.8f };
        for (var i = 0; i < MaxStains; i++) _stains[i] = Disc($"Stain{i}", _blood);
        for (var i = 0; i < MaxHands; i++)
        {
            _patches[i] = Disc($"Dirt{i}", _dirt);
            _hands[i] = new Node3D { Name = $"Hand{i}" };
            AddChild(_hands[i]);
            _arms[i] = Hand(_skin);
            _hands[i].AddChild(_arms[i]);
        }
        _flies = new FlySwarm(this, Rng, 16);
    }

    /// <summary>A forearm and an open hand of unit height standing on the origin, built from boxes.</summary>
    static Node3D Hand(Material skin)
    {
        var arm = new Node3D();
        void Box(Vector3 size, Vector3 pos, Vector3 rotDeg) =>
            arm.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = size, Material = skin }, Position = pos, RotationDegrees = rotDeg, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        Box(new Vector3(0.16f, 0.56f, 0.12f), new Vector3(0, 0.28f, 0), Vector3.Zero);
        Box(new Vector3(0.24f, 0.2f, 0.08f), new Vector3(0, 0.66f, 0), Vector3.Zero);
        for (var f = 0; f < 4; f++)
            Box(new Vector3(0.045f, 0.24f, 0.045f), new Vector3(-0.095f + f * 0.063f, 0.87f - Math.Abs(f - 1.5f) * 0.02f, 0.02f), new Vector3(18f, 0, (f - 1.5f) * -7f));
        Box(new Vector3(0.05f, 0.16f, 0.05f), new Vector3(0.16f, 0.68f, 0.01f), new Vector3(0, 0, -50f));
        return arm;
    }

    protected override void OnApply()
    {
        _rock.AlbedoColor = P.C("rock_tint", "#3d362c");
        ApplyVeined(_crystal, new[] { "#6f7a5a", "#7d8468", "#5f6a52" }, "#4a1a22", 1f, 0f, 0.55f, 0f, 0.25f, "#d8d0b8");
        _blood.AlbedoColor = P.C("blood_color", "#4a0a0c");
        _dirt.AlbedoColor = P.C("dirt_color", "#2a1d12");
        _skin.AlbedoColor = P.C("hand_color", "#7d8a66");

        var sn = Math.Clamp(P.I("stain_count", 5), 0, MaxStains);
        for (var i = 0; i < MaxStains; i++)
        {
            // half of them on the crystals, half on the rock
            var spot = i % 2 == 0 ? CrystalSpot(P.I("stain_seed", 4) + i * 31) : RockSpot(P.I("stain_seed", 4) + i * 31);
            _stains[i].Visible = i < sn && spot is not null;
            if (!_stains[i].Visible) continue;
            var r = P.F("stain_radius", 0.035f) * (0.5f + 0.8f * Hash01(i * 2.7));
            PlaceDisc(_stains[i], spot!.Value, r, r * (0.5f + 0.5f * Hash01(i * 4.1)), i * 1.3f);
        }

        var hn = P.B("dress_hands", true) && Info.RockUp.Count > 0 ? Math.Clamp(P.I("hand_count", 3), 0, MaxHands) : 0;
        for (var i = 0; i < MaxHands; i++)
        {
            _hands[i].Visible = _patches[i].Visible = i < hn;
            if (i >= hn) continue;
            var s = RockSpot(P.I("hand_seed", 12) + i * 37)!.Value;
            var up = (s.N + Vector3.Up).Normalized();
            _hands[i].Transform = new Transform3D(OnSurface(up, i * 2.2f).Scaled(Vector3.One * P.F("hand_size", 0.1f)), s.P);
            var r = P.F("dirt_radius", 0.045f);
            PlaceDisc(_patches[i], s, r, r * 0.8f, i * 0.9f);
        }
        SetLoop(P.S("fly_sound", ""), P.F("fly_db", -22f));
    }

    protected override void Tick(double dt)
    {
        _flies.Update(Time, Math.Clamp(P.I("flies_count", 7), 0, 16), P.F("fly_size", 0.007f), Tip(P.I("flies_island", 2)) + new Vector3(0, 0.05f, 0), P.F("flies_radius", 0.12f), P.F("flies_speed", 3.2f));

        float rise = Math.Max(P.F("hand_rise", 1.6f), 0.1f), hold = P.F("hand_hold", 2.5f);
        for (var i = 0; i < MaxHands; i++)
        {
            if (!_hands[i].Visible) continue;
            var period = Mathf.Lerp(P.F("hand_interval_min", 10f), P.F("hand_interval_max", 15f), Hash01(i * 3.9));
            var t = (float)((Time + Hash01(i * 8.3) * 30.0) % period);
            var ext = t < rise ? Mathf.SmoothStep(0f, 1f, t / rise) : t < rise + hold ? 1f : Mathf.SmoothStep(1f, 0f, (t - rise - hold) / rise);
            var twitch = t > rise && t < rise + hold ? 1f : 0f;
            _arms[i].Visible = t < rise * 2f + hold;
            _arms[i].Position = new Vector3(0, -(1f - ext) * 1.05f, 0);
            _arms[i].Rotation = new Vector3(Mathf.Sin(t * 13f + i) * 0.09f * twitch, 0, Mathf.Sin(t * 9.7f) * 0.12f * twitch);
        }
    }
}
