using Godot;
using Drycastle.Shooter.Assets;
using Drycastle.Shooter.Audio;
using Drycastle.Shooter.Buildings;
using Drycastle.Shooter.HeadsUp.Prompts;
using Drycastle.Shooter.Menu;
using Drycastle.Shooter.Net.Client.Gear;
using Drycastle.Shooter.Weapons;
using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Hud;
using Drycastle.ShooterCore.Menu;
using Drycastle.ShooterCore.Net;

namespace Drycastle.Shooter.Net.Client.Incidents;

/// <summary>
/// The Drills on the Trap houses' safes on the client (#496, GLOSSARY "Drill"; trap_house.json <c>drill</c>). The server tells a player near a safe its Drill
/// (<see cref="EventKind.DrillState"/>, <see cref="EventKind.DrillCleared"/>); this draws the drill on the safe prop (<see cref="TrapHousesNode.SafeOf"/>: the Synty model, or a box
/// without the pack) while it runs or is jammed, plays its running loop, sparks and clunks when it jams, and swings the safe's door open when it is through. A party player near it gets
/// the Drill panel on the HUD (the progress, extrapolated between the server's messages; the open safe's for <see cref="DonePanelSeconds"/>, then the cash is the line's). Only the server
/// knows the party and what else wins the key, so the prompt and the hold ring follow its <see cref="EventKind.DrillReach"/>; the hold is counted here as the server counts it, for the
/// ring only. While it says the drill is in reach (and until a completed hold's key is let go), a corpse and a ground item give up the key (<see cref="InteractTargets.DrillInReach"/>),
/// as they do on the server.
/// </summary>
public sealed class DrillsClient
{
    sealed class View
    {
        public DrillMsg Msg;
        public double At;
        public Node3D? Safe, Drill, Door;
        public float DoorClosedY, DoorTurn, DoorTarget;
        public LoopVoice? Voice;
        public double NextSpark;
    }

    /// <summary>How long the panel of an open safe stays up: it says the cash is out, then the tracker line and the Duffels' prompts carry on.</summary>
    const double DonePanelSeconds = 4;

    readonly ClientContext _ctx;
    readonly Dictionary<string, View> _views = new();
    (DrillHold Hold, string House) _reach = (DrillHold.None, "");
    InteractHold _hold;
    /// <summary>The interact key's name, worked out again only when its binding changes (not every frame).</summary>
    Binding? _keyBinding;
    string _keyName = "";

    internal DrillsClient(ClientContext ctx, InteractTargets targets)
    {
        _ctx = ctx;
        targets.DrillInReach = () => InReach || _hold.Sent; // a set drill keeps the key until it is let go: the body under the player does not take it mid-hold
    }

    string KeyName()
    {
        var b = GameSettings.Current.Keys.Get("interact");
        if (b != _keyBinding) { _keyBinding = b; _keyName = InputBindings.Name(b); }
        return _keyName;
    }

    static TrapDrillData? Data => TrapHousesNode.Current?.Data.Drill;

    static double Now => Time.GetTicksMsec() / 1000.0;

    public void OnEvent(EventKind kind, byte[] payload)
    {
        if (kind == EventKind.DrillState && DrillCodec.DecodeState(payload) is { } m) Apply(m);
        else if (kind == EventKind.DrillCleared && DrillCodec.DecodeCleared(payload) is { } house) Forget(house);
        else if (kind == EventKind.DrillReach && DrillCodec.DecodeReach(payload) is { } r) _reach = r;
    }

    void Apply(DrillMsg m)
    {
        var fresh = !_views.TryGetValue(m.House, out var v);
        if (fresh) _views[m.House] = v = new View();
        var was = fresh ? (DrillState?)null : v!.Msg.State;
        v!.Msg = m; v.At = Now;
        if (was == m.State || Data is not { } data) return;
        var at = v.Drill is { } d && GodotObject.IsInstanceValid(d) ? d.GlobalPosition : v.Safe is { } s && GodotObject.IsInstanceValid(s) ? s.GlobalPosition : (Vector3?)null;
        if (m.State == DrillState.Jammed && at is { } j) { Sfx.Play3D("drill.jam", j); v.NextSpark = 0; }
        if (m.State == DrillState.Done)
        {
            // learnt on coming near (or with reduce motion): open at once; seen happen: it swings
            v.DoorTarget = Mathf.DegToRad(data.View.DoorOpenDeg);
            if (was == null || GameSettings.Current.ReduceMotion || data.View.DoorOpenSeconds <= 0) v.DoorTurn = v.DoorTarget;
            else if (at is { } o) Sfx.Play3D("drill.open", o);
        }
    }

    void Forget(string house)
    {
        if (!_views.Remove(house, out var v)) return;
        v.Voice?.Release();
        if (v.Drill is { } d && GodotObject.IsInstanceValid(d)) d.QueueFree();
        if (v.Door is { } door && GodotObject.IsInstanceValid(door)) door.Rotation = new Vector3(door.Rotation.X, v.DoorClosedY, door.Rotation.Z);
    }

    /// <summary>A new connection (or a leave): nothing is known until the server says so.</summary>
    public void Reset()
    {
        foreach (var house in _views.Keys.ToList()) Forget(house);
        _reach = (DrillHold.None, "");
        _hold.Reset();
        _ctx.Hud?.SetDrill(null);
    }

    /// <summary>Progress now: the server's, run on while it runs (a jam or the end is the server's to say).</summary>
    static float Progress(View v) => v.Msg.State == DrillState.Running ? MathF.Min(v.Msg.RunSeconds, v.Msg.Progress + (float)(Now - v.At)) : v.Msg.Progress;

