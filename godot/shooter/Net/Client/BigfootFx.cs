using Godot;
using Drycastle.Shooter.Audio;
using Drycastle.Shooter.Weapons;
using Drycastle.ShooterCore.Events;
using Drycastle.ShooterCore.Net;

namespace Drycastle.Shooter.Net.Client;

/// <summary>The sound events the effects play. There is no roar yet: these are the closest sounds the game already has, and the table to change when real ones arrive.</summary>
internal static class BigfootSounds
    {
    public const string Roar = "explosion.vehicle", StompWindup = "land", Shockwave = "explosion.rpg", Throw = "shot.knife", Land = "explosion.vehicle", Log = "impact", Grab = "death";
}

/// <summary>
/// The Bigfoot's one-shot effects (<see cref="EventKind.BigfootFx"/>: the shockwave ring, the sounds, the shake, the roar clip, the driver's wait while it throws a vehicle) and the footprints that
/// lead to its corpse (<see cref="EventKind.BigfootTrail"/>, #264).
/// </summary>
internal sealed class BigfootFx
{
    readonly ClientContext _ctx;
    readonly BigfootFile _data;
    readonly BigfootClient _giants;
    /// <summary>The shockwave rings in the air: the node, its radius now, its age.</summary>
    readonly List<(Node3D Node, float Radius, double Born)> _rings = new();
    readonly List<Node3D> _prints = new();

    public BigfootFx(ClientContext ctx, BigfootFile data, BigfootClient giants) { _ctx = ctx; _data = data; _giants = giants; }

    public void Render()
    {
        for (var i = _rings.Count - 1; i >= 0; i--)
        {
            var (node, _, born) = _rings[i];
            var radius = (float)(_ctx.Now - born) * _data.Stomp.WaveSpeedMps;
            if (radius >= _data.Stomp.WaveMaxRadiusM) { node.QueueFree(); _rings.RemoveAt(i); continue; }
            _rings[i] = (node, radius, born);
            var r = MathF.Max(radius, 0.1f);
            // the wall rises in its first metres, runs out at full height and sinks as it dies away
            var life = radius / _data.Stomp.WaveMaxRadiusM;
            var h = MathF.Max(0.05f, WaveWallM * MathF.Min(1f, r / 3f) * (1f - 0.6f * life * life));
            node.Scale = new Vector3(r, h, r);
            var p = node.GlobalPosition; p.Y = (float)node.GetMeta("ground_y") + h / 2f; node.GlobalPosition = p;
            if (node is MeshInstance3D { MaterialOverride: StandardMaterial3D m }) m.AlbedoColor = new Color(0.86f, 0.74f, 0.52f, 0.8f * (1f - life * life));
        }
    }

    // ---- events ----


    /// <summary>The Bigfoot's one-shot effects and its trail. Called for every event the client receives.</summary>
    public void OnEvent(EventKind kind, byte[] payload)
    {
        switch (kind)
        {
            case EventKind.BigfootFx when BigfootFxCodec.Decode(payload) is { } m:
                OnFx(m);
                break;
            case EventKind.BigfootTrail when BigfootTrail.TryDecode(payload, out var prints):
                ShowTrail(prints);
                break;
        }
    }

