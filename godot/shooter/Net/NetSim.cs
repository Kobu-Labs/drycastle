using System.Globalization;
using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// Network impairment for tests (issue #93), applied in the application send path of both the client and the server
/// (no tc, no root). Option string: <c>latency_ms=50,jitter_ms=5,loss=0.03</c> (every key optional).
/// Every outgoing packet is delayed by latency +/- jitter (one way, per sender, and never reordered: ENet sequences at send time);
/// unreliable datagrams (the snapshots and inputs on channel 1) are additionally dropped with probability <c>loss</c>.
/// Reliable packets are delayed but never dropped: loss recovery of those is ENet's job and the simulation does not model its retransmit timeouts.
/// </summary>
public sealed class NetSim
{
    public double LatencyMs, JitterMs, Loss;
    public long Sent, Dropped;
    readonly Random _rng;
    readonly Queue<(double Due, ENetPacketPeer Peer, int Chan, byte[] Data, int Flags)> _q = new();
    double _lastDue;

    public NetSim(int seed = 0) => _rng = seed == 0 ? new Random() : new Random(seed);

    public bool Active => LatencyMs > 0 || JitterMs > 0 || Loss > 0;

    /// <summary>Parses the option string; null when empty. Throws on unknown keys so a typo does not silently test the clean path.</summary>
    public static NetSim? Parse(string spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return null;
        var s = new NetSim();
        foreach (var kv in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = kv.Split('=');
            if (p.Length != 2 || !double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < 0) throw new InvalidOperationException($"bad --net-sim entry '{kv}' (latency_ms, jitter_ms, loss)");
            switch (p[0])
            {
                case "latency_ms": s.LatencyMs = v; break;
                case "jitter_ms": s.JitterMs = v; break;
                case "loss": if (v > 1) throw new InvalidOperationException("--net-sim loss is a fraction 0..1"); s.Loss = v; break;
                default: throw new InvalidOperationException($"unknown --net-sim key '{p[0]}' (latency_ms, jitter_ms, loss)");
            }
        }
        return s;
    }

    public override string ToString() => $"latency_ms={LatencyMs},jitter_ms={JitterMs},loss={Loss}";

    static double Now => Time.GetTicksUsec() / 1e6;

    /// <summary>Drop-in for <c>peer.Send</c>.</summary>
    public void Send(ENetPacketPeer peer, int chan, byte[] data, int flags)
    {
        if (!Active) { peer.Send(chan, data, flags); return; }
        var reliable = (flags & (int)ENetPacketPeer.FlagReliable) != 0;
        if (!reliable)
        {
            Sent++;
            if (Loss > 0 && _rng.NextDouble() < Loss) { Dropped++; return; }
        }
        var due = Now + (LatencyMs + (JitterMs > 0 ? (_rng.NextDouble() * 2 - 1) * JitterMs : 0)) / 1000.0;
        due = Math.Max(due, _lastDue); // keep send order
        _lastDue = due;
        _q.Enqueue((due, peer, chan, data, flags));
    }

    /// <summary>Sends everything that is due. Returns true if anything went out (the caller flushes its host).</summary>
    public bool Pump()
    {
        var any = false;
        var now = Now;
        while (_q.Count > 0 && _q.Peek().Due <= now)
        {
            var (_, peer, chan, data, flags) = _q.Dequeue();
            if (!GodotObject.IsInstanceValid(peer) || peer.GetState() != ENetPacketPeer.PeerState.Connected) continue;
            peer.Send(chan, data, flags);
            any = true;
        }
        return any;
    }

    public int Pending => _q.Count;
}
