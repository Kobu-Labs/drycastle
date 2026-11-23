using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// A scripted player for the headless smoke and load runs (<c>--headless-bot</c>): each physics tick the client asks <see cref="Step"/> for the input the bot "pressed", and tells it about
/// the snapshots, the prediction errors and the own spawn and death through the hooks below. A bot ends the run itself (a report line and <see cref="IBotHost.Quit"/>) or by running out of
/// <see cref="IBotHost.Seconds"/> (<see cref="TimedOut"/>).
/// </summary>
public abstract class ClientBot
{
    protected readonly IBotHost Host;
    /// <summary>The chat and rozhlas feed script running beside this bot, if the run has one.</summary>
    protected readonly RozhlasTestBot? Chat;

    protected ClientBot(IBotHost host, RozhlasTestBot? chat)
    {
        Host = host;
        Chat = chat;
    }

    protected ClientContext Ctx => Host.Ctx;

    /// <summary>The input of this physics tick.</summary>
    public abstract MoveInput Step();

    /// <summary>The bot's seconds ran out. True when the physics tick ends here (the usual), false when it goes on to send one more input.</summary>
    public virtual bool TimedOut()
    {
        FinishWalk();
        return true;
    }

    /// <summary>A snapshot was accepted (before it is applied).</summary>
    public virtual void OnSnapshot(SnapshotHeader h) { }
    /// <summary>A snapshot was applied.</summary>
    public virtual void AfterSnapshot(SnapshotHeader h) { }
    /// <summary>The acked prediction was compared with the server's position: <paramref name="err"/> metres apart.</summary>
    public virtual void OnPredictionError(float err) { }
    /// <summary>A reliable event arrived (after every feature saw it).</summary>
    public virtual void OnEvent(EventKind kind, byte[] payload) { }
    /// <summary>The own spawn event: <paramref name="protection"/> seconds of spawn protection.</summary>
    public virtual void OnSpawn(float protection) { }
    public virtual void OnOwnDeath() { }
    /// <summary>The own record of a snapshot, once the player is spawned and predicted.</summary>
    public virtual void OnOwnState(EntityRecord rec) { }

    /// <summary>The scripted walk of the plain net smoke: forward, a new heading every 1.5 s, a jump now and then.</summary>
    protected MoveInput WalkStep()
    {
        var phase = (int)(Host.Elapsed / 1.5);
        var yaw = (float)(phase * Math.PI / 2 + (Ctx.PlayerName.GetHashCode() & 3) * Math.PI / 2);
        var jump = ((int)(Host.Elapsed * 30) % 45) == 0 ? MoveButtons.Jump : MoveButtons.None;
        return new MoveInput { MoveY = 1, Yaw = yaw, Buttons = jump, SpeedScale = 1 };
    }

    /// <summary>The net smoke's verdict (the run of the plain walker, and of the car loop): it moved, saw another player, and got snapshots.</summary>
    protected void FinishWalk()
    {
        Host.Leave();
        Chat?.Report();
        var own = Host.OwnTravel;
        var best = Host.BestRemoteTravel;
        GD.Print($"client {Ctx.PlayerName}: moved {own:F1} m (predicted), saw {Host.Remotes.Count()} remote player(s), remote moved up to {best:F1} m, snapshots {Host.Snaps}, reconciliations {Host.Recon}, max error {Host.MaxErr:F3} m");
        var ok = own >= 2f && Host.Remotes.Count() >= 1 && best >= 1f && Host.Snaps > 30;
        GD.Print(ok ? "SHOOTER_NET_CLIENT_OK" : "SHOOTER_NET_CLIENT_FAIL");
        Host.Quit(ok);
    }
}
