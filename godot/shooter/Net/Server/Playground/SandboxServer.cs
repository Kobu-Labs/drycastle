using Godot;
using Drycastle.Shooter.Net.Server.Streaks;
using Drycastle.ShooterCore.Chat;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Sandbox;
using Drycastle.ShooterCore.Weapons;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Playground;

/// <summary>
/// Sandbox mode on the server (#156): the grant at hello, the status notice, the held killstreak (grant from the panel, used with the activation key or the panel's button), the panel's
/// one-shot actions, the name tags of other players, and the rule that a sandbox player's kills are not scored (<see cref="Careers.ProgressionServer.Scores"/>). The rules are in
/// <see cref="SandboxRules"/>; the damage immunity and the score rules are in <see cref="MatchSim"/>; unlimited ammo is <see cref="PlayerWeapons.Unlimited"/>.
/// </summary>
internal sealed class SandboxServer
{
    readonly ServerContext _ctx;
    readonly bool _allowed;
    readonly Func<StreakServer> _streaks;
    readonly AdminSpawner _spawner;
    readonly Func<List<NVec3>> _spawns;
    readonly Func<WorldQuery> _query;
    readonly Action<ServerClient> _sendAmmo;
    readonly Action<ServerClient, bool> _leaveSeat;
    readonly Random _rng;

    /// <param name="allowed">--allow-sandbox: grant sandbox mode to clients that ask for it. Dev servers only.</param>
    /// <param name="sendAmmo">Tells the player their ammunition.</param>
    /// <param name="leaveSeat">Takes the player out of their vehicle's seat; the flag puts the body on the ground beside it.</param>
    public SandboxServer(ServerContext ctx, bool allowed, Func<StreakServer> streaks, AdminSpawner spawner, Func<List<NVec3>> spawns, Func<WorldQuery> query,
        Action<ServerClient> sendAmmo, Action<ServerClient, bool> leaveSeat, Random rng)
    {
        _ctx = ctx; _allowed = allowed; _streaks = streaks; _spawner = spawner; _spawns = spawns; _query = query; _sendAmmo = sendAmmo; _leaveSeat = leaveSeat; _rng = rng;
    }

    /// <summary>The hello's flags: the sandbox request is kept on the client until the join sends the answer.</summary>
    public void OnHello(ServerClient c, byte flags)
    {
        c.SandboxRequested = (flags & HelloFlags.Sandbox) != 0;
        c.Sandbox = _allowed && c.SandboxRequested;
    }

    /// <summary>Sends the answer to a sandbox request (no message when the client did not ask).</summary>
    public void SendStatus(ServerClient c)
    {
        if (SandboxRules.Decide(c.SandboxRequested, _allowed) is not { } answer) return;
        _ctx.Send(c, new EventMsg((byte)EventKind.SandboxStatus, SandboxEvents.EncodeStatus(answer)));
        GD.Print(answer.Granted ? $"server: {c.Name} #{c.Id} sandbox mode on" : $"server: {c.Name} #{c.Id} sandbox refused (--allow-sandbox is off)");
    }

    /// <summary>Join: the sandbox name tags of everyone already in sandbox mode go to the joiner, and the joiner's to everyone.</summary>
    public void Announce(ServerClient c)
    {
        var joined = _ctx.Players.Values.ToList();
        if (c.Sandbox) foreach (var o in joined) _ctx.SendEvent(o, EventKind.SandboxState, SandboxEvents.EncodeState(c.Id, true));
        foreach (var o in joined.Where(o => o != c && o.Sandbox)) _ctx.SendEvent(c, EventKind.SandboxState, SandboxEvents.EncodeState(o.Id, true));
        if (c.Sandbox) SendHeld(c);
    }

    /// <summary>
    /// An admin turns sandbox mode on or off for a joined player (#156). The player gets the status notice, everyone the name tag
    /// change; unlimited ammo is live at once, the whole hotbar from the next spawn (the loadout is applied at every spawn).
    /// </summary>
    public void SetSandbox(ServerClient c, bool on)
    {
        c.Sandbox = on;
        _ctx.Match.SetSandbox(c.Id, on);
        if (c.W != null) c.W.Unlimited = on;
        if (!on) _streaks().Rules.SandboxRelease(c.Id);
        _ctx.SendEvent(c, EventKind.SandboxStatus, SandboxEvents.EncodeStatus(new SandboxAnswer(on, on ? SandboxRules.GrantedReason : SandboxRules.RevokedReason)));
        _ctx.Broadcast(EventKind.SandboxState, SandboxEvents.EncodeState(c.Id, on));
        SendHeld(c); // off: the held slot is empty, the client clears it
        if (c.W != null) _sendAmmo(c);
        GD.Print($"server: {c.Name} #{c.Id} sandbox mode {(on ? "on" : "off")} by an admin");
    }

    /// <summary>"/sandbox player [on|off]": a human by name (spaces ignored) or "#id". Without a state it flips.</summary>
    public void ChatCommand(ServerClient c, IReadOnlyList<string> args)
    {
        if (!_allowed) { _ctx.ChatTo(c, ChatLineKind.Error, "Sandbox mode is not allowed on this server (--allow-sandbox is off)."); return; }
        var who = args[0];
        var target = _ctx.FindHuman(who);
        if (target == null) { _ctx.ChatTo(c, ChatLineKind.Error, $"No player '{who}'. Use the name without spaces, or #id."); return; }
        var on = args.Count > 1 ? args[1] == "on" : !target.Sandbox;
        if (target.Sandbox == on) { _ctx.ChatTo(c, ChatLineKind.Info, $"{target.Name} is already {(on ? "in" : "out of")} sandbox mode."); return; }
        SetSandbox(target, on);
        _ctx.ChatToAll(ChatLineKind.System, "", $"{c.Name} turned sandbox mode {(on ? "on" : "off")} for {target.Name}.");
    }

