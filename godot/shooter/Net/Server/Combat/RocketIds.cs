namespace Drycastle.Shooter.Net.Server.Combat;

/// <summary>The ids of server-owned projectiles and blasts (rockets, homing missiles, a test blast): from 0x8000 up, wrapping, since player ids count up from 1.</summary>
internal sealed class RocketIds
{
    ushort _next = 0x8000;

    public ushort Next()
    {
        var id = _next;
        _next = _next == ushort.MaxValue ? (ushort)0x8000 : (ushort)(_next + 1);
        return id;
    }
}
