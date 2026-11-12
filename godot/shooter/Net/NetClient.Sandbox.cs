using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// Sandbox mode on the client (#156): the request in the hello, the server's answer, the held killstreak, the panel's
/// actions and the F4 key that opens the panel. The server decides; the client asks, shows the answers and sends the panel's
/// requests.
/// </summary>
public partial class NetClient
{
    /// <summary>The client asks for sandbox mode in its hello. The server may refuse (see <see cref="SandboxAnswered"/>).</summary>
    public bool SandboxRequested;

    /// <summary>True once the server granted sandbox mode to this player.</summary>
    public bool SandboxGranted { get; private set; }

    /// <summary>The killstreak held in the sandbox slot ("" = none). Set by the server.</summary>
    public string SandboxHeldStreak { get; private set; } = "";

    /// <summary>Sandbox: this player takes damage (the panel's checkbox). Off is godmode. Kept here so it outlives the panel; a new connection starts off, as the server does.</summary>
    public bool SandboxTakesDamage { get; private set; }

    /// <summary>Sandbox only: switch taking damage on or off for this player.</summary>
    public void SetSandboxTakesDamage(bool on)
    {
        if (!SandboxGranted || SandboxTakesDamage == on) return;
        SandboxTakesDamage = on;
        SendSandboxAction(SandboxAction.TakeDamage, (byte)(on ? 1 : 0));
        _hud?.SetSandbox(SandboxGranted, SandboxHeldStreak, SandboxTakesDamage);
        SandboxChanged?.Invoke();
    }

    /// <summary>The server's answer to a sandbox request, granted or refused, with the reason to show.</summary>
    public event Action<SandboxAnswer>? SandboxAnswered;

    /// <summary>A sandbox action was refused or the server has something to say (a short line for a toast).</summary>
    public event Action<string>? SandboxNoticed;

    /// <summary>Granted, the held streak, or a player's sandbox tag changed: the panel and the HUD redraw from the fields above.</summary>
    public event Action? SandboxChanged;

    /// <summary>Opens the sandbox panel (set by the match owner; F4 calls it).</summary>
    public Action? OpenSandbox { get; set; }

    readonly HashSet<ushort> _sandboxPlayers = new();

    /// <summary>The name a player is drawn with: their name, with the sandbox tag when they are in sandbox mode.</summary>
    public string NameTag(ushort id, string name) => _sandboxPlayers.Contains(id) ? $"{name} {Strings.T("sandbox.tag")}" : name;

    void OnSandboxEvent(EventKind kind, byte[] payload)
    {
        switch (kind)
        {
            case EventKind.SandboxStatus when SandboxEvents.DecodeStatus(payload) is { } answer:
                SandboxGranted = answer.Granted;
                GD.Print($"client: sandbox {(answer.Granted ? "on" : "refused")}: {answer.Reason}");
                SandboxAnswered?.Invoke(answer);
                SandboxChanged?.Invoke();
                break;
            case EventKind.SandboxState when SandboxEvents.DecodeState(payload) is { } st:
                if (st.On) _sandboxPlayers.Add(st.Id); else _sandboxPlayers.Remove(st.Id);
                if (_remotes.TryGetValue(st.Id, out var r) && _names.TryGetValue(st.Id, out var n))
                    foreach (var label in r.Node.GetChildren().OfType<Label3D>()) label.Text = NameTag(st.Id, n);
                SandboxChanged?.Invoke();
                break;
            case EventKind.SandboxHeld when SandboxEvents.DecodeText(payload) is { } held:
                SandboxHeldStreak = held;
                SandboxChanged?.Invoke();
                break;
            case EventKind.SandboxNotice when SandboxEvents.DecodeText(payload) is { } text:
                SandboxNoticed?.Invoke(text);
                break;
        }
        _hud?.SetSandbox(SandboxGranted, SandboxHeldStreak, SandboxTakesDamage);
    }

    /// <summary>Sandbox only: put a killstreak into the held slot (the server ignores it for anyone else).</summary>
    public void SendSandboxGrant(string streakId)
    {
        if (SandboxGranted) SendSandboxEvent(EventKind.SandboxGrant, SandboxEvents.EncodeGrant(streakId));
    }

    /// <summary>Sandbox only: one of the panel's actions (heal, teleport, a vehicle, the target respawn switch, use the held streak).</summary>
    public void SendSandboxAction(SandboxAction action, byte arg = 0)
    {
        if (SandboxGranted) SendSandboxEvent(EventKind.SandboxAction, SandboxEvents.EncodeAction(action, arg));
    }

    void SendSandboxEvent(EventKind kind, byte[] payload)
    {
        if (_peer == null) return;
        Tx(_peer, Wire.ChanReliable, Wire.Encode(new EventMsg((byte)kind, payload)), (int)ENetPacketPeer.FlagReliable);
    }

    /// <summary>
    /// F4 (the sandbox_panel action, rebindable): opens the panel while no screen is open. Without sandbox mode it says so.
    /// True when the key was used.
    /// </summary>
    bool HandleSandboxInput(InputEvent e)
    {
        if (e is not InputEventKey { Echo: false } || !e.IsActionPressed("sandbox_panel")) return false;
        if (MenuOpen?.Invoke() == true) return false;
        if (!SandboxGranted)
        {
            if (SandboxRequested) SandboxNoticed?.Invoke(Strings.T("sandbox.notice.off"));
            return false;
        }
        OpenSandbox?.Invoke();
        return true;
    }
}
