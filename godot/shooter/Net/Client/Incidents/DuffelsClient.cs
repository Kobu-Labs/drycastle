using Godot;
using Drycastle.Shooter.Menu;
using Drycastle.Shooter.Net.Client.Gear;
using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Menu;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Net;

namespace Drycastle.Shooter.Net.Client.Incidents;

/// <summary>
/// The Duffels on the client (GLOSSARY "Duffel"; world entity <see cref="DuffelRules.TypeId"/>; duffels.json <c>view</c>). Everyone sees a carried Duffel on its carrier's back and a lying or flying one
/// where it is (interpolated); a loaded one is the cargo volume's to show and is hidden here. The own carried Duffel is not drawn (the eye is inside the body), but it burdens the predicted body
/// (<see cref="Characters.Player.Burden"/>, the server's numbers) from the very input that takes it or lets it go, as the server does: the client predicts its own pick-up (the interact key's press
/// on the Duffel in reach) and throw (the throw key's fresh press), and each predicted input keeps the burden it ran with for the replay (<see cref="OwnPlayer.OwnPrediction"/>), so carrying
/// corrects nothing. Only the server knows a Duffel's party, so the key's owner and the prompt follow its <see cref="EventKind.DuffelReach"/>. A refusal (<see cref="EventKind.DuffelNotice"/>) is a toast.
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
    /// <summary>
    /// The kind this player carries at the newest input, as predicted (null none): taken at the press that picks one up, gone at the throw key's press. The replica wins when it still
    /// disagrees after <see cref="ConfirmSeconds"/> (a pick-up the server refused, or a change only the server made: a Job's gift, a drop on death).
    /// </summary>
    DuffelKind? _carried;
    /// <summary>The kind the replica says this player carries (null none), as of the last frame.</summary>
    DuffelKind? _replicated;
    /// <summary>Runs while a prediction waits for the replica to agree.</summary>
    Countdown _unconfirmed;
    Edge _use, _throw;
    CachedLine _take, _carry, _noSeat;

    /// <summary>Longer than a round trip takes: a prediction the replica has not confirmed by then was wrong.</summary>
    const float ConfirmSeconds = 1.5f;

    internal DuffelsClient(ClientContext ctx, DuffelsFile data, InteractTargets targets)
    {
        _ctx = ctx; _data = data; _targets = targets;
        DuffelRules.Register(WorldEntityRegistry.Shared);
        ctx.WorldEntities.Spawned += OnSpawned;
        ctx.WorldEntities.Despawned += OnDespawned;
    }

    public void OnEvent(EventKind kind, byte[] payload)
    {
        if (kind == EventKind.DuffelReach && DuffelCodec.TryDecodeReach(payload, out var id)) _reach = id;
        else if (kind == EventKind.DuffelNotice && DuffelCodec.TryDecodeNotice(payload, out var notice))
            UiShell.Instance?.Toast(notice switch
            {
                DuffelNotice.NoSeat => Strings.F("toast.duffel.noseat", InputBindings.Name("duffel_throw")),
                DuffelNotice.VanReserved => Strings.T("toast.van.reserved"),
                _ => Strings.T("toast.duffel.carrying"),
            }, ToastKind.Warn);
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

    /// <summary>Every frame: places every Duffel, reads what the own body carries in the replica, and tells the interact key what is in reach.</summary>
    public void Render()
    {
        _replicated = null;
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
        _targets.Duffel = _reach != 0 && _carried == null && !_ctx.Dead && !_ctx.Driving && _views.ContainsKey(_reach) ? _reach : null;
    }

    /// <summary>On the carrier's back, turned with the figure; the own one is not drawn.</summary>
    void PlaceCarried(View v, ushort carrier)
    {
        if (carrier == _ctx.MyId) { _replicated = v.Kind; v.Node.Visible = false; return; }
        if (!_ctx.Remotes.TryGetValue(carrier, out var r)) { v.Node.Visible = false; return; }
        var back = new Vector3((float)_data.View.Back[0], (float)_data.View.Back[1], (float)_data.View.Back[2]);
        v.Node.GlobalPosition = r.Node.GlobalPosition + back.Rotated(Vector3.Up, r.ShownYaw);
        v.Node.Rotation = new Vector3(0, r.ShownYaw, 0);
        v.Node.Visible = r.Node.Visible;
    }

    /// <summary>
    /// Runs on the input tick with the input about to be predicted and sent: predicts the own pick-up (the interact key's press on the Duffel in reach) and throw (the throw key's fresh press,
    /// followed as the server follows it), and sets the body's burden for this input. The server takes the Duffel before it moves the body on that input, so this runs before the step.
    /// </summary>
    public void TickKey(in MoveInput mi, float dt)
    {
        _unconfirmed.Tick(dt);
        if (_carried == _replicated) _unconfirmed.Cancel();
        else if (_unconfirmed.Ready) _carried = _replicated;
        var onFoot = !_ctx.Dead && !_ctx.Driving;
        if (_use.Pressed(mi.Has(MoveButtons.Use)) && onFoot && _carried == null && _targets.Duffel is { } id && _views.TryGetValue(id, out var v)) Predict(v.Kind);
        if (_throw.Pressed(mi.Has(MoveButtons.DuffelThrow)) && _carried != null && onFoot) Predict(null);
        if (_ctx.Me != null) _ctx.Me.Burden = _carried is { } kind && !_ctx.Dead ? _data.BurdenOf(kind) : MoveBurden.None;
    }

    void Predict(DuffelKind? kind)
    {
        _carried = kind;
        _unconfirmed.Start(ConfirmSeconds);
    }

    /// <summary>The pick-up prompt when this player's key would take a Duffel, else null.</summary>
    public string? Prompt() =>
        _targets.Duffel is { } id && _views.TryGetValue(id, out var v) ? _take.Get(v.Kind, "interact", "hud.duffel.take") : null;

    /// <summary>While carrying: how to throw it; the last line of the prompt chain.</summary>
    public string? CarryHint() => _carried is { } kind ? _carry.Get(kind, "duffel_throw", "hud.duffel.carry") : null;

    /// <summary>The ride's prompt, or while carrying on foot (the server refuses a carrier every seat) why there is no seat and how to get rid of the Duffel, in place of its "get in".</summary>
    public string? InsteadOfRide(string? ride) =>
        ride != null && _carried is { } kind && !_ctx.Driving ? _noSeat.Get(kind, "duffel_throw", "hud.duffel.noseat") : ride;

    /// <summary>A prompt line rebuilt only when its kind or its key's binding changes: the HUD asks every frame, and nothing is built per frame.</summary>
    struct CachedLine
    {
        string? _text;
        DuffelKind _kind;
        Binding _key;

        /// <summary>The line of <paramref name="format"/> with the key bound to <paramref name="action"/> ({0}) and the kind's name ({1}).</summary>
        public string Get(DuffelKind kind, string action, string format)
        {
            var key = GameSettings.Current.Keys.Get(action);
            if (_text != null && _kind == kind && _key == key) return _text;
            _kind = kind; _key = key;
            return _text = Strings.F(format, InputBindings.Name(key), Strings.T("duffel." + DuffelRules.NameOf(kind)));
        }
    }
}
