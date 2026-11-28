using Godot;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Citizens;
using Drycastle.ShooterCore.Mining;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Wildlife;
using Drycastle.ShooterServer.Persistence;


namespace Drycastle.Shooter.Net.Server.MineralLog;

/// <summary>
/// Finding minerals on the server (#401, GLOSSARY "Mineral node", "Find"). The world has mineral nodes (crystals); a player holds the interact key at one and the client sends
/// <see cref="EventKind.MineralPick"/>. The server checks reach, that the mineral is active now (<see cref="MineralActivity"/>: each mineral has its own criteria) and that the player
/// has not found it, then records the find in the <see cref="MineralBook"/> (so the place among the finders needs no database) and queues it to the store. The book is replayed from the
/// store at start; a pick before it is read is ignored. Only stored players find (like the Hunter's Log, guests and bots are not logged). Static nodes never go away; a dynamic one (a creature's drop, GLOSSARY "Mineral") lies for anybody until taken or the round ends. Nodes are the same for
/// everyone; the nodes and whether each is active go to everyone (changes only), the finds to their owner.
/// </summary>
internal sealed class MineralsServer
{
    const double LoadRetrySeconds = 5;

    readonly ServerContext _ctx;
    readonly IProgressionStore? _store;
    readonly Func<IReadOnlyList<MineralDrop>> _drops;
    readonly Func<IReadOnlyList<MineralNodeSpec>> _statics;
    readonly Func<bool> _holyMassPeaceful;
    readonly Func<bool> _storm;

    MineralBook? _book;
    Task<IReadOnlyList<MineralFind>>? _load;
    double _retryAt;
    MineralNodeSet? _nodes;
    bool[] _active = Array.Empty<bool>();
    double _nextCheck;

    /// <param name="drops">The drops that already lie in the world (a drop cannot come before the nodes, but never lose one).</param>
    /// <param name="statics">The static nodes of the map (mineral_nodes.json, set in the editor's edits).</param>
    /// <param name="storm">A thunderstorm is on (<see cref="Drycastle.Shooter.Net.Server.Weather.WeatherServer.Storm"/>): Thundernite's rule.</param>
    /// <param name="holyMassPeaceful">A Holy Mass was completed with no pastor killed, within holystone_seconds (60 min), and this round has not ended nor a new Mass begun (Holystone's rule, <see cref="HolyMassEvent.Peaceful"/>).</param>
    public MineralsServer(ServerContext ctx, IProgressionStore? store, Func<IReadOnlyList<MineralDrop>> drops, Func<IReadOnlyList<MineralNodeSpec>> statics, Func<bool> holyMassPeaceful, Func<bool> storm)
    {
        _ctx = ctx; _store = store; _drops = drops; _statics = statics; _holyMassPeaceful = holyMassPeaceful; _storm = storm;
    }

    /// <summary>Once a tick: builds the nodes (once the spawns are known), loads the book, sends the finds to joiners and the nodes when an activity changed.</summary>
    public void Tick(bool spawnsKnown)
    {
        if (_nodes == null && _ctx.Tick >= 4 && spawnsKnown) BuildNodes();
        if (_book == null) LoadBook();
        else foreach (var c in _ctx.Connections)
            if (!c.FindsSent && c.Joined && c.Profile is { Persist: true } p) { c.FindsSent = true; SendFinds(c, p.DbId, 255); }
        if (_nodes != null && _ctx.Now >= _nextCheck)
        {
            _nextCheck = _ctx.Now + 1;
            if (_nodes.Expire(_ctx.Now).Count > 0) NodesChanged(); // a drop's minute is over: it burns away
            var now = ActiveNow();
            if (!now.SequenceEqual(_active)) { _active = now; _ctx.Broadcast(EventKind.MineralNodes, NodesPayload()); }
        }
    }

    /// <summary>The static nodes the map names (mineral_nodes.json: the places #389 to #397 each bring theirs), then the drops that already lie in the world.</summary>
    void BuildNodes()
    {
        _nodes = new MineralNodeSet();
        var added = MineralNodeFile.AddTo(_nodes, _statics(), m => GD.PrintErr($"server: mineral node skipped, {m}"));
        _active = ActiveNow();
        GD.Print($"server: {added} mineral node(s)");
        foreach (var d in _drops()) _nodes.AddDrop(d);
    }

