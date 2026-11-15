using System;
using System.Collections.Generic;
using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// Headless movement parity probe (I-rubber-banding, round 2): does a reconcile replay reproduce the sequential run? A seeded walk with jumps
/// runs once from a settled spawn (the server model: every step follows the one before it). Then each trial rewinds to the state after step k
/// (<see cref="Player.SetState"/>, as a reconcile does) and replays steps k+1..k+2S. The replayed result must equal the sequential state
/// after k+2S. The body's own floor flag (Godot's <c>IsOnFloor</c>, which drives the floor snap) is not part of the rewind: a trial
/// that starts with the body's floor flag different from the step's is the case the probe counts.
/// Prints one <c>MOVE_PARITY</c> line and exits 0. Run: <c>--role client --move-parity --map DIR</c> (no server).
/// </summary>
public partial class MoveParity : Node
{
    public Player Player = null!;
    public Vector3 Spawn;

    /// <summary>Steps of the walk, the seed, and whether the replay uses <see cref="Player.SetStateForReplay"/> (the production path) or a plain <see cref="Player.SetState"/>.</summary>
    public int Steps = 900, Seed = 20261113;
    public bool Production;
    const int Span = 10;
    const float Dt = 1f / Wire.TickRate;
    int _frame;

    public override void _Ready() => Player.SetPhysicsProcess(false); // this probe steps the body itself

    public override void _PhysicsProcess(double delta)
    {
        if (++_frame == 1) { Run(); SetPhysicsProcess(false); }
    }

    static MoveInput InputFor(int i, Random rng, ref float yaw, ref int sprint)
    {
        if (i % 30 == 0) yaw = (float)(rng.NextDouble() * Math.Tau);
        if (i % 60 == 0) sprint = rng.Next(2);
        var jump = rng.Next(40) == 0 && i % 2 == 0;
        var buttons = MoveButtons.None;
        if (jump) buttons |= MoveButtons.Jump;
        if (sprint == 1) buttons |= MoveButtons.Sprint;
        return new MoveInput { MoveX = (float)(rng.NextDouble() * 0.6 - 0.3), MoveY = rng.Next(10) < 9 ? 1 : 0, Yaw = yaw, Buttons = buttons, SpeedScale = 1 };
    }

    void Run()
    {
        // settle on the ground at the spawn
        Player.Teleport(Spawn + Vector3.Up * 0.5f);
        for (var i = 0; i < 60; i++) Player.Simulate(new MoveInput { SpeedScale = 1 }, Dt);

        var rng = new Random(Seed);
        var n = Steps;
        var inputs = new MoveInput[n];
        var yaw = 0f; var sprint = 0;
        for (var i = 0; i < n; i++) inputs[i] = InputFor(i, rng, ref yaw, ref sprint);

        // the sequential run: the server model
        var states = new MoveState[n];
        for (var i = 0; i < n; i++) { Player.Simulate(inputs[i], Dt); states[i] = Player.State; }

        var trials = 0; var mismatch = 0; var over1 = 0; var over5 = 0; var over5Mismatch = 0; var over5Vertical = 0; float maxMm = 0; float maxMmVert = 0;
        var worst = new List<string>();
        for (var k = 0; k + 2 * Span < n; k += 3)
        {
            trials++;
            var end = k + 2 * Span;
            var bodyFloor = Player.IsOnFloor(); // the body's floor flag before the rewind: the last move's, i.e. the newest step's
            var stepFloor = states[k].OnFloor;   // the flag the step itself carries (what the server had)
            var mis = bodyFloor != stepFloor;
            if (mis) mismatch++;

            if (Production) Player.SetStateForReplay(states[k]);
            else Player.SetState(states[k]);
            for (var i = k + 1; i <= end; i++) Player.Simulate(inputs[i], Dt);
            var d = Player.State.Position - states[end].Position;
            var len = d.Length();
            var vert = Math.Abs(d.Y) > 0.5f * len;
            maxMm = Math.Max(maxMm, len * 1000f);
            if (len > 0.001f) over1++;
            if (len > 0.005f) { over5++; if (mis) over5Mismatch++; if (vert) over5Vertical++; }
            if (len > 0.005f && worst.Count < 4) worst.Add($"k={k} dy={d.Y * 100:F1}cm dxz={MathF.Sqrt(d.X * d.X + d.Z * d.Z) * 100:F1}cm floor body={bodyFloor} step={stepFloor}");
            maxMmVert = Math.Max(maxMmVert, Math.Abs(d.Y) * 1000f);
        }
        GD.Print($"MOVE_PARITY trials={trials} steps={n} floor_flag_mismatch={mismatch} diverged_over_1mm={over1} diverged_over_5mm={over5} over_5mm_with_mismatch={over5Mismatch} over_5mm_vertical={over5Vertical} max_mm={maxMm:F1} max_vertical_mm={maxMmVert:F1} production={Production}");
        foreach (var w in worst) GD.Print($"MOVE_PARITY worst {w}");
        GD.Print("MOVE_PARITY_DONE");
        GetTree().Quit(0);
    }
}
