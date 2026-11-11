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
        /// <summary>One look per loadout slot (#141). The look other players see is the active slot's.</summary>
        public CamoSpec[] Camos = Enumerable.Repeat(CamoSpec.Default, Loadout.SlotCount).ToArray();
        public CamoSpec Camo => Camos[Math.Clamp(Active, 0, Loadout.MaxSlot)];
    }

    /// <summary>A profile still being read on the database thread. Only the task is shared; the result is applied on the tick.</summary>
    sealed class ProfileLoad
    {
        public Task<PlayerProfile> Work = null!;
        public ulong StartedMs;
    }

    /// <summary>Dev mode (issue #149, <c>--unlock-all</c>): every item unlocked, showcase starter loadouts, nothing of it saved. Set by the host.</summary>
    public bool UnlockAll;

    /// <summary>The starter loadout of one slot: the release default, or the dev kit in dev mode.</summary>
    Loadout StarterLoadout(int slot) => UnlockAll ? DevUnlocks.DevKit(slot) : LoadoutRules.DefaultLoadout(slot, Weapons, Perks);

    /// <summary>The unlock rows a player is told about and loadouts are validated against: the stored rows, or every item in dev mode.</summary>
    IReadOnlyList<Unlock> ReportedUnlocks(PlayerProfile p) =>
        UnlockAll ? DevUnlocks.Expand(p.Unlocks, Weapons, Perks, Killstreaks.UnlockLevels) : p.Unlocks;

    void StartProfileLoad(Client c, string token)
    {
        if (Progression == null || token.Length == 0)
        {
            AttachProfile(c, GuestProfile());
            Join(c);
            return;
        }
        c.Loading = new ProfileLoad { Work = LoadProfileAsync(Progression, token, StarterLoadout, saveDefaults: !UnlockAll), StartedMs = Time.GetTicksMsec() };
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
    static async Task<PlayerProfile> LoadProfileAsync(IProgressionStore store, string token, Func<int, Loadout> defaults, bool saveDefaults)
    {
        var p = await store.Profiles.GetOrCreateAsync(token);
        var rows = await store.Loadouts.GetAllAsync(p.Id);
        var active = await store.Loadouts.GetActiveSlotAsync(p.Id);
        var unlocks = await store.Unlocks.GetAllAsync(p.Id);
        var camos = Enumerable.Repeat(CamoSpec.Default, Loadout.SlotCount).ToArray();
        foreach (var (slot, spec) in await store.Camo.GetAllAsync(p.Id))
            if (slot is >= 0 and <= Loadout.MaxSlot) camos[slot] = spec;
        var slots = Enumerable.Range(0, Loadout.SlotCount).Select(Loadout.Empty).ToArray();
        foreach (var l in rows) if (l.Slot is >= 0 and <= Loadout.MaxSlot) slots[l.Slot] = l;
        for (var i = 0; i < slots.Length; i++)
        {
            if (slots[i].PrimaryWeapon != "")
            {
                if (slots[i].Name != "") continue;
                slots[i] = slots[i] with { Name = LoadoutRules.DefaultName(i) }; // a row from before loadout names (#145)
            }
            else slots[i] = defaults(i);
            if (saveDefaults) await store.Loadouts.SaveAsync(p.Id, slots[i]);
        }
        return new PlayerProfile
        {
            DbId = p.Id, Persist = true, Name = p.Name, Xp = p.Xp, Level = p.Level,
            Loadouts = slots, Active = Math.Clamp(active, 0, Loadout.MaxSlot), Unlocks = unlocks.ToList(), Camos = camos,
        };
    }

    PlayerProfile GuestProfile()
    {
        var slots = Enumerable.Range(0, Loadout.SlotCount).Select(StarterLoadout).ToArray();
        return new PlayerProfile { Persist = false, Level = 1, Loadouts = slots };
    }

    /// <summary>Makes <paramref name="p"/> the client's profile and stores a changed display name (the name the player typed).</summary>
    void AttachProfile(Client c, PlayerProfile p)
    {
        if (p.Persist && p.Name != c.Name) _ = Progression!.Profiles.SetNameAsync(p.DbId, c.Name);
        p.Name = c.Name;
        for (var i = 0; i < p.Camos.Length; i++)
            if (p.Camos[i].Validate(CamoData) != null) p.Camos[i] = CamoSpec.Default; // a stored look that camo.json no longer has falls back to the default
        c.Profile = p;
    }

    void SendProfile(Client c)
    {
        var p = c.Profile!;
        Send(c, new ProfileMsg(p.Name, p.Xp, p.Level, p.Active, p.Loadouts, ReportedUnlocks(p), p.Camos));
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
            case SaveLoadoutMsg s: OnSaveLoadout(c, s); break;
            case SelectSlotMsg sl: OnSelectSlot(c, sl.Slot); break;
            case SaveCamoMsg sc: OnSaveCamo(c, sc.Slot, sc.Spec); break;
            case EventMsg { Kind: (byte)EventKind.CopyLoadout } cp: OnCopyLoadout(c, cp.Payload); break;
            case EventMsg { Kind: (byte)EventKind.ResetLoadout } rs: OnResetLoadout(c, rs.Payload); break;
        }
    }

    /// <summary>Answers one save (ticket #145): the client matches it by seq. Failures change nothing on the server.</summary>
    void AckSave(Client c, int slot, uint seq, string? error)
    {
        var ack = new LoadoutAckInfo(slot, seq, error == null, error ?? "");
        Send(c, new EventMsg((byte)EventKind.LoadoutAck, LoadoutEvents.EncodeAck(ack)));
    }

    /// <summary>
    /// Saves one slot from the editor (autosave sends these). The name is trimmed and checked (ValidateName), then the loadout
    /// is checked against the data files and the player's unlocks. Ack ok on success, otherwise the reason.
    /// </summary>
    void OnSaveLoadout(Client c, SaveLoadoutMsg m)
    {
        var p = c.Profile!;
        var l = m.Loadout;
        if (LoadoutRules.ValidateSlot(l.Slot) is { } slotErr) { AckSave(c, l.Slot, m.Seq, slotErr); return; }
        var name = LoadoutRules.ValidateName(l.Name, out var nameErr);
        if (nameErr != null) { AckSave(c, l.Slot, m.Seq, nameErr); return; }
        l = l with { Name = name! };
        if (LoadoutRules.ValidateLoadout(l, Weapons, p.Level, ReportedUnlocks(p), PerkLevels, Killstreaks.UnlockLevels) is { } err)
        {
            AckSave(c, l.Slot, m.Seq, err);
            return;
        }
        p.Loadouts[l.Slot] = l;
        if (p.Persist) _ = Progression!.Loadouts.SaveAsync(p.DbId, l);
        AckSave(c, l.Slot, m.Seq, null);
        SendProfile(c);
    }

    /// <summary>
    /// Copies one slot's loadout and camouflage into another (ticket #145). The destination keeps its own name, and the active
    /// slot does not change. The copied loadout is validated like a save. Invalid copies get an <see cref="ErrorMsg"/>.
    /// </summary>
    void OnCopyLoadout(Client c, byte[] payload)
    {
        var p = c.Profile!;
        if (LoadoutEvents.DecodeCopy(payload) is not { } pair) { Send(c, new ErrorMsg("malformed copy request")); return; }
        var (from, to) = pair;
        if (LoadoutRules.ValidateSlot(from) is { } fromErr) { Send(c, new ErrorMsg(fromErr)); return; }
        if (LoadoutRules.ValidateSlot(to) is { } toErr) { Send(c, new ErrorMsg(toErr)); return; }
        if (from == to) { Send(c, new ErrorMsg("copy needs two different slots")); return; }
        var next = LoadoutRules.CopyInto(p.Loadouts[from], p.Loadouts[to]);
        if (LoadoutRules.ValidateLoadout(next, Weapons, p.Level, ReportedUnlocks(p), PerkLevels, Killstreaks.UnlockLevels) is { } err)
        {
            Send(c, new ErrorMsg(err));
            return;
        }
        p.Loadouts[to] = next;
        p.Camos[to] = p.Camos[from];
        if (p.Persist)
        {
            _ = Progression!.Loadouts.SaveAsync(p.DbId, next);
            _ = Progression.Camo.SaveAsync(p.DbId, to, p.Camos[to]);
        }
        SendProfile(c);
        if (to == p.Active) BroadcastAppearance(c);
    }

    /// <summary>
    /// Restores one slot's loadout to the starter kit and its default name (ticket #145). The slot's camouflage is kept:
    /// the reset covers the loadout items, not the look.
    /// </summary>
    void OnResetLoadout(Client c, byte[] payload)
    {
        var p = c.Profile!;
        if (LoadoutEvents.DecodeReset(payload) is not { } slot) { Send(c, new ErrorMsg("malformed reset request")); return; }
        if (LoadoutRules.ValidateSlot(slot) is { } err) { Send(c, new ErrorMsg(err)); return; }
        var next = StarterLoadout(slot); // the showcase kit in dev mode (#149)
        p.Loadouts[slot] = next;
        if (p.Persist) _ = Progression!.Loadouts.SaveAsync(p.DbId, next);
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
        BroadcastAppearance(c); // the look other players see is the newly active loadout's (#141)
    }
}
