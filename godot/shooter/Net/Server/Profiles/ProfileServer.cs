using Godot;
using Drycastle.ShooterCore.Camo;
using Drycastle.ShooterCore.Killstreaks;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Perks;
using Drycastle.ShooterCore.Progression;
using Drycastle.ShooterCore.Weapons;
using Drycastle.ShooterServer.Persistence;

namespace Drycastle.Shooter.Net.Server.Profiles;

/// <summary>
/// A player's saved side (ADR 0003, ticket #107): the profile handshake, the loadouts, the camouflage and the player's own Respawn. A hello carries the player token; the
/// profile is loaded through the repositories off the tick, and the join (body, welcome, profile, spawn) waits for it. If the load is still running after
/// <see cref="ProfileTimeoutMs"/>, or there is no token or no store, the player joins as a guest: default loadouts, nothing saved. Changes from the client are
/// validated here and saved through the repositories.
/// </summary>
internal sealed class ProfileServer
{
    /// <summary>How long a join waits for the profile before it falls back to a guest profile.</summary>
    public const double ProfileTimeoutMs = 3000;

    const string SaveFailed = "Could not save, try again in a moment.";

    readonly ServerContext _ctx;
    readonly IProgressionStore? _store;
    readonly bool _unlockAll;
    readonly WeaponsFile _weapons;
    readonly PerksFile _perks;
    readonly CamoFile _camo;
    readonly KillstreakFile _killstreaks;
    readonly Action<ServerClient> _join;
    readonly Action<ServerClient> _attached;
    readonly Func<ServerClient, bool> _allowSave;
    IReadOnlyDictionary<string, int>? _perkLevels;

    /// <summary>A save whose answer waits for the database write (#195). Only the task crosses threads; the answer runs on the tick.</summary>
    readonly List<(Task Write, ServerClient C, Action<bool> Done)> _pendingSaves = new();

    /// <param name="store">The progression store; null = every player is a guest.</param>
    /// <param name="unlockAll">Dev mode (issue #149): every item unlocked, showcase starter loadouts, nothing of it saved.</param>
    /// <param name="join">Joins a client once it has a profile.</param>
    /// <param name="attached">A client has its profile now: whatever else of the server hangs on it (the log's names, the collection rewards, the companions) follows.</param>
    /// <param name="allowSave">Spends one save token of the client; false when it saves too fast.</param>
    public ProfileServer(ServerContext ctx, IProgressionStore? store, bool unlockAll, WeaponsFile weapons, PerksFile perks, CamoFile camo, KillstreakFile killstreaks,
        Action<ServerClient> join, Action<ServerClient> attached, Func<ServerClient, bool> allowSave)
    {
        _ctx = ctx; _store = store; _unlockAll = unlockAll; _weapons = weapons; _perks = perks; _camo = camo; _killstreaks = killstreaks;
        _join = join; _attached = attached; _allowSave = allowSave;
    }

    /// <summary>The starter loadout of one slot: the release default, or the dev kit in dev mode.</summary>
    Loadout StarterLoadout(int slot) => _unlockAll ? DevUnlocks.DevKit(slot) : LoadoutRules.DefaultLoadout(slot, _weapons, _perks);

    /// <summary>The unlock rows a player is told about and loadouts are validated against: the stored rows, or every item in dev mode.</summary>
    IReadOnlyList<Unlock> ReportedUnlocks(PlayerProfile p) =>
        _unlockAll ? DevUnlocks.Expand(p.Unlocks, _weapons, _perks, _killstreaks.UnlockLevels) : p.Unlocks;

    IReadOnlyDictionary<string, int> PerkLevels => _perkLevels ??= _perks.Perks.ToDictionary(p => p.Id, p => p.UnlockLevel);

    string? Invalid(Loadout l, PlayerProfile p) => LoadoutRules.ValidateLoadout(l, _weapons, p.Level, ReportedUnlocks(p), PerkLevels, _killstreaks.UnlockLevels);

    /// <summary>The ids of the reward items a player owns (unlock rows of kind "item", #473), so a spawn issues the upgrade in place of the standard item.</summary>
    public static IReadOnlySet<string> OwnedItems(ServerClient c) =>
        c.Profile is { } p ? p.Unlocks.Where(u => u.Kind == "item").Select(u => u.ItemId).ToHashSet() : new HashSet<string>();

