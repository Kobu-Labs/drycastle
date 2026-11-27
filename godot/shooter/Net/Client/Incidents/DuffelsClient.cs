using Godot;
using Drycastle.Shooter.Menu;
using Drycastle.Shooter.Net.Client.Gear;
using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Net;

namespace Drycastle.Shooter.Net.Client.Incidents;

/// <summary>
/// The Duffels on the client (GLOSSARY "Duffel"; world entity <see cref="DuffelRules.TypeId"/>; duffels.json <c>view</c>). Everyone sees a carried Duffel on its carrier's back and a lying or flying one
/// where it is (interpolated); a loaded one is the cargo volume's to show and is hidden here. The own carried Duffel is not drawn (the eye is inside the body), but it burdens the predicted body
/// (<see cref="Characters.Player.Burden"/>, the server's numbers). Only the server knows a Duffel's party, so the key's owner and the prompt follow its <see cref="EventKind.DuffelReach"/>; the throw key's
/// charge fills the hold ring, counted as the server counts it (<see cref="DuffelThrowKey"/>). A refusal (<see cref="EventKind.DuffelNotice"/>) is a toast.
/// </summary>
public sealed class DuffelsClient
{
    sealed record View(Node3D Node, DuffelKind Kind);

    readonly ClientContext _ctx;
    readonly DuffelsFile _data;
    readonly InteractTargets _targets;
    readonly Dictionary<ushort, View> _views = new();
    /// <summary>The Duffel the server last said this player's key would pick up (0 none).</summary>
    ushort _reach;
    /// <summary>The kind this player carries now (null none), from the replica.</summary>
    DuffelKind? _carried;
    DuffelThrowKey _key;

    internal DuffelsClient(ClientContext ctx, DuffelsFile data, InteractTargets targets)
    {
        _ctx = ctx; _data = data; _targets = targets;
        DuffelRules.Register(WorldEntityRegistry.Shared);
        ctx.WorldEntities.Spawned += OnSpawned;
        ctx.WorldEntities.Despawned += OnDespawned;
    }

    /// <summary>How full the hold ring is for the throw key's charge (0 when not charging).</summary>
    public float ChargeFraction => _key.State == DuffelKeyState.Charging && _key.Held >= _data.TapSeconds ? MathF.Max(0.05f, _key.Charge(_data)) : 0f;

    public void OnEvent(EventKind kind, byte[] payload)
    {
        if (kind == EventKind.DuffelReach && DuffelCodec.TryDecodeReach(payload, out var id)) _reach = id;
        else if (kind == EventKind.DuffelNotice && DuffelCodec.TryDecodeNotice(payload, out var notice))
            UiShell.Instance?.Toast(notice == DuffelNotice.NoSeat ? Strings.F("toast.duffel.noseat", InputBindings.Name("duffel_throw")) : Strings.T("toast.duffel.carrying"), ToastKind.Warn);
    }

    void OnSpawned(WorldReplica.Entity e)
    {
        if (e.Type != DuffelRules.TypeId || DuffelRules.KindOf(e.State.State) is not { } kind) return;
        var node = new Node3D { Name = $"Duffel{e.Id}", Visible = false }; // shown at its first placed position, never at the world's origin
        node.AddChild(Prop(kind));
        _ctx.Owned.AddChild(node);
        _views[e.Id] = new View(node, kind);
    }

    void OnDespawned(ushort id)
    {
        if (_views.Remove(id, out var v)) v.Node.QueueFree();
        if (_reach == id) _reach = 0;
    }

