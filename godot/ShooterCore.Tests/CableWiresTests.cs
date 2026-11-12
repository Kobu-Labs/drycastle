using System.Numerics;
using Suchohrad.ShooterCore;
using Xunit;

namespace Suchohrad.ShooterCore.Tests;

public class CableWiresTests
{
    /// <summary>A pole at (x, z) whose crossarm (pins 0.3 m apart at 9 m) is turned yaw degrees from square to the X axis.</summary>
    static List<WireSeat> Pole(float x, float z, int pins, float yawDeg = 0, bool bracket = false, int tier = 0, float height = 9f)
    {
        var a = yawDeg * MathF.PI / 180f;
        var along = new Vector3(MathF.Sin(a), 0, MathF.Cos(a)); // the arm runs along Z when yaw is 0
        var seats = new List<WireSeat>();
        for (var i = 0; i < pins; i++)
            seats.Add(new WireSeat("line", tier, new Vector3(x, height, z) + along * ((i - (pins - 1) / 2f) * 0.3f)));
        if (bracket) seats.Add(new WireSeat("service", -1, new Vector3(x + 0.3f, 6f, z)));
        return seats;
    }

    static bool Cross(CableWire p, CableWire q)
    {
        // plan view: do the straight lines between the ends cross?
        static float Side(Vector3 a, Vector3 b, Vector3 c) => (b.X - a.X) * (c.Z - a.Z) - (b.Z - a.Z) * (c.X - a.X);
        Vector3 a = p.Points[0], b = p.Points[^1], c = q.Points[0], d = q.Points[^1];
        return Side(a, b, c) * Side(a, b, d) < 0 && Side(c, d, a) * Side(c, d, b) < 0;
    }

    [Fact]
    public void EveryPinGetsAWireAndTheyHangBelowTheSeats()
    {
        var wires = CableWires.Span(Pole(0, 0, 4), Pole(30, 0, 4), "C-0001");
        Assert.Equal(4, wires.Count);
        foreach (var w in wires)
        {
            Assert.Equal("line", w.Kind);
            Assert.Equal(9f, w.Points[0].Y, 3);
            Assert.Equal(9f, w.Points[^1].Y, 3);
            var low = w.Points.Min(p => p.Y);
            Assert.InRange(9f - low, 30f * CableWires.LineSag * 0.8f, 30f * CableWires.LineSag * 1.25f);
            Assert.Equal(w.Points[0].Z, w.Points[^1].Z, 3); // straight across: left to left, right to right
        }
    }

    [Theory]
    [InlineData(180f)] // the far pole faces the other way
    [InlineData(35f)]
    [InlineData(-70f)]
    [InlineData(250f)]
    public void WiresNeverCrossHoweverAPoleIsTurned(float yaw)
    {
        var wires = CableWires.Span(Pole(0, 0, 5, 20f), Pole(28, 6, 5, yaw), "C-0002");
        Assert.Equal(5, wires.Count);
        for (var i = 0; i < wires.Count; i++)
            for (var j = i + 1; j < wires.Count; j++)
                Assert.False(Cross(wires[i], wires[j]), $"wires {i} and {j} cross at yaw {yaw}");
    }

    [Fact]
    public void ThePoleWithFewerPinsDecidesAndSpareSeatsAreSpreadOverTheArm()
    {
        var wires = CableWires.Span(Pole(0, 0, 6), Pole(30, 0, 3), "C-0003");
        Assert.Equal(3, wires.Count);
        var used = wires.Select(w => MathF.Round(w.Points[0].Z, 2)).ToList();
        Assert.Equal(-0.75f, used[0]); // the first, one of the two middle ones and the last of six
        Assert.Equal(0.15f, MathF.Abs(used[1]));
        Assert.Equal(0.75f, used[2]);
        Assert.Single(CableWires.Span(Pole(0, 0, 5), Pole(30, 0, 1), "C-0004"));
    }

    [Fact]
    public void TheArmThatStandsAcrossTheSpanIsUsed()
    {
        // a junction pole: the top arm runs along the span to the north pole, the lower branch arm stands across it
        var junction = Pole(0, 0, 4, 0f).Concat(Pole(0, 0, 3, 90f, tier: 1, height: 8.3f)).ToList();
        var east = CableWires.Span(junction, Pole(30, 0, 4), "C-0005");
        Assert.All(east, w => Assert.Equal(9f, w.Points[0].Y, 3));
        var south = CableWires.Span(junction, Pole(0, 30, 3, 90f), "C-0006");
        Assert.Equal(3, south.Count);
        Assert.All(south, w => Assert.Equal(8.3f, w.Points[0].Y, 3));
    }

    [Fact]
    public void AServiceCableJoinsTheBracketsWhenBothPolesHaveOne()
    {
        var both = CableWires.Span(Pole(0, 0, 4, bracket: true), Pole(30, 0, 4, bracket: true), "C-0007");
        Assert.Equal(5, both.Count);
        var service = both.Single(w => w.Kind == "service");
        Assert.Equal(CableWires.ServiceRadius, service.Radius);
        Assert.Equal(6f, service.Points[0].Y, 3);
        Assert.DoesNotContain(CableWires.Span(Pole(0, 0, 4, bracket: true), Pole(30, 0, 4), "C-0007"), w => w.Kind == "service");
    }

    [Fact]
    public void ACableHangsTheSameEveryTimeAndNothingJoinsAPoleToItself()
    {
        var a = CableWires.Span(Pole(0, 0, 4), Pole(30, 0, 4), "C-0008");
        var b = CableWires.Span(Pole(0, 0, 4), Pole(30, 0, 4), "C-0008");
        Assert.Equal(a.SelectMany(w => w.Points), b.SelectMany(w => w.Points));
        Assert.Empty(CableWires.Span(Pole(0, 0, 4), Pole(0, 0, 4), "C-0009"));
        Assert.Empty(CableWires.Span(new List<WireSeat>(), Pole(30, 0, 4), "C-0010"));
    }
}
