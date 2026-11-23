using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The head lamp on the client (GLOSSARY "Head lamp"): the server tells whose lamp is on (<see cref="EventKind.HeadLampState"/>); each frame the nearest
/// <see cref="HeadLamp.MaxBeams"/> lit players get a real spot light (no shadows), the local player's first, from the camera along the view; a remote
/// player's from their head along their aim yaw and pitch. The numbers are in <see cref="HeadLamp"/>.
/// </summary>
public sealed class HeadLampClient
{
    readonly ClientContext _ctx;
    /// <summary>Players whose head lamp is on, by id (a character made later is lit at once).</summary>
    readonly HashSet<ushort> _lit = new();
    readonly SpotLight3D[] _beams = new SpotLight3D[HeadLamp.MaxBeams];
    readonly List<(float D, Vector3 At, Vector3 Dir)> _candidates = new();

    public HeadLampClient(ClientContext ctx) { _ctx = ctx; }

    public void SetLamp(ushort player, bool on)
    {
        if (on) _lit.Add(player); else _lit.Remove(player);
    }

    public void Forget(ushort player) => _lit.Remove(player);

    SpotLight3D Beam(int i)
    {
        if (_beams[i] is { } b && GodotObject.IsInstanceValid(b) && b.IsInsideTree()) return b;
        var beam = LampKit.Beam(Transform3D.Identity, new Color(HeadLamp.ColourR, HeadLamp.ColourG, HeadLamp.ColourB), HeadLamp.RangeM, HeadLamp.AngleDeg, HeadLamp.Energy);
        beam.SpotAngleAttenuation = HeadLamp.AngleAttenuation; // low: no visible rim at the cone's border
        beam.SpotAttenuation = HeadLamp.RangeAttenuation;
        _ctx.Owned.AddChild(beam);
        return _beams[i] = beam;
    }

    /// <param name="ownInVehicle">The local player sits in a vehicle: no lamp of their own then.</param>
    public void Render(bool ownInVehicle)
    {
        if (!ShotFx.Enabled) return;
        if (_lit.Count == 0)
        {
            foreach (var b in _beams) if (b != null && GodotObject.IsInstanceValid(b)) b.Visible = false;
            return;
        }
        _candidates.Clear();
        if (_lit.Contains(_ctx.MyId) && _ctx.Me != null && !_ctx.Dead && !ownInVehicle)
        {
            var cam = _ctx.Me.Camera.GlobalTransform;
            _candidates.Add((0f, cam.Origin, -cam.Basis.Z));
        }
        foreach (var (id, r) in _ctx.Remotes)
        {
            if (id == _ctx.MyId || !_lit.Contains(id) || r.State is EntityState.Dead or EntityState.InVehicle || !r.Node.Visible) continue;
            var head = r.Node.GlobalPosition + Vector3.Up * MovementStep.EyeHeight(r.Stance);
            var dir = -Basis.FromEuler(new Vector3(r.ShownPitch, r.ShownYaw, 0)).Z;
            _candidates.Add((Sfx.ListenerDistance(head), head, dir));
        }
        _candidates.Sort((a, b) => a.D.CompareTo(b.D));
        for (var i = 0; i < HeadLamp.MaxBeams; i++)
        {
            if (i >= _candidates.Count) { if (_beams[i] != null && GodotObject.IsInstanceValid(_beams[i])) _beams[i].Visible = false; continue; }
            var (_, at, d) = _candidates[i];
            var beam = Beam(i);
            var up = Mathf.Abs(d.Y) > 0.99f ? Vector3.Forward : Vector3.Up;
            beam.GlobalTransform = new Transform3D(Basis.LookingAt(d, up), at);
            beam.Visible = true;
        }
    }
}
