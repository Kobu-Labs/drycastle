using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>
/// Input starvation (A-08, I-rubber-banding round 2). A server tick with no input queued does not step the body, while the client predicted the
/// step. The reconcile compares the predicted and the server state at the acknowledged input sequence, not at a tick, so a late server step is
/// not a divergence as long as the server steps the inputs in order, once each. These tests pin that on the shared movement step, with the
/// server's queue semantics (<see cref="CatchUpBudget"/>, the 8-input cap, the highest sequence received) and the client's redundancy of three
/// inputs per packet. The alternative, stepping again with the last input while the queue is empty, is modelled too: it adds a step the client
/// did not predict at that sequence, so it is measured as a divergence.
/// </summary>
public class StarvationTests
{
    const float Dt = 1f / 30f;

    /// <summary>
    /// The client's inputs, predicted at once; then the server, one arrival per tick (a packet of three inputs, the highest sequence received
    /// in order), an optional drop of the packet at every <paramref name="dropEvery"/>-th tick, and the queue's catch-up. Returns the predicted
    /// position after each sequence, the server's position after each acknowledged sequence, and the ticks with an empty queue.
    /// </summary>
    static (List<Vector3> Predicted, Dictionary<ushort, Vector3> ServerAfterAck, int Starved) Run(int count, int dropEvery, bool repeatLast, int seed)
    {
        var rng = new Random(seed);
        var all = new NetInput[count];
        var yaw = 0f;
        for (var i = 0; i < count; i++)
        {
            if (i % 20 == 0) yaw = (float)(rng.NextDouble() * MathF.Tau);
            all[i] = NetInput.From((ushort)(i + 1), new MoveInput { MoveY = 1, Yaw = yaw, SpeedScale = 1, Buttons = i % 45 == 0 ? MoveButtons.Jump : MoveButtons.None }, 0f, 0);
        }
        var predicted = new List<Vector3>();
        var client = new MoveState();
        foreach (var n in all)
        {
            MovementStep.Step(ref client, n.ToMove(1f), Dt);
            MovementStep.Integrate(ref client, Dt);
            predicted.Add(client.Position);
        }

        var server = new MoveState();
        var queue = new Queue<NetInput>();
        var budget = new CatchUpBudget();
        var afterAck = new Dictionary<ushort, Vector3>();
        var starved = 0;
        var highest = -1;
        NetInput? lastApplied = null;
        for (var tick = 0; tick < count + 60; tick++)
        {
            // the packet sent at this tick: the inputs up to the client's current one, three of them
            if (tick < count && !(dropEvery > 0 && tick % dropEvery == dropEvery - 1))
            {
                for (var j = Math.Max(0, tick - 2); j <= tick; j++)
                    if (j > highest) { queue.Enqueue(all[j]); highest = j; }
            }
            while (queue.Count > 8) queue.Dequeue(); // a stalled sim never builds unbounded lag (NetServer.OnPacket)
            var steps = budget.Steps(queue.Count);
            if (queue.Count == 0)
            {
                if (tick < count) starved++; // the ticks after the walk drain the queue; they are not starvation
                if (repeatLast && lastApplied is { } again) { MovementStep.Step(ref server, again.ToMove(1f), Dt); MovementStep.Integrate(ref server, Dt); }
            }
            for (var k = 0; k < steps && queue.Count > 0; k++)
            {
                var i = queue.Dequeue();
                MovementStep.Step(ref server, i.ToMove(1f), Dt);
                MovementStep.Integrate(ref server, Dt);
                afterAck[i.Seq] = server.Position;
                lastApplied = i;
            }
        }
        return (predicted, afterAck, starved);
    }

    static float Worst(List<Vector3> predicted, Dictionary<ushort, Vector3> afterAck)
    {
        var worst = 0f;
        foreach (var (seq, pos) in afterAck) worst = MathF.Max(worst, Vector3.Distance(pos, predicted[seq - 1]));
        return worst;
    }

    [Fact]
    public void AStarvedServerTickDoesNotDivergeFromThePredictionAtTheSameSequence()
    {
        var (predicted, afterAck, starved) = Run(600, dropEvery: 25, repeatLast: false, seed: 3);
        Assert.True(starved > 0, "the scenario must starve the server");
        Assert.True(afterAck.Count > 500, "every input is eventually stepped once");
        Assert.True(Worst(predicted, afterAck) < 1e-4f, $"largest divergence at an acknowledged sequence {Worst(predicted, afterAck)} m");
    }

    [Fact]
    public void WithoutStarvationTheServerMatchesThePredictionExactly()
    {
        var (predicted, afterAck, starved) = Run(300, dropEvery: 0, repeatLast: false, seed: 9);
        Assert.Equal(0, starved);
        Assert.True(Worst(predicted, afterAck) < 1e-5f);
    }

    [Fact]
    public void RepeatingTheLastInputWhileStarvedWouldDivergeFromThePrediction()
    {
        // the rejected option: the extra step has no sequence on the client, so every later acknowledged state is one step ahead
        var (predicted, afterAck, starved) = Run(600, dropEvery: 25, repeatLast: true, seed: 3);
        Assert.True(starved > 0);
        Assert.True(Worst(predicted, afterAck) > 1e-3f);
    }

    [Fact]
    public void TheCatchUpBudgetAddsAtMostItsCreditAndNeverADoubleStepEveryTick()
    {
        var b = new CatchUpBudget();
        var total = 0;
        // a queue that stays above the threshold: at most the initial credit (8) plus a quarter per tick
        for (var t = 0; t < 40; t++) total += b.Steps(5);
        Assert.InRange(total, 40, 40 + 8 + 10);
        Assert.Equal(1, new CatchUpBudget().Steps(3)); // at or below the threshold: one input
    }
}
