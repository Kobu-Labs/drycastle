using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>
/// The mineral nodes of the world (GLOSSARY "Mineral node"): the static ones the places bring (ids 0 to <see cref="FirstDynamicId"/> - 1, never go away) and the dynamic ones a creature's drop makes
/// (a dead Phoenix's pheonixite, ids from <see cref="FirstDynamicId"/> up), which lie until somebody takes them or the round ends. A dynamic node is a pickup for anybody: never reserved for whoever
/// caused it. The server's tick owns it (not thread-safe); <see cref="Nodes"/> is what <see cref="MineralRules.EncodeNodes"/> carries, so a client that no longer sees a node drops it.
/// </summary>
public sealed class MineralNodeSet
{
    /// <summary>The first node id of a dynamic node; the ids below belong to static nodes.</summary>
    public const byte FirstDynamicId = 128;

    readonly List<MineralNode> _nodes = new();
    byte _nextDynamic = FirstDynamicId;

    /// <summary>All nodes, static first, then the dynamic ones in the order they were left.</summary>
    public IReadOnlyList<MineralNode> Nodes => _nodes;

    /// <summary>A static node (a place's crystal). Null when the static ids are used up.</summary>
    public MineralNode? AddStatic(string mineralId, Vector3 at)
    {
        var id = _nodes.Count(n => n.Id < FirstDynamicId);
        if (id >= FirstDynamicId) return null;
        var node = new MineralNode((byte)id, mineralId, at);
        _nodes.Insert(id, node);
        return node;
    }

    /// <summary>A creature's drop becomes a dynamic node at its place. Null when no id is free (128 lie at once).</summary>
    public MineralNode? AddDrop(MineralDrop drop)
    {
        for (var tries = 0; tries < 256 - FirstDynamicId; tries++)
        {
            var id = _nextDynamic;
            _nextDynamic = (byte)(id == 255 ? FirstDynamicId : id + 1);
            if (_nodes.Any(n => n.Id == id)) continue;
            var node = new MineralNode(id, drop.MineralId, drop.At);
            _nodes.Add(node);
            return node;
        }
        return null;
    }

    public MineralNode? Find(byte id) => _nodes.FirstOrDefault(n => n.Id == id);

    public static bool IsDynamic(MineralNode node) => node.Id >= FirstDynamicId;

    /// <summary>A dynamic node was taken (or its time is over): it is gone. False for a static node or one that is not there.</summary>
    public bool Remove(byte id)
    {
        var node = Find(id);
        return node != null && IsDynamic(node) && _nodes.Remove(node);
    }

    /// <summary>The round is over: every dynamic node goes. True when any did.</summary>
    public bool ClearDrops() => _nodes.RemoveAll(IsDynamic) > 0;
}
