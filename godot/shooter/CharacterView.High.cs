using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// A high player's figure (GLOSSARY "High", plantation.json <c>view</c>), the same on every client: the eyes become two bigger white rectangles with a red tint and a small dark pupil each (<see cref="BuildEyes"/>), and a lit
/// cigarette sticks out of the mouth: the Synty model on the head bone (placed from the face plate the way the eyes are), a glowing ember at its tip (emission above the bloom threshold, ADR 0030), one small warm shadowless
/// light on the tip that lights the face, and a dense smoke trail in world space (so it hangs behind a walking figure). The light and the smoke are kept out of the rig (which is scaled) and follow the tip each frame, and
/// only run while the figure is near the camera. Without the private pack a thin white stick stands in. Nothing here has been seen in the game: the numbers are data, nudge them.
/// </summary>
public sealed partial class CharacterView
{
    PlantationFile? _high;
    BoneAttachment3D? _eyes, _cigarette;
    Node3D? _tip;
    OmniLight3D? _glow;
    CpuParticles3D? _smoke;

    /// <summary>The figure is drawn high (the data of plantation.json), or sober again (null). Safe before the model has loaded: the look is kept and drawn on whichever body is current.</summary>
    public void SetHigh(PlantationFile? data)
    {
        if (ReferenceEquals(_high, data)) return;
        _high = data;
        if (_skeleton == null || _mesh?.Skin == null) return;
        DropHigh(true);
        BuildEyes(_skeleton, _mesh);
        BuildCigarette();
    }

    public bool IsHigh => _high != null;

    /// <summary>Takes the cigarette and its light and smoke (and, when asked, the eyes: they are rebuilt) off the figure. The smoke already in the air thins out where it is.</summary>
    void DropHigh(bool andEyes)
    {
        if (andEyes && _eyes != null && GodotObject.IsInstanceValid(_eyes)) _eyes.QueueFree();
        if (andEyes) _eyes = null;
        if (_cigarette != null && GodotObject.IsInstanceValid(_cigarette)) _cigarette.QueueFree();
        _cigarette = null; _tip = null;
        if (_glow != null && GodotObject.IsInstanceValid(_glow)) _glow.QueueFree();
        _glow = null;
        if (_smoke != null && GodotObject.IsInstanceValid(_smoke))
        {
            _smoke.Emitting = false;
            if (IsInsideTree()) ShotFx.Expire(this, _smoke, (float)_smoke.Lifetime + 0.2f);
            else _smoke.QueueFree();
        }
        _smoke = null;
    }