    void SendHeld(ServerClient c) => _ctx.SendEvent(c, EventKind.SandboxHeld, SandboxEvents.EncodeText(_streaks().Rules.SandboxHeldOf(c.Id) ?? ""));

    /// <summary>A short line for the owner's toast (a refused action, or the reason the panel did nothing).</summary>
    void Note(ServerClient c, string text) => _ctx.SendEvent(c, EventKind.SandboxNotice, SandboxEvents.EncodeText(text));

    /// <summary>
    /// The panel picks a killstreak: it goes into the held slot (replacing the held one). It is not used yet: the activation
    /// key or the panel's "Use now" button uses it, and it stays held, so repeat uses are unlimited.
    /// </summary>
    public void OnGrant(ServerClient c, byte[] payload)
    {
        if (!c.Sandbox || SandboxEvents.DecodeGrant(payload) is not { } streakId) return;
        if (!_streaks().Rules.SandboxHold(c.Id, streakId)) { Note(c, "Unknown killstreak."); return; }
        SendHeld(c);
    }

    /// <summary>The panel's one-shot actions. Each one checks the player is alive (a dead player gets a notice).</summary>
    public void OnAction(ServerClient c, byte[] payload)
    {
        if (!c.Sandbox || SandboxEvents.DecodeAction(payload) is not { } a) return;
        var match = _ctx.Match;
        if (a.Action == SandboxAction.TakeDamage)
        {
            // a switch, not an act on the body: it works while dead too, so a player who keeps dying can turn it off
            match.SetVulnerable(c.Id, a.Arg != 0);
            GD.Print($"server: {c.Name} #{c.Id} sandbox damage {(a.Arg != 0 ? "on" : "off")}");
            return;
        }
        if (match.Get(c.Id) is not { Life: PlayerLife.Alive })
        {
            Note(c, "Sandbox actions work while alive.");
            return;
        }
        switch (a.Action)
        {
            case SandboxAction.Heal:
                match.Heal(c.Id);
                c.W.Refill();
                _sendAmmo(c);
                break;
            case SandboxAction.Teleport:
                TeleportToSpawn(c);
                break;
            case SandboxAction.SpawnVehicle:
                if (_spawner.TrySpawn(c, a.Arg, null) is { } why) Note(c, why);
                break;
            case SandboxAction.TargetRespawn:
                match.Targets.Respawns = a.Arg != 0;
                GD.Print($"server: {c.Name} set target dummy respawn {(a.Arg != 0 ? "on" : "off")}");
                break;
            case SandboxAction.Activate:
                if (!UseHeld(c)) Note(c, "Nothing held. Pick a killstreak first.");
                break;
        }
    }

    /// <summary>The activation key for a sandbox player with a held streak: use the held one (returns true when it did).</summary>
    public bool StreakKey(ServerClient c)
    {
        if (!c.Sandbox || _streaks().Rules.SandboxHeldOf(c.Id) is null) return false;
        UseHeld(c);
        return true;
    }

    /// <summary>Uses the held streak from the player's position. False when nothing is held or the player is dead.</summary>
    bool UseHeld(ServerClient c)
    {
        if (!c.Sandbox || _ctx.Match.Get(c.Id) is not { Life: PlayerLife.Alive }) return false;
        var p = c.Body.GlobalPosition;
        return _streaks().Rules.SandboxActivateHeld(c.Id, _ctx.Now, new NVec3(p.X, p.Y, p.Z));
    }

    /// <summary>Teleport to a random player spawn point, out of any vehicle. Same wire event as a spawn (no protection).</summary>
    void TeleportToSpawn(ServerClient c)
    {
        var spawns = _spawns();
        if (spawns.Count == 0) { Note(c, "No spawn points on this map."); return; }
        if (c.Car != null) _leaveSeat(c, false);
        var s = spawns[_rng.Next(spawns.Count)];
        c.Body.Teleport(new Vector3(s.X, s.Y + 0.1f, s.Z));
        c.Inputs.Clear();
        _ctx.Match.SetPosition(c.Id, s);
        _ctx.Broadcast(EventKind.Spawn, MatchCodec.Encode(new SpawnMsg(c.Id, s, 0)));
    }

    /// <summary>
    /// Teleport to a point picked on the tactical map (middle click): onto the highest surface there (a roof when the point is on a building),
    /// out of any vehicle. Same wire event as a spawn. Refused outside the map or where there is nothing to stand on.
    /// </summary>
    public void OnTeleportTo(ServerClient c, byte[] payload)
    {
        if (!c.Sandbox || SandboxEvents.DecodeTeleportTo(payload) is not { } at) return;
        if (_ctx.Match.Get(c.Id) is not { Life: PlayerLife.Alive }) { Note(c, "Sandbox actions work while alive."); return; }
        if (_query().Ground(MoverKind.Player, new Vector3(at.X, 0f, at.Z), 2000f, 500f) is not { } hit) { Note(c, "Nothing to stand on there."); return; }
        var p = hit.Position;
        if (c.Car != null) _leaveSeat(c, false);
        c.Body.Teleport(new Vector3(p.X, p.Y + 0.1f, p.Z));
        c.Inputs.Clear();
        var s = new NVec3(p.X, p.Y, p.Z);
        _ctx.Match.SetPosition(c.Id, s);
        _ctx.Broadcast(EventKind.Spawn, MatchCodec.Encode(new SpawnMsg(c.Id, s, 0)));
    }
}
