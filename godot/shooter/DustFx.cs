using Godot;
using Drycastle.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

/// <summary>
/// The dust of a blast, drawn (#345). Each blast with ground under it takes one <see cref="DustVolume"/> from a small pool: a single ray-marched volume
/// holding the Dust cloud, the column that climbs out of it and, for a large blast, the Blast wave. No particles, so there is nothing to make out but
/// the dust. <see cref="RocketFx.Explode"/> calls <see cref="Blast"/> for every blast, so every kind of explosion gets it. The live blasts are kept in
/// <see cref="Field"/>, which the screen veil and the name tags read. A blast a young cloud already covers draws nothing new; a blast with no ground
/// close under it raises none. Skipped on headless runs. The wave runs level from the ground under the blast: on a slope it leaves the ground or enters it.
/// </summary>
public static class DustFx
{
    /// <summary>Dust volumes in the pool: a cloud lives several seconds, so more than the blast kits. A seventh cloud takes the oldest volume.</summary>
    public const int KitPool = 6;
    /// <summary>A blast raises dust when the ground is at most this far under it: the larger of the two (metres, blast radii).</summary>
    const float GroundMinM = 3f, GroundPerRadius = 0.6f;
    /// <summary>A steady drift: the same on every client.</summary>
    public static readonly Vector3 Wind = new(0.5f, 0f, 0.3f);
    /// <summary>The dust's colour by day and by night (the volume is unshaded and the screen veil is not lit: both are told).</summary>
    public static readonly Color Day = new(0.62f, 0.55f, 0.45f), Night = new(0.09f, 0.085f, 0.08f);

    /// <summary>The dust's colour now (<see cref="SetNight"/>).</summary>
    public static Color Colour { get; private set; } = Day;

    /// <summary>The dust in the air on this client.</summary>
    public static readonly DustField Field = new();

    static Node3D? _world;
    static DustVolume[]? _volumes;
    static Shader? _shader;
    static NoiseTexture3D? _noise;
    static BoxMesh? _box;

    static DustVolume[] Volumes(Node3D world)
    {
        if (_volumes != null && _world == world && GodotObject.IsInstanceValid(_volumes[0])) return _volumes;
        _shader ??= GD.Load<Shader>("res://shaders/dust_volume.gdshader");
        // the grain the dust is cut from: made once, tiles in every direction
        _noise ??= new NoiseTexture3D
        {
            Width = 64, Height = 64, Depth = 64, Seamless = true,
            Noise = new FastNoiseLite { NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth, Frequency = 0.03f, FractalOctaves = 3, Seed = 345 },
        };
        _box ??= new BoxMesh { Size = new Vector3(2f, 2f, 2f) };
        Field.Clear(); // a new world: the old one's dust is gone with it
        _volumes = new DustVolume[KitPool];
        for (var i = 0; i < KitPool; i++)
        {
            _volumes[i] = DustVolume.Make(_shader, _noise, _box);
            world.AddChild(_volumes[i]);
        }
        _world = world;
        return _volumes;
    }

    /// <summary>The time of day for the dust's colour: 0 at full day, 1 at full night.</summary>
    public static void SetNight(float night) => Colour = Day.Lerp(Night, Mathf.Clamp(night, 0f, 1f));

    /// <summary>The dust of a blast of <paramref name="radius"/> at <paramref name="at"/>. Its Blast wave leaves at <paramref name="speed"/>. A blast covered by a young cloud (<see cref="DustRules.Covers"/>) adds nothing.</summary>
    public static void Blast(Node3D world, Vector3 at, float radius, float speed)
    {
        if (!ShotFx.Enabled || !GodotObject.IsInstanceValid(world) || !world.IsInsideTree()) return;
        var volumes = Volumes(world);
        var nowMs = Time.GetTicksMsec();
        var r = Mathf.Max(1f, radius);
        // dust is raised from the ground: a blast in the air (a shot-down aircraft, a rocket high on a wall) has none
        var drop = Mathf.Max(GroundMinM, r * GroundPerRadius);
        var ground = world.GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(at + Vector3.Up * 0.5f, at + Vector3.Down * drop, CollisionLayers.World));
        if (ground.Count == 0) return;
        at = ground["position"].AsVector3();
        var born = nowMs / 1000.0;
        if (!Field.Add(new DustBlast(new NVec3(at.X, at.Y, at.Z), r, born, speed))) return;

        // an idle volume, else the one whose dust ends first
        var pick = volumes[0];
        foreach (var v in volumes)
        {
            if (v.EndMs <= nowMs) { pick = v; break; }
            if (v.EndMs < pick.EndMs) pick = v;
        }
        pick.Start(at, r, born, speed);
    }
}
