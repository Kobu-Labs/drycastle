using System.Numerics;
using Drycastle.ShooterCore.Vehicles;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Vehicles;

/// <summary>The steering wheel the driver's rig draws is measured from the model's own rim (<see cref="SteeringWheelFit"/>).</summary>
public class SteeringWheelFitTests
{
    /// <summary>The vertices of a rim: a torus at <paramref name="centre"/>, its plane leaning <paramref name="leanDeg"/> from level towards the driver.</summary>
    static List<Vector3> Rim(Vector3 centre, float radius, float leanDeg, float tube = 0.02f)
    {
        var (s, c) = MathF.SinCos(leanDeg * MathF.PI / 180);
        var axis = new Vector3(0, c, s);
        var across = Vector3.Cross(Vector3.UnitX, axis);
        var points = new List<Vector3>();
        for (var i = 0; i < 20; i++)
        for (var j = 0; j < 6; j++)
        {
            var (a, b) = (i * MathF.Tau / 20, j * MathF.Tau / 6);
            var ring = Vector3.UnitX * MathF.Cos(a) + across * MathF.Sin(a);
            points.Add(centre + ring * (radius + tube * MathF.Cos(b)) + axis * (tube * MathF.Sin(b)));
        }
        return points;
    }

    [Theory]
    [InlineData(90f)] // upright, as the Golf's
    [InlineData(65f)] // the Humvee's
    [InlineData(45f)]
    [InlineData(20f)] // nearly flat, a lorry's
    public void A_rim_gives_back_its_centre_radius_and_lean(float lean)
    {
        var centre = new Vector3(-0.62f, 1.10f, -0.60f);
        var fit = SteeringWheelFit.From(Rim(centre, 0.185f, lean));
        Assert.NotNull(fit);
        Assert.True(Vector3.Distance(centre, fit.Value.Centre) < 0.005f, $"centre {fit.Value.Centre}");
        Assert.InRange(fit.Value.Radius, 0.18f, 0.19f);
        Assert.InRange(fit.Value.LeanDeg, lean - 1, lean + 1);
    }

    [Fact]
    public void The_Humvees_elbows_are_where_its_hand_built_rig_has_them()
    {
        var fit = new SteeringWheelFit(new Vector3(-0.62f, 1.10f, -0.60f), 0.185f, 65);
        var elbows = fit.Elbows(new Vector3(-0.62f, 1.65f, -0.13f));
        Assert.True(Vector3.Distance(new Vector3(-0.62f, 0.90f, 0.12f), elbows) < 0.03f, $"elbows {elbows}");
    }

    [Fact]
    public void A_mesh_that_is_not_a_wheel_is_no_fit()
    {
        Assert.Null(SteeringWheelFit.From(new List<Vector3> { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }));
        Assert.Null(SteeringWheelFit.From(Rim(Vector3.Zero, 1.2f, 45)));  // a road wheel
        Assert.Null(SteeringWheelFit.From(Rim(Vector3.Zero, 0.03f, 45))); // a knob
    }
}

/// <summary>The hands of a quad's or a motorcycle's rider go on the grips measured from the model's own bars (<see cref="HandlebarFit"/>).</summary>
public class HandlebarFitTests
{
    /// <summary>A bar across x at height <paramref name="y"/> with a narrow stem under its middle.</summary>
    static List<Vector3> Bars(float half, float y)
    {
        var points = new List<Vector3>();
        for (var i = 0; i <= 20; i++)
        for (var j = 0; j < 4; j++)
            points.Add(new Vector3(-half + i * half / 10, y + 0.015f * MathF.Cos(j * MathF.PI / 2), 0.015f * MathF.Sin(j * MathF.PI / 2)));
        foreach (var sx in new[] { -0.04f, 0.04f })
        foreach (var sy in new[] { -0.5f, 0f })
            points.Add(new Vector3(sx, y + sy, 0));
        return points;
    }

    [Fact]
    public void The_grips_are_the_two_ends_of_the_bar()
    {
        var fit = HandlebarFit.From(Bars(0.36f, 0.2f));
        Assert.NotNull(fit);
        Assert.True(Vector3.Distance(new Vector3(-0.30f, 0.2f, 0), fit.Value.Left) < 0.02f, $"left {fit.Value.Left}");
        Assert.True(Vector3.Distance(new Vector3(0.30f, 0.2f, 0), fit.Value.Right) < 0.02f, $"right {fit.Value.Right}");
    }

    [Fact]
    public void A_mesh_that_is_not_a_handlebar_is_no_fit()
    {
        Assert.Null(HandlebarFit.From(Bars(0.1f, 0.2f))); // a stem alone
        Assert.Null(HandlebarFit.From(Bars(1.5f, 0.2f))); // a bumper
    }
}
