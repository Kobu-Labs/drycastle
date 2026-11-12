namespace Drycastle.ShooterCore;

/// <summary>
/// Player ids on the wire (#197). 0 means "no hello yet", cars start at <see cref="VehicleRecord.FirstId"/>, rockets at 0x8000
/// and target dummies at 50000, so players live in 1 to 0x3FFF. Ids are handed out in turn and an id in use is skipped, so a
/// server that runs for weeks never wraps into another range or hands out an id twice.
/// </summary>
public sealed class PlayerIds
{
    public const ushort First = 1;
    public const ushort Last = VehicleRecord.FirstId - 1;

    ushort _next = First;

    /// <summary>The next free id, or 0 when every id is in use.</summary>
    public ushort Allocate(Func<ushort, bool> inUse)
    {
        for (var tries = 0; tries <= Last - First; tries++)
        {
            var id = _next;
            _next = id >= Last ? First : (ushort)(id + 1);
            if (!inUse(id)) return id;
        }
        return 0;
    }
}

/// <summary>
/// Limits how often the server runs a second input in one tick to catch up a backed-up queue (#197). A burst after a stall or
/// loss is covered by the stored credit; a client that keeps its queue full gets only the refill rate on top of normal speed,
/// not double.
/// </summary>
public struct CatchUpBudget
{
    public const double Capacity = 8, RefillPerTick = 0.25;
    /// <summary>Queued inputs above which a second one may run.</summary>
    public const int QueueThreshold = 3;

    double _credit;
    bool _started;

    /// <summary>How many inputs to run this tick (1 or 2) for a queue of <paramref name="queued"/>. Call once per tick.</summary>
    public int Steps(int queued)
    {
        if (!_started) { _credit = Capacity; _started = true; }
        else _credit = Math.Min(Capacity, _credit + RefillPerTick);
        if (queued <= QueueThreshold || _credit < 1) return 1;
        _credit -= 1;
        return 2;
    }
}
