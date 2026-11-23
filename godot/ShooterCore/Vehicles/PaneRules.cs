using System.Numerics;
using Drycastle.ShooterCore.Glass;

namespace Drycastle.ShooterCore.Vehicles;

/// <summary>Where a pane stopped a bullet on its way through a vehicle (<see cref="PaneRules.StoppedBy"/>).</summary>
public enum PaneStopAt { None, Entry, Exit }

/// <summary>The pane that stopped the bullet, and the metres along the ray where it did.</summary>
public readonly record struct PaneStop(PaneStopAt At, int Pane, float T);

/// <summary>How a round fares at one vehicle (<see cref="PaneRules.Pass"/>).</summary>
public enum HullPassKind
{
    /// <summary>It met the hull where no pane or opening let it in (or its range ends before the pane): stopped at <see cref="HullPass.T"/>, the hull's surface. No rider is reached.</summary>
    Hull,
    /// <summary>A reinforced pane that holds stopped it, at <see cref="HullPass.T"/>: the way in, nothing behind the pane is reached; the way out, the riders on the way are.</summary>
    Pane,
    /// <summary>It got in and has no way out (or its range ends inside): the hull takes it on its far side, <see cref="HullPass.T"/>; every rider on the way is open to it.</summary>
    Inside,
    /// <summary>It went in and out again and flies on from <see cref="HullPass.T"/> (the exit); the riders on the way are open to it.</summary>
    Through,
}

/// <summary>The outcome of a round at one vehicle: the kind, the distance <see cref="T"/> it stopped or left at, and the panes it crossed (-1: none or an opening) with their distances.</summary>
public readonly record struct HullPass(HullPassKind Kind, float T, int EntryPane, float EntryT, int ExitPane, float ExitT);

/// <summary>
/// What a vehicle's pane does to a bullet by its kind (ADR 0035). A normal pane lets the round through at once, whole or shattered; a reinforced
/// one stops it while it holds (the pane takes the damage, nothing behind it any), and lets it through once shattered. Which pane guards which
/// seat is not declared: it is the geometry of the round's way through the vehicle (<see cref="GlassRules.Through"/>). Pure, so the tests run the
/// server's own arithmetic.
/// </summary>
public static class PaneRules
{
    /// <summary>Whether a pane of <paramref name="kind"/>, shattered or not, stops a bullet that meets it.</summary>
    public static bool Stops(GlassKind kind, bool shattered) => kind == GlassKind.Reinforced && !shattered;

    /// <summary>
    /// The first pane on <paramref name="way"/> that holds (<paramref name="holds"/> says so for a pane index): the pane the round comes in by, or, when
    /// that lets it through, the pane it would leave by from inside (the riders on the way in are open to it, those past the pane are not). An open side
    /// never stops a round. <see cref="PaneStopAt.None"/> when every pane on the way lets it through.
    /// </summary>
    public static PaneStop StoppedBy(GlassPassage way, Func<int, bool> holds)
    {
        if (!way.Enters) return new PaneStop(PaneStopAt.None, -1, 0);
        if (way.EntryPane >= 0 && holds(way.EntryPane)) return new PaneStop(PaneStopAt.Entry, way.EntryPane, way.EntryT);
        if (way.Leaves && way.ExitPane >= 0 && holds(way.ExitPane)) return new PaneStop(PaneStopAt.Exit, way.ExitPane, way.ExitT);
        return new PaneStop(PaneStopAt.None, -1, 0);
    }

    /// <summary>
    /// A round that met the hit box of <paramref name="def"/> <paramref name="wall"/> metres along its ray (the ray in the vehicle's frame, <paramref name="dir"/>
    /// a unit vector, <paramref name="max"/> the round's range). The way through the panes and openings (<see cref="GlassRules.Through"/>), cut short by a
    /// reinforced pane that holds (<paramref name="shattered"/> says which panes are gone). Panes the round crossed (<see cref="HullPass.EntryPane"/>,
    /// <see cref="HullPass.ExitPane"/>) take its damage, except a pane that stopped it, which takes it where it stopped (the server's shot-glass step).
    /// </summary>
    public static HullPass Pass(VehicleDef def, Func<int, bool> shattered, Vector3 origin, Vector3 dir, float wall, float reach, float max)
    {
        var panes = def.GlassRects; var open = def.OpenSideRects;
        if (panes.Count == 0 && open.Count == 0) return new HullPass(HullPassKind.Hull, wall, -1, 0, -1, 0);
        var way = GlassRules.Through(panes, open, origin, dir, wall, reach);
        if (!way.Enters || way.EntryT > max) return new HullPass(HullPassKind.Hull, wall, -1, 0, -1, 0);
        var holding = StoppedBy(way, i => Stops(def.Glass[i].Kind, shattered(i)));
        if (holding.At == PaneStopAt.Entry) return new HullPass(HullPassKind.Pane, holding.T, -1, 0, -1, 0);
        if (holding.At == PaneStopAt.Exit && holding.T <= max) return new HullPass(HullPassKind.Pane, holding.T, way.EntryPane, way.EntryT, -1, 0);
        if (!way.Leaves || way.ExitT + 0.02f >= max)
            return new HullPass(HullPassKind.Inside, MathF.Min(MathF.Max(VehicleBox.ExitDistance(origin, dir, def), way.EntryT), max), way.EntryPane, way.EntryT, -1, 0);
        return new HullPass(HullPassKind.Through, way.ExitT, way.EntryPane, way.EntryT, way.ExitPane, way.ExitT);
    }
}
