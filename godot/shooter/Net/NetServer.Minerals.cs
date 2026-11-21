using Godot;
using Drycastle.ShooterCore;
using Drycastle.ShooterServer.Persistence;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

/// <summary>
/// Finding minerals on the server (#401, GLOSSARY "Mineral node", "Find"). The world has mineral nodes (crystals); a player holds the interact key at one and the client sends
/// <see cref="EventKind.MineralPick"/>. The server checks reach, that the mineral is active now (<see cref="MineralActivity"/>: each mineral has its own criteria) and that the player
/// has not found it, then records the find in the <see cref="MineralBook"/> (so the place among the finders needs no database) and queues it to the store. The book is replayed from the
/// store at start; a pick before it is read is ignored. Only stored players find (like the Hunter's Log, guests and bots are not logged). Static nodes never go away; a dynamic one (a creature's drop, GLOSSARY "Mineral") lies for anybody until taken or the round ends. Nodes are the same for
/// everyone; the nodes and whether each is active go to everyone (changes only), the finds to their owner.
/// </summary>
public partial class NetServer
{
    MineralBook? _mineralBook;
    Task<IReadOnlyList<MineralFind>>? _mineralLoad;
    double _mineralRetryAt;
    MineralNodeSet? _mineralNodes;
    bool[] _mineralActive = Array.Empty<bool>();
    double _mineralNextCheck;

    const double MineralLoadRetrySeconds = 5;

    /// <summary>Once a tick: builds the nodes, loads the book, sends the finds to joiners and the nodes when an activity changed.</summary>
    void MineralTick()
    {
        if (_mineralNodes == null && _tick >= 4 && Spawns.Count > 0) BuildMineralNodes();
        if (_mineralBook == null) LoadMineralBook();
        else foreach (var c in _clients.Values)
            if (!c.FindsSent && c.Joined && c.Profile is { Persist: true } p) { c.FindsSent = true; SendMineralFinds(c, p.DbId, 255); }
        if (_mineralNodes != null && _elapsed >= _mineralNextCheck)
        {
            _mineralNextCheck = _elapsed + 1;
            var now = ActiveNow();
            if (!now.SequenceEqual(_mineralActive)) { _mineralActive = now; BroadcastEvent(EventKind.MineralNodes, MineralNodesPayload()); }
        }
    }

    /// <summary>No nodes yet: the places (#389 to #397) each bring theirs.</summary>
    void BuildMineralNodes()
    {
        _mineralNodes = new MineralNodeSet();
        _mineralActive = ActiveNow();
        GD.Print("server: 0 mineral node(s)");
        foreach (var d in _wildlife?.Drops ?? Array.Empty<MineralDrop>()) _mineralNodes.AddDrop(d); // (a drop cannot come before the nodes, but never lose one)
    }

    bool[] ActiveNow() => _mineralNodes!.Nodes.Select(n => MineralActivity.IsActive(n.MineralId, default)).ToArray();

    byte[] MineralNodesPayload() => MineralRules.EncodeNodes(_mineralNodes!.Nodes.Select((n, i) => (n, _mineralActive[i])).ToList());

    /// <summary>The nodes changed (a drop came or went): everyone gets the whole set again.</summary>
    void MineralNodesChanged()
    {
        _mineralActive = ActiveNow();
        BroadcastEvent(EventKind.MineralNodes, MineralNodesPayload());
    }

    /// <summary>A dead Phoenix's mineral (<see cref="WildlifeKeeper.MineralDropped"/>) lies where it burned as a node anybody can take, until taken or the round ends (GLOSSARY "Mineral").</summary>
    void OnMineralDropped(MineralDrop drop)
    {
        if (_mineralNodes == null) return;
        if (_mineralNodes.AddDrop(drop) is { } node) { MineralNodesChanged(); GD.Print($"server: {drop.MineralId} lies as mineral node {node.Id} at ({drop.At.X:F0}, {drop.At.Y:F0}, {drop.At.Z:F0})"); }
        else GD.PrintErr($"server: no free mineral node id for the dropped {drop.MineralId}");
    }

    /// <summary>A round starts: the drops of the last one are gone.</summary>
    void MineralRoundStart()
    {
        if (_mineralNodes?.ClearDrops() == true) MineralNodesChanged();
    }

    void LoadMineralBook()
    {
        if (_mineralLoad == null)
        {
            if (_elapsed < _mineralRetryAt) return;
            if (Progression == null) { _mineralBook = new MineralBook(); return; } // no store: every player is a guest, nothing is found
            _mineralLoad = Progression.Minerals.GetAllAsync();
            return;
        }
        if (!_mineralLoad.IsCompleted) return;
        if (!_mineralLoad.IsCompletedSuccessfully)
        {
            GD.PrintErr($"server: the mineral log could not be read, retrying: {_mineralLoad.Exception?.GetBaseException().Message}");
            _mineralLoad = null;
            _mineralRetryAt = _elapsed + MineralLoadRetrySeconds;
            return;
        }
        var rows = _mineralLoad.Result;
        _mineralBook = MineralBook.From(rows);
        _mineralLoad = null;
        GD.Print($"server: mineral log loaded, {rows.Count} finds replayed");
    }

    /// <summary>Join: the nodes (the finds follow from <see cref="MineralTick"/> once the book is there).</summary>
    void SendMineralsOnJoin(Client c)
    {
        c.FindsSent = false;
        if (_mineralNodes != null) Send(c, new EventMsg((byte)EventKind.MineralNodes, MineralNodesPayload()));
    }

    void SendMineralFinds(Client c, long playerId, byte justFound) =>
        Send(c, new EventMsg((byte)EventKind.MineralFinds, MineralRules.EncodeFinds(_mineralBook!.RanksOf(playerId), justFound)));

    /// <summary>A client held the key at a node: the server checks the player, the reach, the activity and the once-only rule.</summary>
    void OnMineralPick(Client c, byte[] payload)
    {
        if (!MineralRules.TryDecodePick(payload, out var id) || _mineralBook is not { } book || _mineralNodes == null) return;
        if (c.IsBot || c.W == null || c.Car != null || _match.Get(c.Id)?.Life != PlayerLife.Alive || c.Profile is not { Persist: true } p) return;
        var node = _mineralNodes.Find(id);
        var result = book.Check(p.DbId, node, node != null && MineralActivity.IsActive(node.MineralId, default), N(c.Body.GlobalPosition));
        if (result != MineralPickResult.Ok || node == null)
        {
            GD.Print($"server: {c.Name} mineral node {id} not taken: {result}");
            return;
        }
        var rank = book.Add(p.DbId, node.MineralId);
        _ = Progression!.Minerals.AppendAsync(new MineralFind(p.DbId, node.MineralId, DateTime.UtcNow));
        SendMineralFinds(c, p.DbId, (byte)MineralRules.IndexOf(node.MineralId));
        if (_mineralNodes.Remove(node.Id)) MineralNodesChanged(); // a dropped mineral lies until somebody takes it
        GD.Print($"server: {c.Name} found {node.MineralId}, {MineralFormat.Ordinal(rank)} finder");
    }
}