    /// <summary>The prop: the kind's pack model with its base centred on the origin, or a box of the kind's colour.</summary>
    Node3D Prop(DuffelKind kind)
    {
        var k = _data.Of(kind);
        var root = new Node3D { Name = "Prop", Scale = Vector3.One * (float)_data.View.Scale };
        if (k.Model.Length > 0 && _ctx.Map?.InstantiateAsset(k.Model) is { } model)
        {
            foreach (var co in model.FindChildren("*", "CollisionObject3D", true, false).OfType<CollisionObject3D>()) { co.CollisionLayer = 0; co.CollisionMask = 0; }
            if (model.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>().FirstOrDefault() is { Mesh: { } mesh })
            {
                var box = mesh.GetAabb();
                model.Position = -new Vector3(box.Position.X + box.Size.X / 2, box.Position.Y, box.Position.Z + box.Size.Z / 2);
            }
            root.AddChild(model);
            return root;
        }
        var size = new Vector3((float)_data.View.Box[0], (float)_data.View.Box[1], (float)_data.View.Box[2]);
        root.AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = size },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = Color.FromHtml(k.Colour), Roughness = 0.95f },
            Position = new Vector3(0, size.Y / 2, 0),
        });
        return root;
    }

    /// <summary>Every frame: places every Duffel, burdens the own body by what it carries, and tells the interact key what is in reach.</summary>
    public void Render()
    {
        _carried = null;
        var rt = _ctx.RenderTick;
        foreach (var (id, v) in _views)
        {
            if (!_ctx.WorldEntities.Entities.TryGetValue(id, out var e)) { v.Node.Visible = false; continue; }
            var state = DuffelRules.StateOf(e.State.State);
            if (state == DuffelState.Carried) { PlaceCarried(v, e.State.Owner); continue; }
            if (state == DuffelState.Loaded || e.Buf.At(rt) is not { } s) { v.Node.Visible = false; continue; }
            v.Node.GlobalPosition = new Vector3(s.Pos.X, s.Pos.Y, s.Pos.Z);
            v.Node.Rotation = new Vector3(0, s.Yaw, 0);
            v.Node.Visible = true;
        }
        if (_ctx.Me != null) _ctx.Me.Burden = _carried is { } kind && !_ctx.Dead ? _data.BurdenOf(kind) : MoveBurden.None;
        _targets.Duffel = _reach != 0 && _carried == null && !_ctx.Dead && !_ctx.Driving && _views.ContainsKey(_reach) ? _reach : null;
    }

    /// <summary>On the carrier's back, turned with the figure; the own one is not drawn.</summary>
    void PlaceCarried(View v, ushort carrier)
    {
        if (carrier == _ctx.MyId) { _carried = v.Kind; v.Node.Visible = false; return; }
        if (!_ctx.Remotes.TryGetValue(carrier, out var r)) { v.Node.Visible = false; return; }
        var back = new Vector3((float)_data.View.Back[0], (float)_data.View.Back[1], (float)_data.View.Back[2]);
        v.Node.GlobalPosition = r.Node.GlobalPosition + back.Rotated(Vector3.Up, r.ShownYaw);
        v.Node.Rotation = new Vector3(0, r.ShownYaw, 0);
        v.Node.Visible = r.Node.Visible;
    }

    /// <summary>Runs on the input tick: follows the throw key as the server does, for the ring (nothing is sent but the key in the input).</summary>
    public void TickKey(float dt)
    {
        var down = Input.MouseMode == Input.MouseModeEnum.Captured && Input.IsActionPressed("duffel_throw");
        _key.Tick(down, _carried != null && !_ctx.Dead && !_ctx.Driving, dt, _data, out _);
    }

    /// <summary>The pick-up prompt when this player's key would take a Duffel, else null.</summary>
    public string? Prompt()
    {
        if (_targets.Duffel is not { } id || !_views.TryGetValue(id, out var v)) return null;
        return Strings.F("hud.duffel.take", InputBindings.Name("interact"), Strings.T("duffel." + DuffelRules.NameOf(v.Kind)));
    }

    /// <summary>While carrying (and not charging): how to throw or put it down; the last line of the prompt chain.</summary>
    public string? CarryHint() =>
        _carried is { } kind && _key.State == DuffelKeyState.Idle ? Strings.F("hud.duffel.carry", InputBindings.Name("duffel_throw"), Strings.T("duffel." + DuffelRules.NameOf(kind))) : null;
}
