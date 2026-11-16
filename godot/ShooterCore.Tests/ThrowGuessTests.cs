using System.Numerics;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>The driver's guessed throw of an animal (<see cref="ThrowGuess"/>): it flies at once, and hands over to the server's picture without a jump.</summary>
public class ThrowGuessTests
{
    const float Dt = 1f / 60;
    static readonly Vector3 Stand = new(5, 1, -3);

    [Fact]
    public void It_flies_from_the_first_frame_and_ahead_of_the_car()
    {
        var g = new ThrowGuess();
        Assert.True(g.Start(Stand, CorpseRules.ThrowFromCar(-Vector3.UnitZ, 20f)));
        Assert.False(g.Start(Stand, Vector3.UnitY)); // one guess at a time
        var at = Stand;
        for (var i = 0; i < 12; i++) at = g.Step(Dt, Stand, false, false); // 0.2 s: the server still shows it standing
        Assert.True(g.Active);
        Assert.True(at.Y > Stand.Y);
        Assert.True(Stand.Z - at.Z > 20f * 0.2f, "it leaves faster than the car drives, so it is not carried along inside the hull");
    }

    [Fact]
    public void The_servers_later_arc_never_pulls_the_body_back()
    {
        // the server's arc is the guessed one, seen 0.15 s later: the body is drawn on the guessed arc all the way and only goes over once both have landed
        var v = CorpseRules.ThrowFromCar(-Vector3.UnitZ, 20f);
        const int late = 9;
        Vector3 Arc(int frame)
        {
            var t = MathF.Max(0, frame - late) * Dt;
            var p = Stand + v * t + new Vector3(0, -MoveConsts.Gravity * t * t / 2, 0);
            return p.Y > Stand.Y || t == 0 ? p : default;
        }
        var g = new ThrowGuess();
        g.Start(Stand, v);
        var landed = Stand; var z = Stand.Z; var settled = -1;
        for (var i = 1; i < 400; i++)
        {
            var s = Arc(i);
            var inAir = s != default;
            if (inAir) landed = s; // the server's body stays where it came down
            var at = g.Step(Dt, inAir ? s : landed, i > late, i > late && inAir);
            if (g.Phase == ThrowGuessPhase.Confirmed || g.Phase == ThrowGuessPhase.Flying) Assert.True(at.Z <= z + 1e-4f, $"pulled back at frame {i}");
            else if (settled < 0)
            {
                settled = i;
                Assert.True((at - landed).Length() < 1.5f, "the two landing spots are near each other: what is left to glide is small");
            }
            z = at.Z;
        }
        Assert.True(settled > late);
        Assert.True(g.Ready);
        Assert.Equal(landed, g.Step(Dt, landed, true, false));
    }

    [Fact]
    public void A_guess_the_server_never_shows_glides_back()
    {
        var g = new ThrowGuess();
        g.Start(Stand, CorpseRules.ThrowFromCar(-Vector3.UnitZ, 10f));
        var steps = 0;
        while (g.Active && steps < 1000) { g.Step(Dt, Stand, false, false); steps++; }
        Assert.InRange(steps * Dt, ThrowGuess.ConfirmSeconds - 0.05f, ThrowGuess.ConfirmSeconds + 0.05f);
        Assert.False(g.Start(Stand, Vector3.UnitY)); // not again at once: the car is still on it
        var at = Stand;
        for (var i = 0; i < 600; i++) at = g.Step(Dt, Stand, false, false);
        Assert.Equal(Stand, at);
        Assert.True(g.Start(Stand, Vector3.UnitY));
    }

    [Fact]
    public void It_lands_at_the_height_it_left()
    {
        var g = new ThrowGuess();
        g.Start(Stand, new Vector3(0, 1f, -2f)); // a nudge: down again well inside the wait
        var at = Stand;
        for (var i = 0; i < 20; i++) at = g.Step(Dt, Stand, false, false);
        Assert.True(g.Active);
        Assert.Equal(Stand.Y, at.Y, 4);
    }
}
