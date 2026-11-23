using System.Numerics;
using Xunit;
using Drycastle.ShooterCore.Corpses;
using Drycastle.ShooterCore.Events;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Wildlife;

namespace Drycastle.ShooterCore.Tests.Vehicles;

/// <summary>The driver's guessed throw of an animal (<see cref="ThrowGuess"/>): it flies at once, and hands over to the server's picture without a jump.</summary>
public class ThrowGuessTests
{
    const float Dt = 1f / 60;
    static readonly Vector3 Stand = new(5, 1, -3);

    [Fact]
    public void A_living_body_the_server_threw_still_flies_while_it_is_faster_than_anybody_runs()
    {
        // #411: hit at 12 m/s (hurts, may not kill), the body leaves at 14.7 m/s along the ground
        var launch = CorpseRules.ThrowFromCar(-Vector3.UnitZ, 12f);
        var along = new Vector3(launch.X, 0, launch.Z).Length();
        Assert.True(ThrowGuess.ServerStillFlies(along, along));
        Assert.True(ThrowGuess.ServerStillFlies(along * 0.8f, along)); // slowed in the air
        Assert.False(ThrowGuess.ServerStillFlies(MoveConsts.SprintSpeed, along)); // down, and running off
        Assert.False(ThrowGuess.ServerStillFlies(0, along));
    }

    [Fact]
    public void A_vehicle_throws_a_living_body_into_the_same_tumble_as_the_bigfoot_and_it_gets_up()
    {
        var f = new BodyFlingTracker();
        f.Thrown();
        Assert.Equal(BodyFling.Flying, f.Phase);
        f.Thrown(); // hit again in the air: it flies on, the clock from the start
        Assert.Equal(BodyFling.Flying, f.Phase);
        for (var i = 0; i < 60; i++) f.Tick(Dt, 12f, false); // a second in the air
        Assert.Equal(BodyFling.Flying, f.Phase);
        f.Tick(Dt, 0.5f, true);
        Assert.Equal(BodyFling.Lying, f.Phase);
        f.Thrown(); // run over again where it lies
        Assert.Equal(BodyFling.Flying, f.Phase);
        for (var i = 0; i < 30; i++) f.Tick(Dt, 12f, false);
        f.Tick(Dt, 0.5f, true);
        for (var i = 0; i < 40; i++) f.Tick(Dt, 0, true);
        Assert.Equal(BodyFling.Landing, f.Phase);
        f.StoodUp();
        Assert.False(f.Active);
        // in the Bigfoot's fist it stays the Bigfoot's
        f.Grabbed(); f.Thrown();
        Assert.Equal(BodyFling.Held, f.Phase);
        Assert.Equal((ushort)0x0A0B, PlayerFlungCodec.Decode(PlayerFlungCodec.Encode(0x0A0B)));
        Assert.Null(PlayerFlungCodec.Decode(new byte[] { 1 }));
    }

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
        // the server's body: the same arc and the same roll along the ground (ThrownRoll), begun `late` frames after the guess
        var sp = Stand; var sv = v; var rolling = false; var moving = true;
        Vector3 Arc(int frame)
        {
            if (frame <= late || !moving) return moving ? sp : default;
            if (rolling) { sp += sv * Dt; sp.Y = Stand.Y; if (ThrownRoll.Roll(ref sv, Dt)) moving = false; return sp; }
            sv.Y -= MoveConsts.Gravity * Dt;
            sp += sv * Dt;
            if (sp.Y <= Stand.Y) { sp.Y = Stand.Y; sv = ThrownRoll.Land(sv); rolling = true; }
            return sp;
        }
        var g = new ThrowGuess();
        g.Start(Stand, v);
        var landed = Stand; var z = Stand.Z; var settled = -1;
        for (var i = 1; i < 400; i++)
        {
            var s = Arc(i);
            var inAir = s != default;
            if (inAir) landed = s; // the server's body stays where it came down
            var at = g.Step(Dt, inAir ? s : landed, i > late, i > late && inAir, Stand.Y);
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
    public void It_lands_on_the_ground_under_it_and_rolls_on()
    {
        var g = new ThrowGuess();
        g.Start(Stand, new Vector3(0, 1f, -6f)); // a nudge: down again well inside the wait
        var at = Stand; var touched = Stand; var down = false;
        for (var i = 0; i < 30; i++)
        {
            at = g.Step(Dt, Stand, false, false, Stand.Y + 0.2f); // the ground there is higher than where it left
            if (!down && at.Y == Stand.Y + 0.2f) { down = true; touched = at; }
        }
        Assert.True(g.Active);
        Assert.True(down);
        Assert.Equal(Stand.Y + 0.2f, at.Y, 4);
        Assert.True(at.Z < touched.Z - 0.3f, "it rolls on with part of its speed");
        Assert.False(g.Moving); // and comes to rest
    }
}