    /// <summary>Every frame: the drill on each safe, its sound, the sparks of a jam, the door, and the panel of the nearest Drill of the player's party.</summary>
    public void Render(double dt)
    {
        DrillHudInfo? panel = null;
        var best = float.MaxValue;
        if (Data is { } data)
            foreach (var (house, v) in _views)
            {
                Attach(house, v, data);
                Show(v, data, (float)dt);
                if (!v.Msg.Party || _ctx.Dead || _ctx.Me is not { } me || v.Safe is not { } safe || (v.Msg.State == DrillState.Done && Now - v.At > DonePanelSeconds)) continue;
                var dist = me.GlobalPosition.DistanceTo(safe.GlobalPosition);
                if (dist > data.NearM || dist >= best) continue;
                best = dist;
                var progress = Progress(v);
                panel = new DrillHudInfo(v.Msg.State, Math.Clamp(progress / v.Msg.RunSeconds, 0, 1), MathF.Max(0, v.Msg.RunSeconds - progress), KeyName());
            }
        _ctx.Hud?.SetDrill(panel);
    }

    /// <summary>The drill and the door hang on the house's safe prop; a new dressing makes a new prop, so they are found again.</summary>
    void Attach(string house, View v, TrapDrillData data)
    {
        var safe = TrapHousesNode.Current?.SafeOf(house);
        if (safe == v.Safe && (safe == null || (v.Drill != null && GodotObject.IsInstanceValid(v.Drill)))) return;
        v.Safe = safe; v.Drill = null; v.Door = null;
        if (safe == null) return;
        v.Drill = Model(data.View);
        v.Drill.Position = new Vector3(data.View.At[0], data.View.At[1], data.View.At[2]);
        v.Drill.Rotation = new Vector3(0, Mathf.DegToRad(data.View.YawDeg), 0);
        v.Drill.Visible = false;
        safe.AddChild(v.Drill);
        if (data.View.Door != "" && safe.FindChild(data.View.Door, true, false) is Node3D door) { v.Door = door; v.DoorClosedY = door.Rotation.Y; }
    }

    static Node3D Model(TrapDrillView view)
    {
        if (view.Model != "" && SyntyLib.Instance(view.Model, view.Atlas) is { } model) return model;
        var size = new Vector3(view.Box[0], view.Box[1], view.Box[2]);
        var root = new Node3D { Name = "Drill" };
        root.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = size }, MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.85f, 0.55f, 0.1f), Roughness = 0.6f }, Position = new Vector3(0, size.Y / 2, 0) });
        return root;
    }

    void Show(View v, TrapDrillData data, float dt)
    {
        var on = v.Msg.State is DrillState.Running or DrillState.Jammed;
        if (v.Drill is { } drill && GodotObject.IsInstanceValid(drill))
        {
            drill.Visible = on;
            var at = drill.GlobalPosition;
            if (v.Msg.State == DrillState.Running) { v.Voice ??= Sfx.Loop("drill.run", at); v.Voice?.Update(at); }
            else if (v.Voice != null) { v.Voice.Release(); v.Voice = null; }
            if (v.Msg.State == DrillState.Jammed && Now >= v.NextSpark)
            {
                v.NextSpark = Now + data.View.SparkEverySeconds;
                var front = v.Safe!.GlobalTransform.Basis.Z.Normalized();
                ShotFx.Spark(_ctx.Owned, at + front * 0.05f + Vector3.Up * 0.15f, front);
            }
        }
        if (v.Door is { } door && GodotObject.IsInstanceValid(door) && !Mathf.IsEqualApprox(v.DoorTurn, v.DoorTarget))
        {
            v.DoorTurn = Mathf.MoveToward(v.DoorTurn, v.DoorTarget, v.DoorTarget / data.View.DoorOpenSeconds * dt);
            door.Rotation = new Vector3(door.Rotation.X, v.DoorClosedY + v.DoorTurn, door.Rotation.Z);
        }
        else if (v.Door is { } d2 && GodotObject.IsInstanceValid(d2) && v.DoorTarget > 0) d2.Rotation = new Vector3(d2.Rotation.X, v.DoorClosedY + v.DoorTarget, d2.Rotation.Z);
    }

    bool InReach => _reach.Hold != DrillHold.None && !_ctx.Dead && !_ctx.Driving;

    /// <summary>The set or fix prompt while the key is not held; the ring carries the hold.</summary>
    public string? Prompt() => InReach && _hold.Held <= 0
        ? Strings.F(_reach.Hold == DrillHold.Fix ? "hud.drill.fix" : "hud.drill.set", KeyName()) : null;

    /// <summary>The input tick: follows the hold of the interact key at the safe. Nothing is sent: the server sees the same key in the inputs.</summary>
    public void TickHold(bool use, double dt)
    {
        if (Data is { } data) _hold.Tick(use, InReach, dt, DrillRules.HoldSeconds(data, _reach.Hold));
    }

    /// <summary>The string key of the hold ring's label: setting the drill, or fixing the jammed one.</summary>
    public string RingKey => _reach.Hold == DrillHold.Fix ? "hud.ring.drill.fix" : "hud.ring.drill.set";

    /// <summary>How full the hold ring is for the Drill (0 when not holding at a safe).</summary>
    public float HoldFraction => InReach && _hold.Held > 0 && Data is { } data ? (float)Math.Min(1.0, _hold.Held / DrillRules.HoldSeconds(data, _reach.Hold)) : 0f;
}
