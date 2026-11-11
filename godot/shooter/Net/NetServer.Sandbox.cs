using Godot;
using Suchohrad.ShooterCore;

namespace Suchohrad.Shooter;

/// <summary>
/// Sandbox mode on the server (#156): the grant at hello, the status notice, the held killstreak (grant from the panel,
/// used with the activation key or the panel's button), the panel's one-shot actions, the name tags of other players, and
/// the rule that a sandbox player's kills are not scored. The rules are in <see cref="SandboxRules"/>; the damage immunity
/// and the score rules are in <see cref="ShooterCore.MatchSim"/>; unlimited ammo is <see cref="PlayerWeapons.Unlimited"/>.
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

    /// <summary>Join: the sandbox name tags of everyone already in sandbox mode go to the joiner, and the joiner's to everyone.</summary>
    void SandboxAnnounce(Client c)
    {
        var joined = _clients.Values.Where(o => o.Joined).ToList();
        if (c.Sandbox) foreach (var o in joined) Send(o, new EventMsg((byte)EventKind.SandboxState, SandboxEvents.EncodeState(c.Id, true)));
        foreach (var o in joined.Where(o => o != c && o.Sandbox)) Send(c, new EventMsg((byte)EventKind.SandboxState, SandboxEvents.EncodeState(o.Id, true)));
        if (c.Sandbox) SendSandboxHeld(c);
    }

    void SendSandboxHeld(Client c) =>
        Send(c, new EventMsg((byte)EventKind.SandboxHeld, SandboxEvents.EncodeText(_streaks.SandboxHeldOf(c.Id) ?? "")));

    /// <summary>A short line for the owner's toast (a refused action, or the reason the panel did nothing).</summary>
    void SandboxNote(Client c, string text) => Send(c, new EventMsg((byte)EventKind.SandboxNotice, SandboxEvents.EncodeText(text)));

    /// <summary>
    /// The panel picks a killstreak: it goes into the held slot (replacing the held one). It is not used yet: the activation
    /// key or the panel's "Use now" button uses it, and it stays held, so repeat uses are unlimited.
    /// </summary>
    void OnSandboxGrant(Client c, byte[] payload)
    {
        if (!c.Sandbox || SandboxEvents.DecodeGrant(payload) is not { } streakId) return;
        if (!_streaks.SandboxHold(c.Id, streakId)) { SandboxNote(c, "Unknown killstreak."); return; }
        SendSandboxHeld(c);
    }

    /// <summary>The panel's one-shot actions. Each one checks the player is alive (a dead player gets a notice).</summary>
    void OnSandboxAction(Client c, byte[] payload)
    {
        if (!c.Sandbox || SandboxEvents.DecodeAction(payload) is not { } a) return;
        if (_match.Get(c.Id) is not { Life: PlayerLife.Alive })
        {
            SandboxNote(c, "Sandbox actions work while alive.");
            return;
        }
        switch (a.Action)
        {
            case SandboxAction.Heal:
                _match.Heal(c.Id);
                c.W.Refill();
                SendAmmo(c);
                break;
            case SandboxAction.Teleport:
                SandboxTeleport(c);
                break;
            case SandboxAction.SpawnVehicle:
                SandboxSpawnVehicle(c, a.Arg);
                break;
            case SandboxAction.TargetRespawn:
                _match.Targets.Respawns = a.Arg != 0;
                GD.Print($"server: {c.Name} set target dummy respawn {(a.Arg != 0 ? "on" : "off")}");
                break;
            case SandboxAction.Activate:
                if (!SandboxUseHeld(c)) SandboxNote(c, "Nothing held. Pick a killstreak first.");
                break;
        }
    }

    /// <summary>The activation key for a sandbox player with a held streak: use the held one (returns true when it did).</summary>
    bool SandboxStreakKey(Client c)
    {
        if (!c.Sandbox || _streaks.SandboxHeldOf(c.Id) is null) return false;
        SandboxUseHeld(c);
        return true;
    }

    /// <summary>Uses the held streak from the player's position. False when nothing is held or the player is dead.</summary>
    bool SandboxUseHeld(Client c)
    {
        if (!c.Sandbox || _match.Get(c.Id) is not { Life: PlayerLife.Alive }) return false;
        var p = c.Body.GlobalPosition;
        return _streaks.SandboxActivateHeld(c.Id, _elapsed, new System.Numerics.Vector3(p.X, p.Y, p.Z));
    }

    /// <summary>Teleport to a random player spawn point, out of any vehicle. Same wire event as a spawn (no protection).</summary>
    void SandboxTeleport(Client c)
    {
        if (Spawns.Count == 0) { SandboxNote(c, "No spawn points on this map."); return; }
        if (c.Car != null) LeaveSeat(c, false);
        var s = Spawns[_rng.Next(Spawns.Count)];
        c.Body.Teleport(new Vector3(s.X, s.Y + 0.1f, s.Z));
        c.Inputs.Clear();
        _match.SetPosition(c.Id, s);
        BroadcastEvent(EventKind.Spawn, MatchCodec.Encode(new SpawnMsg(c.Id, s, 0)));
    }

    /// <summary>Sandbox vehicles per server run. Spawned cars are never removed, so an uncapped count would eventually wrap the vehicle id (T3-016).</summary>
    const int SandboxVehicleCap = 8;
    int _sandboxVehicles;

    /// <summary>
    /// Spawns a vehicle a few metres in front of the player, facing the way they look (the same construction as the
    /// map's cars). <paramref name="kind"/> indexes vehicles.json.
    /// </summary>
    void SandboxSpawnVehicle(Client c, byte kind)
    {
        if (Vehicles == null || kind >= Vehicles.Vehicles.Count) { SandboxNote(c, "Unknown vehicle."); return; }
        if (c.Car != null) { SandboxNote(c, "Leave the vehicle first."); return; }
        if (_sandboxVehicles >= SandboxVehicleCap) { SandboxNote(c, $"The sandbox vehicle limit ({SandboxVehicleCap}) is reached until the server restarts."); return; }
        _sandboxVehicles++;
        var def = Vehicles.Vehicles[kind];
        var yaw = c.Body.Yaw;
        var p = c.Body.GlobalPosition;
        var fwd = new Vector3(-Mathf.Sin(yaw), 0, -Mathf.Cos(yaw));
        var at = p + fwd * (def.HalfLength + 4f);
        var colour = VehicleColours.Pick(_rng, new List<byte>(), def.Variants);
        var car = new ServerCar
        {
            Name = $"{def.Id}{_cars.Count}", Id = (ushort)(VehicleRecord.FirstId + _cars.Count), Kind = kind, Def = def, Colour = VehicleRecord.Pack(kind, colour),
            HalfWidth = def.HalfWidth, HalfLength = def.HalfLength, OriginPos = new Vector3(at.X, at.Y + 0.02f, at.Z), OriginYaw = yaw,
        };
        World.AddChild(car);
        foreach (var cl in _clients.Values.Where(x => x.Joined)) car.AddCollisionExceptionWith(cl.Body);
        _cars.Add(car);
        GD.Print($"server: {c.Name} #{c.Id} spawned a {def.Id}");
    }
}
