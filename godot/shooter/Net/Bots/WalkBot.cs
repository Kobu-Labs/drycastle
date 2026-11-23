using Drycastle.Shooter.Hunts;
using Drycastle.Shooter.Menu;
using Drycastle.Shooter.Net.Client;
using Drycastle.Shooter.Net.Server;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Companions;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Hunts;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Scenery;

namespace Drycastle.Shooter.Net.Bots;

/// <summary>
/// The plain net smoke bot: a scripted walk (forward, a new heading every 1.5 s, a jump now and then). When the chat script took it to the dictation table
/// (<c>--bot-chat "/rozhlas goto"</c>) it stands still and holds E until the set is its own. Ends with <c>SHOOTER_NET_CLIENT_OK</c> after <c>--bot-seconds</c> when it moved,
/// saw another player and got snapshots.
/// </summary>
public sealed class WalkBot : ClientBot
{
    public WalkBot(IBotHost host, RozhlasTestBot? chat) : base(host, chat) { }

    public override MoveInput Step()
    {
        if (Chat is { Standing: true })
        {
            var use = !Host.HoldsRozhlasSet ? MoveButtons.Use : MoveButtons.None;
            return new MoveInput { Buttons = use, SpeedScale = 1 };
        }
        return WalkStep();
    }
}
