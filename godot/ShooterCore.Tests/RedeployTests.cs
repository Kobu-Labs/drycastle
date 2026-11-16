using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>The player's own Respawn (#284): the message and the match's ForceDeath.</summary>
public class RedeployTests
{
    sealed class Rig
    {
        public double T;
        public readonly MatchSim M;
        public readonly List<MatchEvent> Ev = new();
        public Rig()
        {
            M = new MatchSim(new MatchConfig(), () => T, new[] { new Vector3(0, 0, 0), new Vector3(100, 0, 0) }, new Random(1));
            M.Event += Ev.Add;
        }
        public void Advance(double s) { T += s; M.Update(); }
    }

    [Fact]
    public void TheMessageRoundTripsASlotAndNoSlot()
    {
        Assert.True(Redeploy.TryDecode(Redeploy.Encode(3), out var slot) && slot == 3);
        Assert.True(Redeploy.TryDecode(Redeploy.Encode(null), out slot) && slot == null);
        Assert.False(Redeploy.TryDecode(Array.Empty<byte>(), out _));
        Assert.False(Redeploy.TryDecode(new byte[] { 1, 2 }, out _));
        var msg = Wire.DecodeReliable(Wire.Encode(new EventMsg((byte)EventKind.Redeploy, Redeploy.Encode(2))));
        Assert.Equal((byte)EventKind.Redeploy, ((EventMsg)msg!).Kind);
    }

    [Fact]
    public void ForceDeathIsASuicideWithTheNormalRespawn()
    {
        var r = new Rig();
        r.M.AddPlayer(1, "a"); r.M.AddPlayer(2, "b");
        Assert.True(r.M.ForceDeath(1)); // even under spawn protection: no damage gate applies
        Assert.Equal(1, r.M.Get(1)!.Deaths);
        Assert.Equal(0, r.M.Get(1)!.Kills);
        Assert.Equal(0, r.M.Get(2)!.Kills);
        Assert.Contains(new PlayerKilled(1, 1, SpecialWeapons.Redeploy), r.Ev);
        Assert.Contains(new PlayerDied(1, 1, new MatchConfig().RespawnDelay), r.Ev);
        Assert.Equal(EntityState.Dead, r.M.StateOf(1));
        r.Advance(new MatchConfig().RespawnDelay + 0.1);
        Assert.NotEqual(EntityState.Dead, r.M.StateOf(1));
    }

    [Fact]
    public void ForceDeathNeedsALivingFreePlayerInARunningRound()
    {
        var r = new Rig();
        Assert.False(r.M.ForceDeath(1)); // not in the match
        r.M.AddPlayer(1, "a");
        r.M.SetHeld(1, true);
        Assert.False(r.M.CanForceDeath(1)); // held by the demon
        Assert.False(r.M.ForceDeath(1));
        r.M.SetHeld(1, false);
        Assert.True(r.M.ForceDeath(1));
        Assert.False(r.M.ForceDeath(1)); // already dead: no second death
        Assert.Equal(1, r.M.Get(1)!.Deaths);
    }
}
