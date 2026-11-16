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
        for (var i = 0; i < 12; i++) at = g.Step(Dt, Stand, false); // 0.2 s: the server still shows it standing
        Assert.True(g.Flying);
        Assert.True(at.Y > Stand.Y);
        Assert.True(Stand.Z - at.Z > 20f * 0.2f, "it leaves faster than the car drives, so it is not carried along inside the hull");
    }

    [Fact]
    public void The_servers_throw_takes_over_without_a_jump()
    {
        var g = new ThrowGuess();
        g.Start(Stand, CorpseRules.ThrowFromCar(-Vector3.UnitZ, 20f));
        var before = Stand;
        for (var i = 0; i < 9; i++) before = g.Step(Dt, Stand, false);
        var server = Stand + new Vector3(0, 0.1f, -0.4f); // the server's arc has just begun
        var at = g.Step(Dt, server, true);
        Assert.Equal(ThrowGuessPhase.Settling, g.Phase);
        Assert.True((at - before).Length() < 0.6f, "one frame of the arc, not a jump back to the server's place");
        var next = g.Step(Dt, server, true);
        Assert.True((next - at).Length() < 0.2f);
        for (var i = 0; i < 600; i++) next = g.Step(Dt, server, true);
        Assert.Equal(server, next); // the offset is gone
        Assert.True(g.Ready);
    }

    [Fact]
    public void A_guess_the_server_never_shows_glides_back()
    {
        var g = new ThrowGuess();
        g.Start(Stand, CorpseRules.ThrowFromCar(-Vector3.UnitZ, 10f));
        var steps = 0;
        while (g.Flying && steps < 1000) { g.Step(Dt, Stand, false); steps++; }
        Assert.InRange(steps * Dt, ThrowGuess.ConfirmSeconds - 0.05f, ThrowGuess.ConfirmSeconds + 0.05f);
        Assert.False(g.Start(Stand, Vector3.UnitY)); // not again at once: the car is still on it
        var at = Stand;
        for (var i = 0; i < 600; i++) at = g.Step(Dt, Stand, false);
        Assert.Equal(Stand, at);
        Assert.True(g.Start(Stand, Vector3.UnitY));
    }

    [Fact]
    public void It_lands_at_the_height_it_left()
    {
        var g = new ThrowGuess();
        g.Start(Stand, new Vector3(0, 1f, -2f)); // a nudge: down again well inside the wait
        var at = Stand;
        for (var i = 0; i < 20; i++) at = g.Step(Dt, Stand, false);
        Assert.True(g.Flying);
        Assert.Equal(Stand.Y, at.Y, 4);
    }
}
