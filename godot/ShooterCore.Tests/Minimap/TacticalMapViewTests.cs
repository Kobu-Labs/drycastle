using System.Numerics;
using Drycastle.ShooterCore.Minimap;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Minimap;

/// <summary>Tactical map zoom and pan: clamping, anchor-stable zoom, pan limits.</summary>
public class TacticalMapViewTests
{
    [Fact]
    public void ZoomIsClamped()
    {
        Assert.Equal(1f, TacticalMapView.ClampZoom(0.2f));
        Assert.Equal(TacticalMapView.MaxZoom, TacticalMapView.ClampZoom(99f));
    }

    [Fact]
    public void ZoomKeepsTheAnchorPointFixed()
    {
        var anchor = new Vector2(0.3f, 0.7f);
        var (z, tl) = TacticalMapView.ZoomAt(1f, Vector2.Zero, anchor, 2f);
        Assert.Equal(2f, z);
        var before = TacticalMapView.ToSheet(anchor, Vector2.Zero, 1f);
        var after = TacticalMapView.ToSheet(anchor, tl, z);
        Assert.Equal(before.X, after.X, 4);
        Assert.Equal(before.Y, after.Y, 4);
    }

    [Fact]
    public void ZoomOutToOneResetsTheWindow()
    {
        var (z, tl) = TacticalMapView.ZoomAt(2f, new Vector2(0.4f, 0.4f), new Vector2(0.5f, 0.5f), 0.1f);
        Assert.Equal(1f, z);
        Assert.Equal(Vector2.Zero, tl);
    }

    [Fact]
    public void ZoomNearTheCornerStaysInsideTheSheet()
    {
        var (z, tl) = TacticalMapView.ZoomAt(1f, Vector2.Zero, new Vector2(1f, 1f), 4f);
        Assert.Equal(0.75f, tl.X, 4);
        Assert.Equal(0.75f, tl.Y, 4);
        Assert.Equal(4f, z);
    }

    [Fact]
    public void PanIsClampedToTheSheet()
    {
        var tl = TacticalMapView.Pan(new Vector2(0.4f, 0.4f), 2f, new Vector2(5f, -5f));
        Assert.Equal(0.5f, tl.X, 4);
        Assert.Equal(0f, tl.Y, 4);
    }

    [Fact]
    public void WindowAndSheetFractionsRoundTrip()
    {
        var tl = new Vector2(0.2f, 0.3f);
        var s = TacticalMapView.ToSheet(new Vector2(0.25f, 0.75f), tl, 2.5f);
        var w = TacticalMapView.ToWindow(s, tl, 2.5f);
        Assert.Equal(0.25f, w.X, 4);
        Assert.Equal(0.75f, w.Y, 4);
    }
}