    void OnFx(BigfootFxMsg m)
    {
        var at = new Vector3(m.Position.X, m.Position.Y, m.Position.Z);
        switch (m.Kind)
        {
            case BigfootFxKind.Appear: Sfx.Play3D(BigfootSounds.Roar, at, 4f); _ctx.Shake.At(at, 25); _giants.RoarAt(at); break;
            case BigfootFxKind.Roar: Sfx.Play3D(BigfootSounds.Roar, at, 6f); _ctx.Shake.At(at, 30); _giants.RoarAt(at); break;
            case BigfootFxKind.Death: Sfx.Play3D(BigfootSounds.Roar, at, 6f); _ctx.Shake.At(at, 30); break;
            case BigfootFxKind.StompWindup: Sfx.Play3D(BigfootSounds.StompWindup, at, 6f); break; // the telegraph: a sound, and the pose
            case BigfootFxKind.Shockwave:
                Sfx.Play3D(BigfootSounds.Shockwave, at, 2f);
                _ctx.Shake.At(at, _data.Stomp.WaveMaxRadiusM);
                AddRing(at);
                break;
            case BigfootFxKind.TrunkThrow: Sfx.Play3D(BigfootSounds.Throw, at); break;
            case BigfootFxKind.VehicleThrow:
                _giants.ThrowCar(m.Id, _ctx.Now + m.Extra / 10.0 + 0.5); // the driver's prediction waits for the flight, and a moment after the landing
                Sfx.Play3D(BigfootSounds.Roar, at, 2f);
                break;
            case BigfootFxKind.VehicleLand:
                _giants.ThrowCar(m.Id, _ctx.Now + 0.3);
                Sfx.Play3D(BigfootSounds.Land, at, -2f);
                _ctx.Shake.At(at, 18);
                break;
            case BigfootFxKind.Grab: Sfx.Play3D(BigfootSounds.Grab, at); break;
            case BigfootFxKind.Launch: Sfx.Play3D(BigfootSounds.Throw, at, 4f); break;
        }
    }


    /// <summary>The height of the shockwave's wall of dust at full size (the wave itself is hit below <see cref="BigfootStomp.WaveHeightM"/>: a jump clears it).</summary>
    const float WaveWallM = 1.5f;

    void AddRing(Vector3 at)
    {
        if (!ShotFx.Enabled) return;
        // a standing wall of dust, dense at the ground and thinning to nothing at its top, with a second, lower one just inside it for depth
        var fade = new GradientTexture2D
        {
            Gradient = new Gradient { Colors = new[] { new Color(1, 1, 1, 0), new Color(1, 1, 1, 0.55f), new Color(1, 1, 1, 1) }, Offsets = new[] { 0f, 0.6f, 1f } },
            Fill = GradientTexture2D.FillEnum.Linear, FillFrom = new Vector2(0, 0), FillTo = new Vector2(0, 0.5f), // a cylinder's side uses the upper half of the texture
            Width = 4, Height = 64,
        };
        var dust = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.86f, 0.74f, 0.52f, 0.8f), AlbedoTexture = fade, Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        var tube = new CylinderMesh { TopRadius = 1.04f, BottomRadius = 1f, Height = 1f, RadialSegments = 48, Rings = 1, CapTop = false, CapBottom = false };
        var ring = new MeshInstance3D { Mesh = tube, MaterialOverride = dust, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Scale = new Vector3(0.1f, 0.05f, 0.1f) };
        ring.AddChild(new MeshInstance3D
        {
            Mesh = tube, MaterialOverride = dust, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Scale = new Vector3(0.93f, 0.55f, 0.93f), Position = new Vector3(0, -0.225f, 0), // its foot on the same ground
        });
        _ctx.Owned.AddChild(ring);
        ring.GlobalPosition = at;
        ring.SetMeta("ground_y", at.Y);
        _rings.Add((ring, 0, _ctx.Now));
    }

    /// <summary>The footprints: one dark print each, flat on the ground, until the next trail replaces them (an empty one clears them).</summary>
    void ShowTrail(List<Footprint> prints)
    {
        foreach (var n in _prints) n.QueueFree();
        _prints.Clear();
        if (!ShotFx.Enabled || prints.Count == 0) return;
        var mesh = new PlaneMesh { Size = new Vector2(0.62f, 1.35f) };
        var material = new StandardMaterial3D { AlbedoColor = new Color(0.07f, 0.05f, 0.035f, 0.8f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
        foreach (var p in prints)
        {
            var node = new MeshInstance3D { Mesh = mesh, MaterialOverride = material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            _ctx.Owned.AddChild(node);
            node.GlobalPosition = new Vector3(p.Position.X, p.Position.Y + 0.04f, p.Position.Z);
            node.Rotation = new Vector3(0, p.Yaw, 0);
            _prints.Add(node);
        }
    }

    // ---- the giant's corpse ----

}
