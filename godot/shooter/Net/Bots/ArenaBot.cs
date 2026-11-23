using Godot;
using Drycastle.ShooterCore.Movement;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Bots;

/// <summary>
/// A scenario on the server's flat test arena (weapons, rpg, blast smoke): role "a" (alice) walks through it, aiming and pressing keys by script, role "b" (bob) stands still as the target
/// and checks the events he receives. Both end the run with SHOOTER_{tag}_CLIENT_OK, or _FAIL on the first failed check.
/// </summary>
public abstract class ArenaBot : SmokeBot
{
    readonly bool _alice;
    IEnumerator<int>? _script;
    protected MoveButtons _pulse;
    protected int _slot = 1;
    protected float _yaw, _pitch;

    protected ArenaBot(IBotHost host, RozhlasTestBot? chat, string role) : base(host, chat) { _alice = role == "a"; }

    /// <summary>What the result line names: WEAPONS, RPG or BLAST.</summary>
    protected abstract string Tag { get; }
    protected abstract IEnumerable<int> Alice();
    protected abstract IEnumerable<int> Bob();

    public override MoveInput Step()
    {
        try
        {
            _script ??= (_alice ? Alice() : Bob()).GetEnumerator();
            if (!Host.LinkEnded && !_script.MoveNext()) _script = null;
        }
        catch (SmokeAbort e) { Result(false, e.Message); }
        if (_alice) { Host.WantSlot = _slot; Ctx.Me!.Pitch = _pitch; }
        var mi = new MoveInput { Yaw = _alice ? _yaw : 0, Buttons = Ctx.Dead ? MoveButtons.None : _pulse, SpeedScale = 1 };
        _pulse = MoveButtons.None;
        return mi;
    }

    public override bool TimedOut()
    {
        Result(false, "timeout");
        return true;
    }

    protected void Result(bool ok, string why)
    {
        if (Host.LinkEnded) return;
        Host.Leave();
        if (!ok) GD.PrintErr($"client {Ctx.PlayerName}: weapons smoke failure: {why}");
        GD.Print(ok ? $"SHOOTER_{Tag}_CLIENT_OK" : $"SHOOTER_{Tag}_CLIENT_FAIL");
        Host.Quit(ok);
    }

    protected static float AimYaw(NVec3 from, NVec3 to) => MathF.Atan2(-(to.X - from.X), -(to.Z - from.Z));
    protected static float AimPitch(NVec3 from, NVec3 to, float height)
    {
        var h = MathF.Sqrt((to.X - from.X) * (to.X - from.X) + (to.Z - from.Z) * (to.Z - from.Z));
        return MathF.Atan2(to.Y + height - (from.Y + MoveConsts.EyeHeight), h);
    }

    /// <summary>Put alice and bob at the given arena positions, wait until both stand there, and face bob (aiming at <paramref name="aimHeight"/>).</summary>
    protected IEnumerable<int> Place(NVec3 alice, NVec3 bob, float aimHeight)
    {
        Teleport(Ctx.MyId, alice); Teleport(BobId, bob);
        foreach (var x in WaitFor(() => Host.OwnSpawned && !Ctx.Dead && NearXZ(Ctx.Me!.State.Position, alice) && NearXZ(BobPos, bob) && Ctx.Me!.State.OnFloor && BobPos.Y < 0.05f, 4, "teleport and landing")) yield return x;
        foreach (var x in Wait(0.3)) yield return x;
        _yaw = AimYaw(alice, bob);
        _pitch = AimPitch(alice, bob, aimHeight);
    }
}
