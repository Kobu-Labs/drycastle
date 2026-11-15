using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

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

    /// <summary>
    /// An admin turns sandbox mode on or off for a joined player (#156). The player gets the status notice, everyone the name tag
    /// change; unlimited ammo is live at once, the whole hotbar from the next spawn (the loadout is applied at every spawn).
    /// </summary>
    void SetSandbox(Client c, bool on)
    {
        c.Sandbox = on;
        _match.SetSandbox(c.Id, on);
        if (c.W != null) c.W.Unlimited = on;
        if (!on) _streaks.SandboxRelease(c.Id);
        Send(c, new EventMsg((byte)EventKind.SandboxStatus, SandboxEvents.EncodeStatus(new SandboxAnswer(on, on ? SandboxRules.GrantedReason : SandboxRules.RevokedReason))));
        BroadcastEvent(EventKind.SandboxState, SandboxEvents.EncodeState(c.Id, on));
        SendSandboxHeld(c); // off: the held slot is empty, the client clears it
        if (c.W != null) SendAmmo(c);
        GD.Print($"server: {c.Name} #{c.Id} sandbox mode {(on ? "on" : "off")} by an admin");
    }

    /// <summary>Scores (XP, stats, the score) count for every player except a sandbox one. Unknown ids count as normal.</summary>
    bool Scores(ushort id) => _match.Get(id) is not { Sandbox: true } && !IsBot(id); // a bot earns no XP and has no stats (#214)

    /// <summary>Join: the sandbox name tags of everyone already in sandbox mode go to the joiner, and the joiner's to everyone.</summary>
    void SandboxAnnounce(Client c)
    {
        var joined = _byId.Values.ToList();
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
        if (a.Action == SandboxAction.TakeDamage)
        {
            // a switch, not an act on the body: it works while dead too, so a player who keeps dying can turn it off
            _match.SetVulnerable(c.Id, a.Arg != 0);
            GD.Print($"server: {c.Name} #{c.Id} sandbox damage {(a.Arg != 0 ? "on" : "off")}");
            return;
        }
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

    /// <summary>
    /// Teleport to a point picked on the tactical map (middle click): onto the highest surface there (a roof when the point is on a building),
    /// out of any vehicle. Same wire event as a spawn. Refused outside the map or where there is nothing to stand on.
    /// </summary>
    void OnSandboxTeleportTo(Client c, byte[] payload)
    {
        if (!c.Sandbox || SandboxEvents.DecodeTeleportTo(payload) is not { } at) return;
        if (_match.Get(c.Id) is not { Life: PlayerLife.Alive }) { SandboxNote(c, "Sandbox actions work while alive."); return; }
        var q = PhysicsRayQueryParameters3D.Create(new Vector3(at.X, 2000f, at.Z), new Vector3(at.X, -500f, at.Z), CollisionLayers.World);
        var hit = World.GetWorld3D().DirectSpaceState.PerfRay(q);
        if (hit.Count == 0) { SandboxNote(c, "Nothing to stand on there."); return; }
        var p = hit["position"].AsVector3();
        if (c.Car != null) LeaveSeat(c, false);
        c.Body.Teleport(new Vector3(p.X, p.Y + 0.1f, p.Z));
        c.Inputs.Clear();
        var s = new System.Numerics.Vector3(p.X, p.Y, p.Z);
        _match.SetPosition(c.Id, s);
        BroadcastEvent(EventKind.Spawn, MatchCodec.Encode(new SpawnMsg(c.Id, s, 0)));
    }

    /// <summary>Sandbox vehicles per server run. Spawned cars are never removed, so an uncapped count would eventually wrap the vehicle id (T3-016).</summary>
    const int SandboxVehicleCap = 8, AdminVehicleCap = 32;
    int _sandboxVehicles;

    /// <summary>
    /// Spawns a vehicle a few metres in front of the player, facing the way they look (the same construction as the
    /// map's cars). <paramref name="kind"/> indexes vehicles.json.
    /// </summary>
    void SandboxSpawnVehicle(Client c, byte kind)
    {
        if (TrySpawnVehicle(c, kind, SandboxVehicleCap) is { } why) SandboxNote(c, why);
    }

    /// <summary>
    /// Spawns a vehicle in front of the player (the sandbox panel and the admin's /spawn). Returns why it did not, or null.
    /// <paramref name="cap"/> bounds the vehicles spawned this run.
    /// </summary>
    string? TrySpawnVehicle(Client c, byte kind, int cap)
    {
        if (Vehicles == null || kind >= Vehicles.Vehicles.Count) return "Unknown vehicle.";
        if (c.Car != null) return "Leave the vehicle first.";
        if (_sandboxVehicles >= cap) return $"The spawned vehicle limit ({cap}) is reached until the server restarts.";
        _sandboxVehicles++;
        var def = Vehicles.Vehicles[kind];
        var yaw = c.Body.Yaw;
        var p = c.Body.GlobalPosition;
        var fwd = new Vector3(-Mathf.Sin(yaw), 0, -Mathf.Cos(yaw));
        var at = p + fwd * (def.HalfLength + 4f + (def.Flight?.RotorRadius ?? 0f)); // a helicopter stands further off: its rotor reaches past its nose
        var colour = VehicleColours.Pick(_rng, new List<byte>(), def.Variants);
        var car = new ServerCar
        {
            Name = $"{def.Id}{_cars.Count}", Id = (ushort)(VehicleRecord.FirstId + _cars.Count), Kind = kind, Def = def, Colour = VehicleRecord.Pack(kind, colour),
            HalfWidth = def.HalfWidth, HalfLength = def.HalfLength, OriginPos = new Vector3(at.X, at.Y + 0.02f, at.Z), OriginYaw = yaw,
            FlightWater = Water, FlightBounds = MapFlightBounds,
        };
        World.AddChild(car);
        foreach (var cl in _byId.Values) { car.AddCollisionExceptionWith(cl.Body); car.Probe?.Ignore(cl.Body); car.DriveWorld?.Ignore(cl.Body); }
        _cars.Add(car);
        GD.Print($"server: {c.Name} #{c.Id} spawned a {def.Id}");
        return null;
    }
}
