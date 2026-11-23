using System.Diagnostics;
using System.Globalization;
using System.Text;
using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// Network audit for the perf harness (<c>--perf-out DIR</c> only; null and free otherwise). Every 10 s one block is appended to
/// DIR/audit.txt: bytes sent by packet kind (per client per second), the parts of the <c>match</c> tick phase, the world entities by
/// type (live, and changed per tick), and what an interest radius would keep: the share of changed world entities and of other players
/// within 200/400/800/1200 m of each player.
/// </summary>
public sealed class PerfAudit
{
    public static readonly string[] Subs = { "match_update", "event_prepare", "entities_tick", "citizens", "pets", "wildlife", "mass", "thieves", "rozhlas" };
    public static readonly float[] Radii = { 200, 400, 800, 1200 };

    readonly string _path;
    readonly Dictionary<string, long> _bytes = new();
    readonly long[] _subUs = new long[Subs.Length];
    readonly Dictionary<byte, (long Live, long Changed)> _types = new();
    readonly Dictionary<byte, long[]> _typeNear = new(); // changed entity-viewer pairs in range, by type and radius
    readonly Dictionary<byte, long> _typePairs = new();
    readonly Dictionary<ushort, WorldState> _prev = new();
    readonly long[] _playerNear = new long[Radii.Length];
    long _playerPairs, _ticks, _clientTicks, _last;
    double _nextAt = 10;

    public PerfAudit(string dir) { _path = System.IO.Path.Combine(dir, "audit.txt"); System.IO.File.WriteAllText(_path, ""); }

    public void Sent(int chan, byte[] data)
    {
        var key = chan == Wire.ChanData ? (data[0] == Wire.TWorldSnapshot ? "world_snapshot" : "entity_snapshot")
            : data[0] == Wire.TEvent && data.Length > 1 ? "ev:" + (EventKind)data[1] : "msg:" + data[0];
        _bytes[key] = _bytes.GetValueOrDefault(key) + data.Length;
    }

    public void Begin() => _last = Stopwatch.GetTimestamp();

    public void Lap(int sub)
    {
        var now = Stopwatch.GetTimestamp();
        _subUs[sub] += (now - _last) * 1_000_000 / Stopwatch.Frequency;
        _last = now;
    }

    /// <summary>Once a tick: the world entities sent, the viewers (every player's position) and how many of them are connected clients.</summary>
    public void Tick(IReadOnlyList<WorldEntry> world, List<System.Numerics.Vector3> viewers, int clients, double elapsed)
    {
        _ticks++; _clientTicks += clients;
        foreach (var e in world)
        {
            var changed = !_prev.TryGetValue(e.Id, out var p) || p != e.State;
            _prev[e.Id] = e.State;
            var t = _types.GetValueOrDefault(e.Type);
            _types[e.Type] = (t.Live + 1, t.Changed + (changed ? 1 : 0));
            if (!changed) continue;
            if (!_typeNear.TryGetValue(e.Type, out var near)) _typeNear[e.Type] = near = new long[Radii.Length];
            var pos = e.State.Position;
            foreach (var v in viewers)
            {
                var d = new System.Numerics.Vector2(pos.X - v.X, pos.Z - v.Z).Length();
                for (var i = 0; i < Radii.Length; i++) if (d <= Radii[i]) near[i]++;
            }
            _typePairs[e.Type] = _typePairs.GetValueOrDefault(e.Type) + viewers.Count;
        }
        for (var a = 0; a < viewers.Count; a++)
            for (var b = 0; b < viewers.Count; b++)
            {
                if (a == b) continue;
                var d = new System.Numerics.Vector2(viewers[a].X - viewers[b].X, viewers[a].Z - viewers[b].Z).Length();
                for (var i = 0; i < Radii.Length; i++) if (d <= Radii[i]) _playerNear[i]++;
                _playerPairs++;
            }
        if (elapsed < _nextAt) return;
        _nextAt = elapsed + 10;
        Write(elapsed);
    }

    void Write(double elapsed)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        var seconds = _ticks / (double)Wire.TickRate;
        var clients = _ticks == 0 ? 0 : _clientTicks / (double)_ticks;
        sb.Append(inv, $"== t {elapsed:F0} s, {_ticks} ticks, clients {clients:F1}\n");
        foreach (var kv in _bytes.OrderByDescending(k => k.Value))
            sb.Append(inv, $"bytes {kv.Key} {(clients > 0 ? kv.Value / seconds / clients : 0):F0} B/s per client\n");
        for (var i = 0; i < Subs.Length; i++) sb.Append(inv, $"sub {Subs[i]} {(_ticks == 0 ? 0 : _subUs[i] / (double)_ticks):F0} us/tick\n");
        foreach (var kv in _types.OrderByDescending(k => k.Value.Changed))
        {
            var name = WorldEntityRegistry.Shared.Find(kv.Key)?.Name ?? kv.Key.ToString(inv);
            var pairs = Math.Max(1, _typePairs.GetValueOrDefault(kv.Key));
            var near = _typeNear.GetValueOrDefault(kv.Key) ?? new long[Radii.Length];
            sb.Append(inv, $"type {name}: live {kv.Value.Live / (double)_ticks:F1}, changed/tick {kv.Value.Changed / (double)_ticks:F1}, changed within");
            for (var i = 0; i < Radii.Length; i++) sb.Append(inv, $" {Radii[i]:F0}m {100.0 * near[i] / pairs:F0}%");
            sb.Append('\n');
        }
        sb.Append("players within");
        for (var i = 0; i < Radii.Length; i++) sb.Append(inv, $" {Radii[i]:F0}m {100.0 * _playerNear[i] / Math.Max(1, _playerPairs):F0}%");
        sb.Append('\n');
        System.IO.File.AppendAllText(_path, sb.ToString());
        _bytes.Clear(); Array.Clear(_subUs); _types.Clear(); _typeNear.Clear(); _typePairs.Clear(); Array.Clear(_playerNear);
        _playerPairs = _ticks = _clientTicks = 0;
    }
}

public partial class NetServer
{
    PerfAudit? _audit;
    readonly List<System.Numerics.Vector3> _auditViewers = new();

    void AuditTick(IReadOnlyList<WorldEntry> world)
    {
        if (_audit == null) return;
        _auditViewers.Clear();
        foreach (var c in _byId.Values) _auditViewers.Add(N(c.Body.GlobalPosition));
        _audit.Tick(world, _auditViewers, _clients.Values.Count(c => c.Joined), _elapsed);
    }
}
