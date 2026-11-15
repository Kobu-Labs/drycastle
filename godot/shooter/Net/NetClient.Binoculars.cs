using Godot;
using Drycastle.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

/// <summary>
/// The binoculars on the client (GLOSSARY "Binoculars"): with a pair in the bag, the binoculars key raises and lowers them. Raised, the camera
/// narrows to <see cref="Binoculars.FovDeg"/>, the weapon is neither shown nor fired or aimed, and the HUD reads the distance to what is under
/// the centre of the view: the nearest of the world (ground, walls, trees), a vehicle, a wild animal or pet (its body box) and another player.
/// It is measured here, against what this client draws, ten times a second. They come down by the key, on death, in a seat, when the mouse is
/// released (a menu) and when the pair leaves the bag. The server is told only that they went up or down, and tells the others, whose clients
/// pose that player's character with the pair held at the face (<see cref="CharacterView.SetBinoculars"/>).
/// </summary>
public partial class NetClient
{
    bool _binoculars;
    /// <summary>The other players who hold the binoculars up (<see cref="EventKind.BinocularsRaised"/>), kept by id so a character made later is posed at once.</summary>
    readonly HashSet<ushort> _binocularsBy = new();

    void OnBinocularsRaised(ushort player, bool up)
    {
        if (up) _binocularsBy.Add(player); else _binocularsBy.Remove(player);
        if (_remotes.TryGetValue(player, out var r)) r.Binoculars = up;
    }

    /// <summary>Tells the server the binoculars went up or down, so the others see it.</summary>
    void SendBinoculars(bool up) => SendItem(EventKind.RaiseBinoculars, Binoculars.EncodeRaise(up));
    double _binocularsMeasuredAt;
    int? _binocularsRange;

    bool CarriesBinoculars
    {
        get
        {
            foreach (var s in _bagItems)
                if (s is { } stack && ItemData.At(stack.Item) is { Binoculars: true }) return true;
            return false;
        }
    }

    /// <summary>What the HUD shows of the binoculars: whether they are up, and the distance read (null: nothing in range).</summary>
    (bool Up, int? RangeM) BinocularsView() => (_binoculars, _binocularsRange);

    bool HandleBinocularsInput(InputEvent e)
    {
        if (Bot || e.IsEcho() || !e.IsActionPressed("binoculars")) return false;
        if (_binoculars) LowerBinoculars();
        else
        {
            if (_me == null || Dead || _pinned || Mode.ScopeSuppressed || Input.MouseMode != Input.MouseModeEnum.Captured || !CarriesBinoculars) return false;
            _binoculars = true;
            _binocularsMeasuredAt = 0; _binocularsRange = null;
            SendBinoculars(true);
            Sfx.Ui("ui.accept");
        }
        GetViewport().SetInputAsHandled();
        return true;
    }

    void LowerBinoculars()
    {
        if (!_binoculars) return;
        _binoculars = false;
        _binocularsRange = null;
        if (_me != null) _me.HandsBusy = false;
        SendBinoculars(false);
        Sfx.Ui("ui.back");
    }

    /// <summary>Once a frame, from the scope step: true while the binoculars are up (they are then the zoom), after lowering them when they cannot stay up.</summary>
    bool TickBinoculars()
    {
        if (!_binoculars || _me == null) return false;
        if (Dead || _pinned || Input.MouseMode != Input.MouseModeEnum.Captured || !CarriesBinoculars) { LowerBinoculars(); return false; }
        _me.HandsBusy = true;
        _me.ZoomFov = Binoculars.FovDeg; _me.IronSightZoom = false;
        if (_viewmodel != null) _viewmodel.Aiming = false;
        if (Now - _binocularsMeasuredAt >= Binoculars.MeasureSeconds)
        {
            _binocularsMeasuredAt = Now;
            _binocularsRange = Binoculars.Reading(MeasureRange());
        }
        return true;
    }

    /// <summary>Metres from the eye to the nearest thing under the centre of the view, null with nothing within <see cref="Binoculars.MaxRangeM"/>.</summary>
    float? MeasureRange()
    {
        var cam = _me!.Camera;
        var from = cam.GlobalPosition;
        var dir = -cam.GlobalTransform.Basis.Z;
        var best = float.PositiveInfinity;
        var q = PhysicsRayQueryParameters3D.Create(from, from + dir * Binoculars.MaxRangeM, CollisionLayers.WorldShots);
        q.Exclude = new Godot.Collections.Array<Rid> { _me.GetRid() };
        var hit = World.GetWorld3D().DirectSpaceState.IntersectRay(q);
        if (hit.Count > 0) best = from.DistanceTo(hit["position"].AsVector3());
        if (VehicleAlong(from, dir, MathF.Min(Binoculars.MaxRangeM, best), out _, out var vh, MySeat()?.Car)) best = MathF.Min(best, vh.Distance);
        var o = new NVec3(from.X, from.Y, from.Z); var d = new NVec3(dir.X, dir.Y, dir.Z);
        foreach (var a in _animals.Values)
        {
            if (!GodotObject.IsInstanceValid(a.Node) || a.HitBox.Count < 3) continue;
            var p = a.Node.GlobalPosition;
            var target = new HitTarget(0, new NVec3(p.X, p.Y, p.Z), Stance.Stand, a.Node.Rotation.Y, Box: new NVec3(a.HitBox[0], a.HitBox[1], a.HitBox[2]), BoxAhead: a.Def.HitBoxAhead);
            if (HitMath.RayBox(o, d, target) is { } at) best = MathF.Min(best, at);
        }
        foreach (var r in _remotes.Values)
        {
            if (!GodotObject.IsInstanceValid(r.Node) || !r.Node.Visible) continue;
            var p = r.Node.GlobalPosition;
            if (HitMath.RayCapsule(o, d, new NVec3(p.X, p.Y, p.Z), PlayerHitBox.StandHeight, PlayerHitBox.Radius) is { } at) best = MathF.Min(best, at);
        }
        return best <= Binoculars.MaxRangeM ? best : null;
    }
}
