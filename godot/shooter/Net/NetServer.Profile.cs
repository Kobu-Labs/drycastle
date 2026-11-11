using Godot;
using Suchohrad.ShooterCore;
using Suchohrad.ShooterServer.Persistence;

namespace Suchohrad.Shooter;

/// <summary>
/// Profile handshake (ADR 0003, ticket #107). A hello carries the player token; the server loads the profile through the
/// repositories off the tick, and the join (body, welcome, profile, spawn) waits for it. If the load is still running
/// after <see cref="ProfileTimeoutMs"/>, or there is no token or no store, the player joins as a guest: default loadouts,
/// nothing saved. Loadout changes from the client are validated here and saved through the repositories.
/// </summary>
public partial class NetServer
{
    /// <summary>How long a join waits for the profile before it falls back to a guest profile.</summary>
    public const double ProfileTimeoutMs = 3000;

    /// <summary>What the server keeps for a joined player. Persist is false for guests: their changes live only in memory.</summary>
    sealed class PlayerProfile
    {
        public long DbId;
        public bool Persist;
        public string Name = "";
        public int Xp, Level = 1, Active;
        public Loadout[] Loadouts = new Loadout[Loadout.SlotCount];
        public List<Unlock> Unlocks = new();
        public CamoSpec Camo = CamoSpec.Default;
    }

    /// <summary>A profile still being read on the database thread. Only the task is shared; the result is applied on the tick.</summary>
    sealed class ProfileLoad
    {
        public Task<PlayerProfile> Work = null!;
        public ulong StartedMs;
    }

    void StartProfileLoad(Client c, string token)
    {
        if (Progression == null || token.Length == 0)
        {
            AttachProfile(c, GuestProfile());
            Join(c);
            return;
        }
        c.Loading = new ProfileLoad { Work = LoadProfileAsync(Progression, token, slot => LoadoutRules.DefaultLoadout(slot, Weapons, Perks)), StartedMs = Time.GetTicksMsec() };
    }

    /// <summary>Joins every client whose profile arrived, or whose wait ran out (guest). Runs on the tick.</summary>
    void CompleteProfileLoads()
    {
        foreach (var c in _clients.Values.ToList())
        {
            var ld = c.Loading;
            if (c.Joined || ld == null) continue;
            PlayerProfile? profile = null;
            if (ld.Work.IsCompletedSuccessfully) profile = ld.Work.Result;
            else if (ld.Work.IsFaulted)
            {
                GD.PrintErr($"server: profile load for {c.Name} failed, guest profile: {ld.Work.Exception?.GetBaseException().Message}");
                profile = GuestProfile();
            }
            else if (Time.GetTicksMsec() - ld.StartedMs >= ProfileTimeoutMs)
            {
                GD.PrintErr($"server: profile load for {c.Name} timed out after {ProfileTimeoutMs / 1000:F0} s, guest profile");
                profile = GuestProfile();
            }
            if (profile == null) continue;
            c.Loading = null; // a late result of a timed-out load is never read
            AttachProfile(c, profile);
            Join(c);
        }
    }

    /// <summary>Reads the profile. A slot with no primary was never set (a new profile): it gets the default kit, saved at once.</summary>
    static async Task<PlayerProfile> LoadProfileAsync(IProgressionStore store, string token, Func<int, Loadout> defaults)
    {
        var p = await store.Profiles.GetOrCreateAsync(token);
        var rows = await store.Loadouts.GetAllAsync(p.Id);
        var active = await store.Loadouts.GetActiveSlotAsync(p.Id);
        var unlocks = await store.Unlocks.GetAllAsync(p.Id);
        var camo = await store.Camo.GetAsync(p.Id);
        var slots = Enumerable.Range(0, Loadout.SlotCount).Select(Loadout.Empty).ToArray();
        foreach (var l in rows) if (l.Slot is >= 0 and <= Loadout.MaxSlot) slots[l.Slot] = l;
        for (var i = 0; i < slots.Length; i++)
        {
            if (slots[i].PrimaryWeapon != "") continue;
            slots[i] = defaults(i);
            await store.Loadouts.SaveAsync(p.Id, slots[i]);
        }
        return new PlayerProfile
        {
            DbId = p.Id, Persist = true, Name = p.Name, Xp = p.Xp, Level = p.Level,
            Loadouts = slots, Active = Math.Clamp(active, 0, Loadout.MaxSlot), Unlocks = unlocks.ToList(), Camo = camo ?? CamoSpec.Default,
        };
    }

    PlayerProfile GuestProfile()
    {
        var slots = Enumerable.Range(0, Loadout.SlotCount).Select(i => LoadoutRules.DefaultLoadout(i, Weapons, Perks)).ToArray();
        return new PlayerProfile { Persist = false, Level = 1, Loadouts = slots };
    }

    /// <summary>Makes <paramref name="p"/> the client's profile and stores a changed display name (the name the player typed).</summary>
    void AttachProfile(Client c, PlayerProfile p)
    {
        if (p.Persist && p.Name != c.Name) _ = Progression!.Profiles.SetNameAsync(p.DbId, c.Name);
        p.Name = c.Name;
        p.Camo = p.Camo.Validate(CamoData) == null ? p.Camo : CamoSpec.Default; // a stored look that camo.json no longer has falls back to the default
        c.Profile = p;
    }

    void SendProfile(Client c)
    {
        var p = c.Profile!;
        Send(c, new ProfileMsg(p.Name, p.Xp, p.Level, p.Active, p.Loadouts, p.Unlocks));
    }

    void Reject(Client c, string reason)
    {
        GD.Print($"server: rejecting {(c.Name == "" ? "a client" : c.Name)}: {reason}");
        Send(c, new ErrorMsg(reason));
        _host.Flush();
        c.Peer.PeerDisconnectLater(0); // after the error has gone out
    }

    void OnProfileMessage(Client c, ReliableMsg? m)
    {
        switch (m)
        {
            case SaveLoadoutMsg s: OnSaveLoadout(c, s.Loadout); break;
            case SelectSlotMsg sl: OnSelectSlot(c, sl.Slot); break;
            case SaveCamoMsg sc: OnSaveCamo(c, sc.Spec); break;
        }
    }

    void OnSaveLoadout(Client c, Loadout l)
    {
        var p = c.Profile!;
        if (LoadoutRules.ValidateLoadout(l, Weapons, p.Level, p.Unlocks, PerkLevels, Killstreaks.UnlockLevels) is { } err)
        {
            Send(c, new ErrorMsg(err));
            return;
        }
        p.Loadouts[l.Slot] = l;
        if (p.Persist) _ = Progression!.Loadouts.SaveAsync(p.DbId, l);
        SendProfile(c);
    }

    void OnSelectSlot(Client c, int slot)
    {
        var p = c.Profile!;
        if (LoadoutRules.ValidateSlot(slot) is { } err)
        {
            Send(c, new ErrorMsg(err));
            return;
        }
        p.Active = slot;
        if (p.Persist) _ = Progression!.Loadouts.SetActiveSlotAsync(p.DbId, slot);
        SendProfile(c);
    }
}
