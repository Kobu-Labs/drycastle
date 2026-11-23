using System.Numerics;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Wildlife;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Wildlife;

public class RainbowTrailTests
{
    static void Walk(RainbowTrail t, float metres, float speed, float dt = 0.05f, float z0 = 0)
    {
        var x = 0f;
        for (var s = 0f; x < metres; s += dt) { x = speed * s; t.Update(dt, new Vector3(x, 1, z0), true); }
    }

    [Fact]
    public void SamplesByDistanceNotPerFrame()
    {
        var t = new RainbowTrail();
        Walk(t, 4f, 2f);
        Assert.InRange(t.Count, 8, 12); // ~0.4 m apart
    }

    [Fact]
    public void KeepsAtMostMaxPoints()
    {
        var t = new RainbowTrail();
        Walk(t, 200f, 20f, 0.02f);
        Assert.Equal(RainbowTrail.MaxPoints, t.Count);
    }

    [Fact]
    public void StandingStillAddsNothingAndTheTrailExpires()
    {
        var t = new RainbowTrail();
        Walk(t, 3f, 2f);
        var count = t.Count;
        for (var i = 0; i < 20; i++) t.Update(0.1f, new Vector3(3, 1, 0), true);
        Assert.Equal(count, t.Count);
        for (var i = 0; i < 100; i++) t.Update(0.1f, new Vector3(3, 1, 0), true);
        Assert.True(t.Count <= 1); // the point the body stands on may be re-taken, the old ones are gone
    }

    [Fact]
    public void TeleportClearsTheHistory()
    {
        var t = new RainbowTrail();
        Walk(t, 4f, 2f);
        t.Update(0.05f, new Vector3(500, 1, 0), true);
        Assert.Equal(1, t.Count);
        Assert.Equal(500f, t.PointAt(0).X);
    }

    [Fact]
    public void DeadAddsNothingAndFadesOut()
    {
        var t = new RainbowTrail();
        Walk(t, 4f, 2f);
        Assert.True(t.Count > 0);
        t.Update(0.05f, new Vector3(50, 1, 0), false);
        Assert.False(t.Attached);
        var count = t.Count;
        t.Update(0.05f, new Vector3(51, 1, 0), false);
        Assert.Equal(count, t.Count);
        for (var i = 0; i < 100; i++) t.Update(0.1f, Vector3.Zero, false);
        Assert.True(t.Empty);
    }

    [Fact]
    public void FadeRunsFromOneToZero()
    {
        Assert.Equal(1f, RainbowTrail.Fade(0));
        Assert.Equal(0f, RainbowTrail.Fade(RainbowTrail.LifeSeconds));
        Assert.Equal(0f, RainbowTrail.Fade(RainbowTrail.LifeSeconds * 2));
        Assert.True(RainbowTrail.Fade(2) > RainbowTrail.Fade(4));
    }

    [Fact]
    public void RowsGoFromRedToViolet()
    {
        Assert.Equal(0f, RainbowTrail.RowHue(0));
        Assert.Equal(RainbowTrail.VioletHue, RainbowTrail.RowHue(RainbowTrail.Rows - 1), 4);
        var red = RainbowTrail.HueRgb(0);
        Assert.True(red.X > 0.99f && red.Y < 0.2f && red.Z < 0.2f);
        var green = RainbowTrail.HueRgb(1f / 3f);
        Assert.True(green.Y > 0.99f && green.X < 0.2f);
        var violet = RainbowTrail.HueRgb(RainbowTrail.VioletHue);
        Assert.True(violet.X > 0.3f && violet.Z > 0.99f && violet.Y < 0.2f);
    }

    [Fact]
    public void BuildsATubeRoundThePath()
    {
        var t = new RainbowTrail();
        Walk(t, 6f, 2f);
        t.Update(0.05f, t.PointAt(t.Count - 1) + new Vector3(0.2f, 0, 0), true); // the live head, ahead of the newest sample
        var v = new Vector3[RainbowTrail.MaxVertices]; var nm = new Vector3[RainbowTrail.MaxVertices]; var c = new Vector4[RainbowTrail.MaxVertices]; var uv = new Vector2[RainbowTrail.MaxVertices]; var ix = new int[RainbowTrail.MaxIndices];
        Assert.True(t.Build(v, nm, c, uv, ix, out var nv, out var ni));
        Assert.Equal((t.Count + 1) * RainbowTrail.Ring, nv); // history and the live head
        Assert.Equal(t.Count * RainbowTrail.Ring * 6, ni);
        Assert.All(ix.Take(ni), i => Assert.InRange(i, 0, nv - 1));
        // a ring: the first vertex on top (red), the opposite one at the bottom (violet), the quarter ones out to the sides: it has height and thickness
        const int Top = 0, Side = RainbowTrail.Ring / 4, Bottom = RainbowTrail.Ring / 2;
        var mid = (t.Count / 2) * RainbowTrail.Ring;
        Assert.True(v[mid + Top].Y - v[mid + Bottom].Y > RainbowTrail.HalfHeight);
        Assert.True(MathF.Abs(v[mid + Side].Z - v[mid + Side + Bottom].Z) > RainbowTrail.HalfWidth);
        Assert.Equal(0f, uv[mid + Top].Y, 3);
        Assert.Equal(1f, uv[mid + Bottom].Y, 3);
        Assert.True(nm[mid + Top].Y > 0.99f && nm[mid + Bottom].Y < -0.99f);
        Assert.True(c[mid + Top].X > 0.99f && c[mid + Top].Z < 0.2f);       // red
        Assert.True(c[mid + Bottom].Z > 0.99f && c[mid + Bottom].Y < 0.2f); // violet
        // it swells out of the body: the ring at the live head is a fraction of the one a few metres behind
        var head = nv - RainbowTrail.Ring;
        var behind = head - 6 * RainbowTrail.Ring;
        Assert.True(v[head + Top].Y - v[head + Bottom].Y < 0.5f * (v[behind + Top].Y - v[behind + Bottom].Y));
        Assert.Equal(RainbowTrail.PeakAlpha, c[nv - 1].W, 3);
        Assert.True(c[0].W < c[nv - 1].W); // the tail is fainter than the head
    }

    [Fact]
    public void NothingToBuildFromOnePoint()
    {
        var t = new RainbowTrail();
        t.Update(0.05f, new Vector3(1, 1, 1), true);
        var v = new Vector3[RainbowTrail.MaxVertices]; var nm = new Vector3[RainbowTrail.MaxVertices]; var c = new Vector4[RainbowTrail.MaxVertices]; var uv = new Vector2[RainbowTrail.MaxVertices]; var ix = new int[RainbowTrail.MaxIndices];
        Assert.False(t.Build(v, nm, c, uv, ix, out _, out _));
    }

    const string Deer = """{"id":"deer","name":"Deer","model":"animated-animals/deer.glb","scale":0.33,"hp":100,"walk_speed":1.5,"run_speed":10,"flee_radius":20,"wander_radius":30,"hit_box":[0.5,1,1.5]""";

    [Fact]
    public void AnimalFxIsAnOptionalLowerCaseId()
    {
        Assert.Equal("", AnimalsFile.Parse($$"""{"version":1,"animals":[{{Deer}}}]}""").Animals[0].Fx);
        Assert.Equal("unicorn", AnimalsFile.Parse($$"""{"version":1,"animals":[{{Deer}},"fx":"unicorn"}]}""").Animals[0].Fx);
        Assert.Throws<DataFileException>(() => AnimalsFile.Parse($$"""{"version":1,"animals":[{{Deer}},"fx":"Uni Corn"}]}"""));
    }
}
