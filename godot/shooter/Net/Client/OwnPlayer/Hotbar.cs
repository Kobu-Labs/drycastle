namespace Drycastle.Shooter.Net.Client.OwnPlayer;

/// <summary>The hotbar the server applied at the spawn (weapons.json indexes: primary, secondary, knife, ...) and the weapon slot the player wants in hand (keys 1-5, the mouse wheel).</summary>
public sealed class Hotbar
{
    /// <summary>weapons.json indexes of the spawn hotbar (keys 1-3 and the cycle).</summary>
    public int[] Slots { get; private set; } = Array.Empty<int>();

    /// <summary>The 1-based weapons.json slot the player wants in hand: what the input carries to the server.</summary>
    public int Want = 1;

    /// <summary>The first hotbar slot (the primary), as a 1-based weapons.json slot.</summary>
    public int Start => Slots.Length > 0 ? Slots[0] + 1 : 1;

    /// <summary>Position of a weapons.json slot (1-based) on the hotbar, 0-based; -1 when it is not on the hotbar.</summary>
    public int PosOf(int slot) => Array.IndexOf(Slots, slot - 1);

    /// <summary>The next (dir = 1) or previous (dir = -1) hotbar slot after <paramref name="slot"/>, wrapping around.</summary>
    public int Cycle(int slot, int dir)
    {
        if (Slots.Length == 0) return slot;
        var i = PosOf(slot);
        var next = i < 0 ? 0 : ((i + dir) % Slots.Length + Slots.Length) % Slots.Length;
        return Slots[next] + 1;
    }

    /// <summary>The hotbar of the active loadout at a spawn.</summary>
    public void Set(int[] slots) => Slots = slots;

    /// <summary>The server's hotbar after a pickup. A selected weapon that was replaced hands the selection to its new weapon.</summary>
    public void Replace(int[] hot)
    {
        for (var i = 0; i < Math.Min(hot.Length, Slots.Length); i++)
            if (hot[i] != Slots[i] && Want == Slots[i] + 1) Want = hot[i] + 1;
        Slots = hot;
    }
}