    /// <summary>What the activity rules look at, read from the server's other parts.</summary>
    MineralWorld World() => new(HolyMassPeaceful: _holyMassPeaceful(), Storm: _storm());

    bool[] ActiveNow() { var world = World(); return _nodes!.Nodes.Select(n => MineralActivity.IsActive(n.MineralId, world)).ToArray(); }

    byte[] NodesPayload() => MineralRules.EncodeNodes(_nodes!.Nodes.Select((n, i) => (n, _active[i])).ToList());

    /// <summary>The nodes changed (a drop came or went): everyone gets the whole set again.</summary>
    void NodesChanged()
    {
        _active = ActiveNow();
        _ctx.Broadcast(EventKind.MineralNodes, NodesPayload());
    }

    /// <summary>A dropped mineral (a dead Phoenix's, <see cref="WildlifeKeeper.MineralDropped"/>; senin from a burnt harvester bale) lies where it was left as a node anybody can take, for a minute (GLOSSARY "Mineral").</summary>
    public void OnDropped(MineralDrop drop)
    {
        if (_nodes == null) return;
        if (_nodes.AddDrop(drop, _ctx.Now) is { } node) { NodesChanged(); GD.Print($"server: {drop.MineralId} lies as mineral node {node.Id} at ({drop.At.X:F0}, {drop.At.Y:F0}, {drop.At.Z:F0})"); }
        else GD.PrintErr($"server: no free mineral node id for the dropped {drop.MineralId}");
    }

    /// <summary>A round starts: the drops of the last one are gone.</summary>
    public void RoundStart()
    {
        if (_nodes?.ClearDrops() == true) NodesChanged();
    }

    void LoadBook()
    {
        if (_load == null)
        {
            if (_ctx.Now < _retryAt) return;
            if (_store == null) { _book = new MineralBook(); return; } // no store: every player is a guest, nothing is found
            _load = _store.Minerals.GetAllAsync();
            return;
        }
        if (!_load.IsCompleted) return;
        if (!_load.IsCompletedSuccessfully)
        {
            GD.PrintErr($"server: the mineral log could not be read, retrying: {_load.Exception?.GetBaseException().Message}");
            _load = null;
            _retryAt = _ctx.Now + LoadRetrySeconds;
            return;
        }
        var rows = _load.Result;
        _book = MineralBook.From(rows);
        _load = null;
        GD.Print($"server: mineral log loaded, {rows.Count} finds replayed");
    }

    /// <summary>Join: the nodes (the finds follow from <see cref="Tick"/> once the book is there).</summary>
    public void SendOnJoin(ServerClient c)
    {
        c.FindsSent = false;
        if (_nodes != null) _ctx.SendEvent(c, EventKind.MineralNodes, NodesPayload());
    }

    void SendFinds(ServerClient c, long playerId, byte justFound) =>
        _ctx.SendEvent(c, EventKind.MineralFinds, MineralRules.EncodeFinds(_book!.RanksOf(playerId), justFound));

    /// <summary>A client held the key at a node: the server checks the player, the reach, the activity and the once-only rule.</summary>
    public void OnPick(ServerClient c, byte[] payload)
    {
        if (!MineralRules.TryDecodePick(payload, out var id) || _book is not { } book || _nodes == null) return;
        if (c.IsBot || c.W == null || c.Car != null || !_ctx.IsAlive(c.Id) || c.Profile is not { Persist: true } p) return;
        var node = _nodes.Find(id);
        var result = book.Check(p.DbId, node, node != null && MineralActivity.IsActive(node.MineralId, World()), c.Body.GlobalPosition.ToNumerics());
        if (result != MineralPickResult.Ok || node == null)
        {
            GD.Print($"server: {c.Name} mineral node {id} not taken: {result}");
            return;
        }
        var rank = book.Add(p.DbId, node.MineralId);
        _ = _store!.Minerals.AppendAsync(new MineralFind(p.DbId, node.MineralId, DateTime.UtcNow));
        SendFinds(c, p.DbId, (byte)MineralRules.IndexOf(node.MineralId));
        GD.Print($"server: {c.Name} found {node.MineralId}, {MineralFormat.Ordinal(rank)} finder");
    }
}