    /// <summary>The two eyes of a high figure, on <paramref name="carrier"/>: a white rectangle with a red tint and a dark pupil in front of it, each in the face plate's frame.</summary>
    void AddHighEyes(BoneAttachment3D carrier, Aabb plate, Transform3D toHead, HighEyesData e)
    {
        var w = plate.Size.X * (float)e.Width;
        var h = plate.Size.Y * (float)e.Height;
        var white = new StandardMaterial3D { AlbedoColor = new Color(e.Colour), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
        var dark = new StandardMaterial3D { AlbedoColor = new Color(e.PupilColour), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
        var eye = new BoxMesh { Size = new Vector3(w, h, 0.02f) };
        var pupil = new BoxMesh { Size = new Vector3(w * (float)e.PupilWidth, h * (float)e.PupilHeight, 0.02f) };
        foreach (var side in new[] { -1f, 1f })
        {
            var at = new Vector3(plate.GetCenter().X + side * plate.Size.X * (float)e.Spread, plate.GetCenter().Y + plate.Size.Y * (float)e.Lift, plate.End.Z + 0.005f);
            carrier.AddChild(new MeshInstance3D { Mesh = eye, MaterialOverride = white, Transform = toHead * new Transform3D(Basis.Identity, at) });
            carrier.AddChild(new MeshInstance3D { Mesh = pupil, MaterialOverride = dark, Transform = toHead * new Transform3D(Basis.Identity, at + new Vector3(0, 0, 0.006f)) });
        }
    }

    /// <summary>The cigarette at the mouth, with its ember, light and smoke (headless: only the stick, no light, no smoke).</summary>
    void BuildCigarette()
    {
        if (_high is not { } high || _skeleton == null) return;
        var c = high.View.Cigarette;
        var head = _skeleton.FindBone("Head");
        if (FacePlate() is not { } plate || head < 0 || !_bind.ContainsKey(head)) return;
        var at = new Vector3(plate.GetCenter().X + plate.Size.X * (float)c.Mouth[0], plate.GetCenter().Y + plate.Size.Y * (float)c.Mouth[1], plate.End.Z + (float)c.OutM / Scale);
        var rot = Basis.FromEuler(new Vector3(Mathf.DegToRad((float)c.RotationDeg[0]), Mathf.DegToRad((float)c.RotationDeg[1]), Mathf.DegToRad((float)c.RotationDeg[2])));
        // the model is in metres facing +Z with its filter end at the origin; the rig is in rig units: the wrapper scales
        var wrap = new Node3D { Name = "Cigarette", Scale = Vector3.One * ((float)c.Scale / Scale) };
        wrap.AddChild(CigaretteModel(c, out var tip));
        _tip = tip;
        var holder = new Node3D { Name = "Holder", Transform = _bind[head].AffineInverse() * new Transform3D(rot, at) };
        holder.AddChild(wrap);
        _cigarette = new BoneAttachment3D { Name = "Cigarette", BoneName = "Head" };
        _cigarette.AddChild(holder);
        _skeleton.AddChild(_cigarette);
        if (!ShotFx.Enabled) return;
        _glow = new OmniLight3D
        {
            Name = "CigaretteGlow", LightColor = new Color(c.Light.Colour), LightEnergy = (float)c.Light.Energy, OmniRange = (float)c.Light.Range, ShadowEnabled = false, Visible = false,
        };
        AddChild(_glow);
        _smoke = CigaretteSmoke(c.Smoke, c.Smoke.Amount, (float)c.Smoke.SizeM);
        AddChild(_smoke);
    }

    /// <summary>The cigarette in metres, facing +Z with its filter end at the origin: the pack model (or the stick), and <paramref name="tip"/> at its end with the glowing ember (no ember headless).</summary>
    internal static Node3D CigaretteModel(CigaretteData c, out Node3D tip)
    {
        var root = new Node3D { Name = "CigaretteModel" };
        root.AddChild(SyntyLib.Instance(c.Model, c.Atlas) ?? Stick(c));
        tip = new Node3D { Name = "Tip", Position = new Vector3(0, 0, (float)c.LengthM) };
        root.AddChild(tip);
        if (!ShotFx.Enabled) return root;
        var ember = new Color(c.EmberColour);
        tip.AddChild(new MeshInstance3D
        {
            Name = "Ember", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Mesh = new SphereMesh { Radius = (float)c.EmberRadiusM, Height = 2f * (float)c.EmberRadiusM, RadialSegments = 8, Rings = 4 },
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = ember, EmissionEnabled = true, Emission = ember, EmissionEnergyMultiplier = (float)c.EmberEnergy,
            },
        });
        return root;
    }

    /// <summary>The smoke of a cigarette tip: a world-space emitter (the smoke stays where it was let out), off until it is run.</summary>
    internal static CpuParticles3D CigaretteSmoke(CigaretteSmokeData s, int amount, float size)
    {
        var rc = RocketFx.C;
        return new CpuParticles3D
        {
            Name = "CigaretteSmoke", LocalCoords = false, Emitting = false, Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Mesh = new SphereMesh { Radius = size, Height = 2f * size, RadialSegments = 8, Rings = 4, Material = RocketFx.Part(new Color(1, 1, 1)) },
            Amount = amount, Lifetime = s.LifetimeS, Direction = Vector3.Up, Spread = 40f,
            InitialVelocityMin = (float)s.SpeedMps * 0.5f, InitialVelocityMax = (float)s.SpeedMps, Gravity = new Vector3(0, (float)s.RiseMps2, 0),
            ColorRamp = rc.SmokeTrail, ScaleAmountCurve = rc.TrailGrow, ScaleAmountMin = 0.8f, ScaleAmountMax = 1.3f,
        };
    }

    /// <summary>The stand-in without the private pack: a thin white stick of the cigarette's length along +Z from the filter end.</summary>
    static MeshInstance3D Stick(CigaretteData c) => new()
    {
        Name = "Stick", Position = new Vector3(0, 0, (float)c.LengthM / 2f),
        Mesh = new BoxMesh { Size = new Vector3(0.011f, 0.011f, (float)c.LengthM) },
        MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.96f, 0.95f, 0.92f), Roughness = 1f },
    };

    /// <summary>Every frame: the light and the smoke follow the tip, and run only while the figure is in sight and near the camera (their own distances in plantation.json).</summary>
    void HighFrame()
    {
        if (_high is not { } high || _tip == null || !GodotObject.IsInstanceValid(_tip) || !_tip.IsInsideTree()) return;
        var c = high.View.Cigarette;
        var tipAt = _tip.GlobalPosition;
        var cam = GetViewport()?.GetCamera3D();
        var seen = IsVisibleInTree();
        if (_glow != null && GodotObject.IsInstanceValid(_glow))
        {
            _glow.GlobalPosition = tipAt;
            _glow.Visible = seen && (cam == null || tipAt.DistanceSquaredTo(cam.GlobalPosition) <= (float)(c.Light.CullM * c.Light.CullM));
        }
        if (_smoke != null && GodotObject.IsInstanceValid(_smoke))
        {
            _smoke.GlobalPosition = tipAt;
            var near = seen && (cam == null || tipAt.DistanceSquaredTo(cam.GlobalPosition) <= (float)(c.Smoke.CullM * c.Smoke.CullM));
            if (_smoke.Emitting != near) ParticleFx.Run(_smoke, near);
        }
    }
}