    /// <summary>The hello's token starts the load; a guest (no store or no token) joins at once.</summary>
    public void StartLoad(ServerClient c, string token)
    {
        if (_store == null || token.Length == 0)
        {
            Attach(c, NewGuest());
            _join(c);
            return;
        }
        c.Session.Enter(SessionPhase.Loading);
        c.Loading = new ProfileLoad { Work = LoadProfileAsync(_store, token, StarterLoadout, saveDefaults: !_unlockAll), StartedMs = Time.GetTicksMsec() };
    }

    /// <summary>Each tick: joins every client whose profile arrived, or whose wait ran out (guest), then answers every save whose write has finished.</summary>
    public void Tick()
    {
        CompleteLoads();
        CompleteSaves();
    }

    void CompleteLoads()
    {
        foreach (var c in _ctx.Connections.ToList())
        {
            if (c.Phase != SessionPhase.Loading || c.Loading is not { } ld) continue;
            PlayerProfile? profile = null;
            if (ld.Work.IsCompletedSuccessfully) profile = ld.Work.Result;
            else if (ld.Work.IsFaulted)
            {
                GD.PrintErr($"server: profile load for {c.Name} failed, guest profile: {ld.Work.Exception?.GetBaseException().Message}");
                profile = NewGuest();
            }
            else if (Time.GetTicksMsec() - ld.StartedMs >= ProfileTimeoutMs)
            {
                GD.PrintErr($"server: profile load for {c.Name} timed out after {ProfileTimeoutMs / 1000:F0} s, guest profile");
                profile = NewGuest();
            }
            if (profile == null) continue;
            c.Loading = null; // a late result of a timed-out load is never read
            Attach(c, profile);
            _join(c);
        }
    }

