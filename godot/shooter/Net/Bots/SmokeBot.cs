using Godot;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Weapons;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Bots;

/// <summary>A scenario check of a smoke script failed (or it waited too long).</summary>
public sealed class SmokeAbort : Exception { public SmokeAbort(string m) : base(m) { } }

/// <summary>
/// The plumbing of the scripted smoke scenarios (weapons, rpg, blast, car): a script is an iterator that yields once per physics tick and is stepped by <see cref="ClientBot.Step"/>;
/// it waits, asserts (<see cref="Check"/>) and teleports through the debug command on the server's flat test arena. Alice runs the scenario, bob is the target.
/// </summary>
public abstract class SmokeBot : ClientBot
{
    protected SmokeBot(IBotHost host, RozhlasTestBot? chat) : base(host, chat) { }

    /// <summary>The target of the scenario (alice's opponent).</summary>
    protected ushort BobId;

    protected void Check(bool ok, string what)
    {
        if (!ok) throw new SmokeAbort($"check failed: {what}");
        GD.Print($"client {Ctx.PlayerName}: ok - {what}");
    }

    protected IEnumerable<int> Wait(double seconds)
    {
        var end = Ctx.Now + seconds;
        while (Ctx.Now < end) yield return 0;
    }

    protected IEnumerable<int> WaitFor(Func<bool> cond, double timeout, string what)
    {
        var end = Ctx.Now + timeout;
        while (!cond())
        {
            if (Ctx.Now > end) throw new SmokeAbort($"timeout waiting for {what}");
            yield return 0;
        }
    }

    protected void Teleport(ushort id, NVec3 pos) => Ctx.Send(EventKind.Debug, WeaponCodec.EncodeDebugTeleport(id, pos));

    protected BotCar Car(ushort id) => Host.Car(id) ?? throw new KeyNotFoundException($"no vehicle {id}");

    protected NVec3 BobPos => Host.States.TryGetValue(BobId, out var r) ? r.Position : default;
    protected EntityState BobState => Host.States.TryGetValue(BobId, out var r) ? r.State : EntityState.Dead;
    protected int BobHealth => Host.States.TryGetValue(BobId, out var r) ? r.Health : -1;

    protected static bool NearXZ(NVec3 a, NVec3 b) => MathF.Abs(a.X - b.X) < 0.3f && MathF.Abs(a.Z - b.Z) < 0.3f;
    protected static float XZ(NVec3 v) => MathF.Sqrt(v.X * v.X + v.Z * v.Z);

    protected IEnumerable<int> WaitBobAlive()
    {
        foreach (var x in WaitFor(() => Ctx.Match.Phase == MatchPhase.Running && BobState == EntityState.Alive && Host.MyState == EntityState.Alive, 8, "bob alive and unprotected")) yield return x;
    }
}
