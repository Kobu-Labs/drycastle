using Drycastle.ShooterCore.Fsm;

namespace Drycastle.ShooterCore.Vehicles;

/// <summary>Whether a vehicle's cargo doors (the boot lid, the hatch, the van's rear doors) stand shut or open.</summary>
public enum CargoDoorState : byte { Closed = 0, Open = 1 }

/// <summary>
/// The cargo doors of one vehicle on the server (machine <c>Cargo.Doors</c>, docs/cartel.md "Cargo vehicles"): a player on foot at the vehicle's access point works them with the interact key and they stay as
/// left, also while the vehicle drives. A spawn, a reset or a respawn shuts them (<see cref="Close"/>). The swing is the client's (<see cref="CargoDoorSwing"/>); here there is only the state.
/// </summary>
public sealed class CargoDoors
{
    public static readonly StateMachineSpec<CargoDoorState> Spec = StateMachineSpec<CargoDoorState>
        .Define("Cargo.Doors", "The cargo doors of a vehicle (boot lid, hatch, van rear doors): shut, or open until somebody shuts them.", CargoDoorState.Closed)
        .Allow(CargoDoorState.Closed, CargoDoorState.Open)
        .Allow(CargoDoorState.Open, CargoDoorState.Closed)
        .Note(CargoDoorState.Closed, "The start state, and what a spawn, a reset or a respawn of the vehicle leaves. A throw into the cargo area is refused (it falls beside the vehicle).")
        .Note(CargoDoorState.Open, "A player on foot at the access point pressed the interact key. Stays open while the vehicle drives; a throw loads, the key at the access point shuts it.")
        .Build();

    readonly StateMachine<CargoDoorState> _fsm = new(Spec);

    public CargoDoorState State => _fsm.Current;
    public bool IsOpen => _fsm.Is(CargoDoorState.Open);

    /// <summary>The interact key at the access point: open when shut, shut when open. The new state.</summary>
    public bool Toggle()
    {
        _fsm.Enter(IsOpen ? CargoDoorState.Closed : CargoDoorState.Open);
        return IsOpen;
    }

    /// <summary>Shut (a spawn, a reset, a respawn). True when they were open.</summary>
    public bool Close()
    {
        if (!IsOpen) return false;
        _fsm.Enter(CargoDoorState.Closed);
        return true;
    }
}

/// <summary>The cargo doors event's payload (<see cref="Net.EventKind.CargoDoors"/>): u16 vehicle id, u8 open (1) or shut (0).</summary>
public static class CargoDoorsCodec
{
    public static byte[] Encode(ushort vehicle, bool open) => new[] { (byte)vehicle, (byte)(vehicle >> 8), (byte)(open ? 1 : 0) };

    public static bool TryDecode(ReadOnlySpan<byte> p, out ushort vehicle, out bool open)
    {
        vehicle = 0; open = false;
        if (p.Length < 3 || p[2] > 1) return false;
        vehicle = (ushort)(p[0] | p[1] << 8);
        open = p[2] == 1;
        return true;
    }
}
