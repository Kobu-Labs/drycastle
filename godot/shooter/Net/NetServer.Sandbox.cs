using Godot;
using Suchohrad.ShooterCore;

namespace Suchohrad.Shooter;

/// <summary>
/// Sandbox mode on the server (#156): the grant at hello, the status notice, the on-demand killstreak grant, and the
/// rule that a sandbox player's kills are not scored. The rules are in <see cref="SandboxRules"/>; the damage immunity and
/// the score rules are in <see cref="ShooterCore.MatchSim"/>; unlimited ammo is <see cref="PlayerWeapons.Unlimited"/>.
/// </summary>
public partial class NetServer
{
    /// <summary>The hello's flags: the sandbox request is kept on the client until the join sends the answer.</summary>
    void SandboxOnHello(Client c, byte flags)
    {
        c.SandboxRequested = (flags & HelloFlags.Sandbox) != 0;
        c.Sandbox = AllowSandbox && c.SandboxRequested;
    }

    /// <summary>Sends the answer to a sandbox request (no message when the client did not ask).</summary>
    void SendSandboxStatus(Client c)
    {
        if (SandboxRules.Decide(c.SandboxRequested, AllowSandbox) is not { } answer) return;
        Send(c, new EventMsg((byte)EventKind.SandboxStatus, SandboxEvents.EncodeStatus(answer)));
        GD.Print(answer.Granted ? $"server: {c.Name} #{c.Id} sandbox mode on" : $"server: {c.Name} #{c.Id} sandbox refused (--allow-sandbox is off)");
    }

    /// <summary>Scores (XP, stats, the score) count for every player except a sandbox one. Unknown ids count as normal.</summary>
    bool Scores(ushort id) => _match.Get(id) is not { Sandbox: true };

    /// <summary>
    /// A sandbox player picks a killstreak from the menu: it starts at once (no kill requirement, no cap). Dead players and
    /// players who were not granted sandbox mode get nothing.
    /// </summary>
    void OnSandboxGrant(Client c, byte[] payload)
    {
        if (!c.Sandbox || SandboxEvents.DecodeGrant(payload) is not { } streakId) return;
        if (_match.Get(c.Id) is not { Life: PlayerLife.Alive }) return;
        var p = c.Body.GlobalPosition;
        if (!_streaks.SandboxActivate(c.Id, streakId, _elapsed, new System.Numerics.Vector3(p.X, p.Y, p.Z)))
            GD.Print($"server: {c.Name} #{c.Id} sandbox grant of {streakId} refused");
    }
}
