using Godot;
using Drycastle.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

/// <summary>
/// The hunting binoculars' mark (#473, GLOSSARY "Binoculars"): the mark key while they are raised highlights the animal under the middle of the view for the pair's
/// <see cref="ItemDef.MarkSeconds"/> (30 s), drawn through walls and trees in the colour of a hunter's tag. Only the watcher sees it: it is a client look on an animal
/// the client already has (the server is not asked and nobody else is told).
/// </summary>
public partial class NetClient
{
    static readonly StandardMaterial3D MarkMaterial = new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, NoDepthTest = true,
        AlbedoColor = new Color(1f, 0.82f, 0.18f, 0.5f), RenderPriority = 20,
    };

    /// <summary>The mark key: the nearest animal under the middle of the view, not behind the world, within the binoculars' range, is marked (a repeat renews the time).</summary>
    void MarkAnimal()
    {
        if (CarriedBinoculars is not { MarkSeconds: > 0 } pair || _me == null) return;
        if (AnimalUnderCentre() is not { } v)
        {
            UiShell.Instance?.Toast(Strings.T("toast.mark.none"), ToastKind.Warn);
            Sfx.Ui("ui.error");
            return;
        }
        v.MarkedUntil = Now + pair.MarkSeconds;
        Sfx.Ui("ui.accept");
        UiShell.Instance?.Toast(Strings.F("toast.mark.set", (int)pair.MarkSeconds), ToastKind.Info);
    }

    AnimalView? AnimalUnderCentre()
    {
        var cam = _me!.Camera;
        var from = cam.GlobalPosition;
        var dir = -cam.GlobalTransform.Basis.Z;
        var wall = Binoculars.MaxRangeM;
        var q = PhysicsRayQueryParameters3D.Create(from, from + dir * Binoculars.MaxRangeM, CollisionLayers.WorldShots);
        q.Exclude = new Godot.Collections.Array<Rid> { _me.GetRid() };
        var hit = World.GetWorld3D().DirectSpaceState.IntersectRay(q);
        if (hit.Count > 0) wall = from.DistanceTo(hit["position"].AsVector3());
        var o = new NVec3(from.X, from.Y, from.Z); var d = new NVec3(dir.X, dir.Y, dir.Z);
        AnimalView? best = null;
        var bestAt = wall;
        foreach (var a in _animals.Values)
        {
            if (!GodotObject.IsInstanceValid(a.Node) || !a.Node.Visible || a.HitBox.Count < 3) continue;
            var p = a.Node.GlobalPosition;
            var target = new HitTarget(0, new NVec3(p.X, p.Y, p.Z), Stance.Stand, a.Node.Rotation.Y, Box: new NVec3(a.HitBox[0], a.HitBox[1], a.HitBox[2]), BoxAhead: a.Def.HitBoxAhead * a.Size);
            if (HitMath.RayBox(o, d, target) is { } at && at < bestAt) { best = a; bestAt = at; }
        }
        return best;
    }

    /// <summary>Once a frame: puts the highlight on the animals that are marked and takes it off those whose time ran out; it pulses.</summary>
    void TickAnimalMarks()
    {
        var pulse = 0.38f + 0.14f * MathF.Sin((float)Now * 5f);
        MarkMaterial.AlbedoColor = new Color(1f, 0.82f, 0.18f, pulse);
        foreach (var v in _animals.Values)
        {
            var on = v.MarkedUntil > Now;
            if (on == v.MarkShown || !GodotObject.IsInstanceValid(v.Node)) continue;
            v.MarkShown = on;
            foreach (var mi in v.Node.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>())
                mi.MaterialOverlay = on ? MarkMaterial : null;
        }
    }
}
