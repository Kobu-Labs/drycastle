using Godot;
using Drycastle.Shooter.Audio;
using Drycastle.Shooter.Scenery;
using Drycastle.Shooter.Weapons;
using Drycastle.ShooterCore.Items;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Net;

namespace Drycastle.Shooter.Net.Client;

/// <summary>
/// The head lamp on the client (GLOSSARY "Head lamp"): the server tells whose lamp is on (<see cref="EventKind.HeadLampState"/>); each frame the nearest
/// <see cref="HeadLamp.MaxBeams"/> lit players get a real spot light (no shadows), the local player's first, from the camera along the view; a remote
/// player's from their head along their aim yaw and pitch. The numbers are in <see cref="HeadLamp"/>.
/// </summary>
public partial class NetClient
{
    /// <summary>Players whose head lamp is on, by id (a character made later is lit at once).</summary>
    readonly HashSet<ushort> _headLampBy = new();
    readonly SpotLight3D[] _headBeams = new SpotLight3D[HeadLamp.MaxBeams];
    readonly List<(float D, Vector3 At, Vector3 Dir)> _headCands = new();

    void OnHeadLampState(ushort player, bool on)
    {
        if (on) _headLampBy.Add(player); else _headLampBy.Remove(player);
    }

    SpotLight3D HeadBeam(int i)
    {
        if (_headBeams[i] is { } b && GodotObject.IsInstanceValid(b) && b.IsInsideTree()) return b;
        var beam = LampKit.Beam(Transform3D.Identity, new Color(HeadLamp.ColourR, HeadLamp.ColourG, HeadLamp.ColourB), HeadLamp.RangeM, HeadLamp.AngleDeg, HeadLamp.Energy);
        beam.SpotAngleAttenuation = HeadLamp.AngleAttenuation; // low: no visible rim at the cone's border
        beam.SpotAttenuation = HeadLamp.RangeAttenuation;
        Owned.AddChild(beam);
        return _headBeams[i] = beam;
    }

    void RenderHeadLamps()
    {
        if (!ShotFx.Enabled) return;
        if (_headLampBy.Count == 0)
        {
            foreach (var b in _headBeams) if (b != null && GodotObject.IsInstanceValid(b)) b.Visible = false;
            return;
        }
        _headCands.Clear();
        if (_headLampBy.Contains(_myId) && _me != null && !Dead && !_driving && _myState != EntityState.InVehicle)
        {
            var cam = _me.Camera.GlobalTransform;
            _headCands.Add((0f, cam.Origin, -cam.Basis.Z));
        }
        foreach (var (id, r) in _remotes)
        {
            if (id == _myId || !_headLampBy.Contains(id) || r.State is EntityState.Dead or EntityState.InVehicle || !r.Node.Visible) continue;
            var head = r.Node.GlobalPosition + Vector3.Up * MovementStep.EyeHeight(r.Stance);
            var dir = -Basis.FromEuler(new Vector3(r.ShownPitch, r.ShownYaw, 0)).Z;
            _headCands.Add((Sfx.ListenerDistance(head), head, dir));
        }
        _headCands.Sort((a, b) => a.D.CompareTo(b.D));
        for (var i = 0; i < HeadLamp.MaxBeams; i++)
        {
            if (i >= _headCands.Count) { if (_headBeams[i] != null && GodotObject.IsInstanceValid(_headBeams[i])) _headBeams[i].Visible = false; continue; }
            var (_, at, d) = _headCands[i];
            var beam = HeadBeam(i);
            var up = Mathf.Abs(d.Y) > 0.99f ? Vector3.Forward : Vector3.Up;
            beam.GlobalTransform = new Transform3D(Basis.LookingAt(d, up), at);
            beam.Visible = true;
        }
    }
}
