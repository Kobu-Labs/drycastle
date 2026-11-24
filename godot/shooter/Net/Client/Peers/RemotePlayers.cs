using System.Collections;
using Godot;
using Drycastle.Shooter.Characters;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Net;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Client.Peers;

/// <summary>
/// The other players and target dummies being drawn, by entity id: a figure is made on the player's first snapshot record, takes each record's state into its interpolation
/// buffer and goes when the server drops the entity or the player leaves. The features that know something about a player's look or hold subscribe to
/// <see cref="Dressing"/> (the look and what they wear) and <see cref="Appeared"/> (the figure exists) instead of reaching into the figure's making; drawing the figures is <see cref="RemotesClient"/>.
/// </summary>
public sealed class RemotePlayers : IEnumerable<KeyValuePair<ushort, RemotePlayer>>
{
    readonly ClientContext _ctx;
    readonly Dictionary<ushort, RemotePlayer> _byId = new();
    /// <summary>The last time each player was seen riding a vehicle: a rider's corpse stays seated or is thrown, it does not collapse.</summary>
    readonly Dictionary<ushort, double> _lastRode = new();

    public RemotePlayers(ClientContext ctx) { _ctx = ctx; }

    /// <summary>A new figure is made for the player: its look and what it wears are put on (the look may arrive before the first snapshot, #140, #266).</summary>
    public event Action<CharacterView, ushort>? Dressing;
    /// <summary>A figure was added to the registry, before its first state is taken.</summary>
    public event Action<ushort, RemotePlayer>? Appeared;

    /// <summary>The text of a name tag (the sandbox's players are tagged, #156); the name when none is set.</summary>
    public Func<ushort, string, string> NameTag = (_, name) => name;

    public int Count => _byId.Count;
    public Dictionary<ushort, RemotePlayer>.ValueCollection Values => _byId.Values;
    public bool TryGetValue(ushort id, out RemotePlayer player) => _byId.TryGetValue(id, out player!);
    public bool ContainsKey(ushort id) => _byId.ContainsKey(id);

    public Dictionary<ushort, RemotePlayer>.Enumerator GetEnumerator() => _byId.GetEnumerator();
    IEnumerator<KeyValuePair<ushort, RemotePlayer>> IEnumerable<KeyValuePair<ushort, RemotePlayer>>.GetEnumerator() => _byId.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => _byId.GetEnumerator();

    /// <summary>One player's record of the newest snapshot: the figure is made when it is the first, then its state and position are taken.</summary>
    public void OnRecord(ushort id, EntityRecord rec, uint tick)
    {
        if (!_byId.TryGetValue(id, out var r))
        {
            r = Make(id, rec.Type == EntityType.Target);
            _byId[id] = r;
            Appeared?.Invoke(id, r);
        }
        r.State = rec.State; r.Stance = rec.Stance; r.Slot = rec.Weapon;
        r.Buf.Push(tick, rec.Position, rec.YawRad, rec.PitchRad);
        r.Min = NVec3.Min(r.Min, rec.Position); r.Max = NVec3.Max(r.Max, rec.Position);
    }

    /// <summary>The figure of a player goes (the server dropped the entity, or the player left).</summary>
    public void Remove(ushort id)
    {
        if (_byId.Remove(id, out var gone)) gone.Node.QueueFree();
    }

    /// <summary>Every figure goes (the client leaves).</summary>
    public void Clear()
    {
        foreach (var r in _byId.Values) if (GodotObject.IsInstanceValid(r.Node)) r.Node.QueueFree();
        _byId.Clear();
    }

    /// <summary>The player is on a vehicle's seat now.</summary>
    public void NoteRode(ushort id) => _lastRode[id] = _ctx.Now;

    /// <summary>When the player was last seen riding, on the client clock; null when never.</summary>
    public double? LastRode(ushort id) => _lastRode.TryGetValue(id, out var when) ? when : null;

    RemotePlayer Make(ushort id, bool target)
    {
        var node = new Node3D { Name = $"Remote{id}" };
        var view = CharacterView.Make();
        Dressing?.Invoke(view, id);
        node.AddChild(view);
        node.AddChild(new Label3D
        {
            Text = _ctx.Names.TryGetValue(id, out var n) ? NameTag(id, n) : $"#{id}", // [SANDBOX] tag for sandbox players (#156)
            Modulate = target ? TargetsClient.DummyTint : Colors.White, // orange name tag for target dummies (#148)
            Position = new Vector3(0, MoveConsts.CapsuleHeight + 0.3f, 0),
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            PixelSize = 0.005f, NoDepthTest = true,
        });
        _ctx.World.AddChild(node);
        return new RemotePlayer { Node = node, View = view };
    }
}
