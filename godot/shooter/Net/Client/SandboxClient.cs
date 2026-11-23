using Godot;
using Drycastle.Shooter.Menu;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Sandbox;

namespace Drycastle.Shooter.Net.Client;

/// <summary>
/// Sandbox mode on the client (#156): the request in the hello, the server's answer, the held killstreak, the panel's
/// actions and the F4 key that opens the panel. The server decides; the client asks, shows the answers and sends the panel's
/// requests.
/// </summary>
public sealed class SandboxClient
{
    readonly ClientContext _ctx;
    readonly HashSet<ushort> _players = new();

    public SandboxClient(ClientContext ctx) { _ctx = ctx; }

    /// <summary>The client asks for sandbox mode in its hello. The server may refuse (see <see cref="Answered"/>).</summary>
    public bool Requested;

    /// <summary>True once the server granted sandbox mode to this player.</summary>
    public bool Granted { get; private set; }

    /// <summary>The killstreak held in the sandbox slot ("" = none). Set by the server.</summary>
    public string HeldStreak { get; private set; } = "";

    /// <summary>Sandbox: this player takes damage (the panel's checkbox). Off is godmode. Kept here so it outlives the panel; a new connection starts off, as the server does.</summary>
    public bool TakesDamage { get; private set; }

    /// <summary>The server's answer to a sandbox request, granted or refused, with the reason to show.</summary>
    public event Action<SandboxAnswer>? Answered;

    /// <summary>A sandbox action was refused or the server has something to say (a short line for a toast).</summary>
    public event Action<string>? Noticed;

    /// <summary>Granted, the held streak, or a player's sandbox tag changed: the panel and the HUD redraw from the fields above.</summary>
    public event Action? Changed;

    /// <summary>Opens the sandbox panel (set by the match owner; F4 calls it).</summary>
    public Action? OpenPanel { get; set; }

    /// <summary>The name a player is drawn with: their name, with the sandbox tag when they are in sandbox mode.</summary>
    public string NameTag(ushort id, string name) => _players.Contains(id) ? $"{name} {Strings.T("sandbox.tag")}" : name;

    /// <summary>Tells a new HUD (and after every change) the badge, the held streak and the damage switch.</summary>
    public void ShowOnHud() => _ctx.Hud?.SetSandbox(Granted, HeldStreak, TakesDamage);

    /// <summary>Sandbox only: switch taking damage on or off for this player.</summary>
    public void SetTakesDamage(bool on)
    {
        if (!Granted || TakesDamage == on) return;
        TakesDamage = on;
        SendAction(SandboxAction.TakeDamage, (byte)(on ? 1 : 0));
        ShowOnHud();
        Changed?.Invoke();
    }

    public void OnEvent(EventKind kind, byte[] payload)
    {
        switch (kind)
        {
            case EventKind.SandboxStatus when SandboxEvents.DecodeStatus(payload) is { } answer:
                Granted = answer.Granted;
                GD.Print($"client: sandbox {(answer.Granted ? "on" : "refused")}: {answer.Reason}");
                Answered?.Invoke(answer);
                Changed?.Invoke();
                break;
            case EventKind.SandboxState when SandboxEvents.DecodeState(payload) is { } st:
                if (st.On) _players.Add(st.Id); else _players.Remove(st.Id);
                if (_ctx.Remotes.TryGetValue(st.Id, out var r) && _ctx.Names.TryGetValue(st.Id, out var n))
                {
                    r.TagSquad = 0; // the squad tag, if any, is added again by the next frame
                    foreach (var label in r.Node.GetChildren().OfType<Label3D>()) label.Text = NameTag(st.Id, n);
                }
                Changed?.Invoke();
                break;
            case EventKind.SandboxHeld when SandboxEvents.DecodeText(payload) is { } held:
                HeldStreak = held;
                Changed?.Invoke();
                break;
            case EventKind.SandboxNotice when SandboxEvents.DecodeText(payload) is { } text:
                Noticed?.Invoke(text);
                break;
        }
        ShowOnHud();
    }

    /// <summary>Sandbox only: put a killstreak into the held slot (the server ignores it for anyone else).</summary>
    public void SendGrant(string streakId)
    {
        if (Granted) _ctx.Send(EventKind.SandboxGrant, SandboxEvents.EncodeGrant(streakId));
    }

    /// <summary>Sandbox only: one of the panel's actions (heal, teleport, a vehicle, the target respawn switch, use the held streak).</summary>
    public void SendAction(SandboxAction action, byte arg = 0)
    {
        if (Granted) _ctx.Send(EventKind.SandboxAction, SandboxEvents.EncodeAction(action, arg));
    }

    /// <summary>
    /// F4 (the sandbox_panel action, rebindable): opens the panel while no screen is open. Without sandbox mode it says so.
    /// True when the key was used.
    /// </summary>
    public bool HandleInput(InputEvent e)
    {
        if (e is not InputEventKey { Echo: false } || !e.IsActionPressed("sandbox_panel")) return false;
        if (_ctx.MenuIsOpen) return false;
        if (!Granted)
        {
            if (Requested) Noticed?.Invoke(Strings.T("sandbox.notice.off"));
            return false;
        }
        OpenPanel?.Invoke();
        return true;
    }
}