    /// <summary>Reads the profile. A slot with no primary was never set (a new profile): it gets the default kit, saved at once.</summary>
    static async Task<PlayerProfile> LoadProfileAsync(IProgressionStore store, string token, Func<int, Loadout> defaults, bool saveDefaults)
    {
        var p = await store.Profiles.GetOrCreateAsync(token);
        var rows = await store.Loadouts.GetAllAsync(p.Id);
        var active = await store.Loadouts.GetActiveSlotAsync(p.Id);
        var unlocks = await store.Unlocks.GetAllAsync(p.Id);
        var companion = await store.Profiles.GetCompanionAsync(p.Id);
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
            if (!saveDefaults) continue;
            try { await store.Loadouts.SaveAsync(p.Id, slots[i]); }
            catch (Exception e) { GD.PrintErr($"server: could not save the default loadout of slot {i}: {e.Message}"); } // the join goes on with it in memory
        }
        return new PlayerProfile
        {
            DbId = p.Id, Persist = true, Name = p.Name, Xp = p.Xp, Level = p.Level,
            Loadouts = slots, Active = Math.Clamp(active, 0, Loadout.MaxSlot), Unlocks = unlocks.ToList(), Camos = camos, Companion = companion,
        };
    }

    /// <summary>A profile that is not saved: the starter loadouts, level 1. The caller may change it before <see cref="Attach"/>.</summary>
    public PlayerProfile NewGuest()
    {
        var slots = Enumerable.Range(0, Loadout.SlotCount).Select(StarterLoadout).ToArray();
        return new PlayerProfile { Persist = false, Level = 1, Loadouts = slots };
    }

    /// <summary>Makes <paramref name="p"/> the client's profile and stores a changed display name (the name the player typed).</summary>
    public void Attach(ServerClient c, PlayerProfile p)
    {
        if (p.Persist && p.Name != c.Name) _ = _store!.Profiles.SetNameAsync(p.DbId, c.Name);
        p.Name = c.Name;
        for (var i = 0; i < p.Camos.Length; i++)
            if (p.Camos[i].Validate(_camo) != null) p.Camos[i] = CamoSpec.Default; // a stored look that camo.json no longer has falls back to the default
        c.Profile = p;
        _attached(c);
    }

    public void Send(ServerClient c)
    {
        var p = c.Profile!;
        _ctx.Send(c, new ProfileMsg(p.Name, p.Xp, p.Level, p.Active, p.Loadouts, ReportedUnlocks(p), p.Camos));
    }

    /// <summary>A loadout, camouflage or slot request of a joined client; one that saves too fast is refused.</summary>
    public void OnMessage(ServerClient c, ReliableMsg? m)
    {
        if (m is SaveLoadoutMsg or SelectSlotMsg or SaveCamoMsg or EventMsg { Kind: (byte)EventKind.CopyLoadout or (byte)EventKind.ResetLoadout } && !_allowSave(c))
        {
            if (m is SaveLoadoutMsg sl0) AckSave(c, sl0.Loadout.Slot, sl0.Seq, "Saving too fast, try again in a moment.");
            return;
        }
        switch (m)
        {
            case SaveLoadoutMsg s: SaveLoadout(c, s); break;
            case SelectSlotMsg sl: SelectSlot(c, sl.Slot); break;
            case SaveCamoMsg sc: SaveCamo(c, sc.Slot, sc.Spec); break;
            case EventMsg { Kind: (byte)EventKind.CopyLoadout } cp: CopyLoadout(c, cp.Payload); break;
            case EventMsg { Kind: (byte)EventKind.ResetLoadout } rs: ResetLoadout(c, rs.Payload); break;
        }
    }

    /// <summary>The player's own Respawn (ticket #284): selects the named loadout slot, then kills the player as a suicide; the normal respawn applies the loadout.</summary>
    public void OnRedeploy(ServerClient c, byte[] payload)
    {
        if (!Redeploy.TryDecode(payload, out var slot)) return;
        if (c.Car == null && _ctx.Tick <= c.HeldUntilTick) return; // in the Bigfoot's hand (the demon's pounce is the match's Held)
        if (!_ctx.Match.CanForceDeath(c.Id)) return;
        if (slot is { } s)
        {
            if (LoadoutRules.ValidateSlot(s) is { } err) { _ctx.Send(c, new ErrorMsg(err)); return; }
            if (s != c.Profile!.Active && !_allowSave(c)) return; // a changed slot is a write: the same rate limit as selecting it
            SelectSlot(c, s);
        }
        _ctx.Match.ForceDeath(c.Id);
    }

    /// <summary>Runs <paramref name="done"/> on a later tick with whether the write succeeded (a dropped or failed write is false).</summary>
    void AfterWrite(Task write, Action<bool> done, ServerClient c) => _pendingSaves.Add((write, c, done));

    /// <summary>Answers every save whose write has finished. A client that left in the meantime is not answered.</summary>
    void CompleteSaves()
    {
        for (var i = _pendingSaves.Count - 1; i >= 0; i--)
        {
            var (write, c, done) = _pendingSaves[i];
            if (!write.IsCompleted) continue;
            _pendingSaves.RemoveAt(i);
            if (c.Peer != null && _ctx.Connections.Contains(c)) done(write.IsCompletedSuccessfully);
        }
    }

    /// <summary>Answers one save (ticket #145): the client matches it by seq. Failures change nothing on the server.</summary>
    void AckSave(ServerClient c, int slot, uint seq, string? error)
    {
        var ack = new LoadoutAckInfo(slot, seq, error == null, error ?? "");
        _ctx.SendEvent(c, EventKind.LoadoutAck, LoadoutEvents.EncodeAck(ack));
    }

    void Error(ServerClient c, string text) => _ctx.Send(c, new ErrorMsg(text));

    /// <summary>
    /// Saves one slot from the editor (autosave sends these). The name is trimmed and checked (ValidateName), then the loadout
    /// is checked against the data files and the player's unlocks. Ack ok on success, otherwise the reason.
    /// </summary>
    void SaveLoadout(ServerClient c, SaveLoadoutMsg m)
    {
        var p = c.Profile!;
        var l = m.Loadout;
        if (LoadoutRules.ValidateSlot(l.Slot) is { } slotErr) { AckSave(c, l.Slot, m.Seq, slotErr); return; }
        var name = LoadoutRules.ValidateName(l.Name, out var nameErr);
        if (nameErr != null) { AckSave(c, l.Slot, m.Seq, nameErr); return; }
        l = l with { Name = name! };
        if (Invalid(l, p) is { } err)
        {
            AckSave(c, l.Slot, m.Seq, err);
            return;
        }
        p.Loadouts[l.Slot] = l;
        var slot = l.Slot;
        if (p.Persist) AfterWrite(_store!.Loadouts.SaveAsync(p.DbId, l), ok => AckSave(c, slot, m.Seq, ok ? null : SaveFailed), c);
        else AckSave(c, slot, m.Seq, null);
        Send(c);
    }

    /// <summary>
    /// Copies one slot's loadout and camouflage into another (ticket #145). The destination keeps its own name, and the active
    /// slot does not change. The copied loadout is validated like a save. Invalid copies get an <see cref="ErrorMsg"/>.
    /// </summary>
    void CopyLoadout(ServerClient c, byte[] payload)
    {
        var p = c.Profile!;
        if (LoadoutEvents.DecodeCopy(payload) is not { } pair) { Error(c, "malformed copy request"); return; }
        var (from, to) = pair;
        if (LoadoutRules.ValidateSlot(from) is { } fromErr) { Error(c, fromErr); return; }
        if (LoadoutRules.ValidateSlot(to) is { } toErr) { Error(c, toErr); return; }
        if (from == to) { Error(c, "copy needs two different slots"); return; }
        var next = LoadoutRules.CopyInto(p.Loadouts[from], p.Loadouts[to]);
        if (Invalid(next, p) is { } err)
        {
            Error(c, err);
            return;
        }
        p.Loadouts[to] = next;
        p.Camos[to] = p.Camos[from];
        if (p.Persist)
        {
            _ = _store!.Loadouts.SaveAsync(p.DbId, next);
            _ = _store.Camo.SaveAsync(p.DbId, to, p.Camos[to]);
        }
        Send(c);
        if (to == p.Active) BroadcastAppearance(c);
    }

    /// <summary>
    /// Restores one slot's loadout to the starter kit and its default name (ticket #145). The slot's camouflage is kept:
    /// the reset covers the loadout items, not the look.
    /// </summary>
    void ResetLoadout(ServerClient c, byte[] payload)
    {
        var p = c.Profile!;
        if (LoadoutEvents.DecodeReset(payload) is not { } slot) { Error(c, "malformed reset request"); return; }
        if (LoadoutRules.ValidateSlot(slot) is { } err) { Error(c, err); return; }
        var next = StarterLoadout(slot); // the showcase kit in dev mode (#149)
        p.Loadouts[slot] = next;
        if (p.Persist) _ = _store!.Loadouts.SaveAsync(p.DbId, next);
        Send(c);
    }

    void SelectSlot(ServerClient c, int slot)
    {
        var p = c.Profile!;
        if (LoadoutRules.ValidateSlot(slot) is { } err)
        {
            Error(c, err);
            return;
        }
        if (p.Active == slot) { Send(c); return; } // unchanged: answer the client, but no write and no broadcast
        p.Active = slot;
        if (p.Persist) _ = _store!.Loadouts.SetActiveSlotAsync(p.DbId, slot);
        Send(c);
        BroadcastAppearance(c); // the look other players see is the newly active loadout's (#141)
    }

    /// <summary>Sends each joined player's look to the joiner, and the joiner's look to every other player.</summary>
    public void SendAppearancesOnJoin(ServerClient c)
    {
        foreach (var o in _ctx.Players.Values)
        {
            _ctx.Send(c, AppearanceEvent(o));
            if (o != c) _ctx.Send(o, AppearanceEvent(c));
        }
    }

    /// <summary>Saves one loadout slot's look (#141). Only the active slot is broadcast: it is the look others see.</summary>
    void SaveCamo(ServerClient c, int slot, CamoSpec spec)
    {
        var p = c.Profile!;
        if (LoadoutRules.ValidateSlot(slot) is { } slotErr)
        {
            Error(c, slotErr);
            return;
        }
        if (spec.Validate(_camo) is { } err)
        {
            Error(c, err);
            return;
        }
        p.Camos[slot] = spec;
        if (p.Persist) AfterWrite(_store!.Camo.SaveAsync(p.DbId, slot, spec), ok => { if (!ok) Error(c, "Could not save your camouflage, try again in a moment."); }, c);
        Send(c);
        if (slot == p.Active) BroadcastAppearance(c);
    }

    void BroadcastAppearance(ServerClient c)
    {
        var ev = AppearanceEvent(c);
        foreach (var o in _ctx.Players.Values) _ctx.Send(o, ev);
    }

    static EventMsg AppearanceEvent(ServerClient c) => new((byte)EventKind.Appearance, AppearanceCodec.Encode(c.Id, c.Profile!.Camo));
}
