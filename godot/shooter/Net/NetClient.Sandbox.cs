using Godot;
using Suchohrad.ShooterCore;

namespace Suchohrad.Shooter;

/// <summary>
/// Sandbox mode on the client (#156): the request in the hello (<c>--sandbox</c> or <c>SUCHOHRAD_SANDBOX=1</c>), the server's
/// answer, and the on-demand killstreak grant. The server decides; the client only asks and shows the answer.
/// </summary>
public partial class NetClient
{
    /// <summary>The client asks for sandbox mode in its hello. The server may refuse (see <see cref="SandboxAnswered"/>).</summary>
    public bool SandboxRequested;

    /// <summary>True once the server granted sandbox mode to this player.</summary>
    public bool SandboxGranted { get; private set; }

    /// <summary>The server's answer to a sandbox request, granted or refused, with the reason to show.</summary>
    public event Action<SandboxAnswer>? SandboxAnswered;

    void OnSandboxEvent(EventKind kind, byte[] payload)
    {
        if (kind != EventKind.SandboxStatus || SandboxEvents.DecodeStatus(payload) is not { } answer) return;
        SandboxGranted = answer.Granted;
        GD.Print($"client: sandbox {(answer.Granted ? "on" : "refused")}: {answer.Reason}");
        SandboxAnswered?.Invoke(answer);
    }

    /// <summary>Sandbox only: start a killstreak now (the server ignores it for anyone else, and while dead).</summary>
    public void SendSandboxGrant(string streakId)
    {
        if (!SandboxGranted || _peer == null) return;
        Tx(_peer, Wire.ChanReliable, Wire.Encode(new EventMsg((byte)EventKind.SandboxGrant, SandboxEvents.EncodeGrant(streakId))), (int)ENetPacketPeer.FlagReliable);
    }
}
