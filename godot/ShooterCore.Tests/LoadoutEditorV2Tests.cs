using Microsoft.Data.Sqlite;
using Drycastle.ShooterCore;
using Drycastle.ShooterServer.Persistence;
using Drycastle.ShooterServer.Persistence.InMemory;
using Drycastle.ShooterServer.Persistence.Sqlite;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>Ticket #145: loadout autosave timing, names (validation, copy, reset, storage), and the loadout wire codecs.</summary>
public class LoadoutEditorV2Tests
{
    static readonly LevelCurve Curve = GameData.Load(Path.Combine(AppContext.BaseDirectory, "data")).Levels.Curve;

    static Loadout L(int slot, string primary = "ak47", string name = "") => new(slot, primary, "pistol", "", "", "", "", name);

    static LoadoutAutosave Seeded(string primary = "ak47")
    {
        var a = new LoadoutAutosave();
        a.Seed(L(0, primary));
        return a;
    }

    // ---- autosave: debounce, coalescing, in flight, answers, retries, flush, undo ----

    [Fact]
    public void AnEditIsSentOnlyAfterTheDebounce()
    {
        var a = Seeded();
        a.Edit(L(0, "m4a1"), 0);
        Assert.Empty(a.Poll(LoadoutAutosave.DebounceMs - 1));
        var sent = Assert.Single(a.Poll(LoadoutAutosave.DebounceMs));
        Assert.Equal(0, sent.Slot);
        Assert.Equal("m4a1", sent.Value.PrimaryWeapon);
        Assert.Equal(AutosaveState.Saving, a.StateOf(0));
    }

    [Fact]
    public void RapidEditsCoalesceIntoOneSaveOfTheNewestValue()
    {
        var a = Seeded();
        a.Edit(L(0, "m4a1"), 0);
        a.Edit(L(0, "g36"), 100);
        a.Edit(L(0, "aug"), 200);
        Assert.Empty(a.Poll(499)); // the quiet period runs from the last edit
        var sent = Assert.Single(a.Poll(500));
        Assert.Equal("aug", sent.Value.PrimaryWeapon);
    }

    [Fact]
    public void AnAcceptedSaveMakesTheServerCopyTheDraft()
    {
        var a = Seeded();
        a.Edit(L(0, "m4a1"), 0);
        var sent = Assert.Single(a.Poll(LoadoutAutosave.DebounceMs));
        a.Acknowledge(new LoadoutAckInfo(0, sent.Seq, true, ""));
        Assert.False(a.HasUnconfirmed(0));
        Assert.Equal(AutosaveState.Saved, a.StateOf(0));
        Assert.Equal("m4a1", a.Effective(0)!.PrimaryWeapon);
    }

    [Fact]
    public void AnEditMadeWhileSavingWaitsForItsOwnDebounceAfterTheAnswer()
    {
        var a = Seeded();
        a.Edit(L(0, "m4a1"), 0);
        var first = Assert.Single(a.Poll(300));
        a.Edit(L(0, "g36"), 400);
        Assert.Empty(a.Poll(450)); // one save per slot at a time
        a.Acknowledge(new LoadoutAckInfo(0, first.Seq, true, ""));
        Assert.Equal(AutosaveState.Pending, a.StateOf(0));
        Assert.Empty(a.Poll(699));
        var second = Assert.Single(a.Poll(700));
        Assert.Equal("g36", second.Value.PrimaryWeapon);
        Assert.NotEqual(first.Seq, second.Seq);
    }

    [Fact]
    public void ARejectionIsShownKeepsTheDraftAndIsNotResent()
    {
        var a = Seeded();
        a.Edit(L(0, "m4a1"), 0);
        var sent = Assert.Single(a.Poll(300));
        a.Acknowledge(new LoadoutAckInfo(0, sent.Seq, false, "m4a1 unlocks at level 2"));
        Assert.Equal(AutosaveState.Rejected, a.StateOf(0));
        Assert.Equal("m4a1 unlocks at level 2", a.RejectReason(0));
        Assert.Equal("m4a1", a.Effective(0)!.PrimaryWeapon);
        Assert.True(a.HasUnconfirmed(0));
        Assert.Empty(a.Poll(60_000)); // a validation answer would repeat, so no retry
    }

    [Fact]
    public void AnAnswerForAnOlderSaveIsIgnored()
    {
        var a = Seeded();
        a.Edit(L(0, "m4a1"), 0);
        var sent = Assert.Single(a.Poll(300));
        a.Acknowledge(new LoadoutAckInfo(0, sent.Seq + 7, true, ""));
        Assert.Equal(AutosaveState.Saving, a.StateOf(0));
        Assert.True(a.HasUnconfirmed(0));
    }

    [Fact]
    public void ASaveWithNoAnswerRetriesWithBackoffUnderANewSeq()
    {
        var a = Seeded();
        a.Edit(L(0, "m4a1"), 0);
        var first = Assert.Single(a.Poll(300));
        Assert.Empty(a.Poll(300 + LoadoutAutosave.AckTimeoutMs - 1));
        Assert.Empty(a.Poll(300 + LoadoutAutosave.AckTimeoutMs)); // timed out: the retry waits one second
        Assert.Equal(AutosaveState.Retrying, a.StateOf(0));
        Assert.Empty(a.Poll(6299));
        var again = Assert.Single(a.Poll(6300));
        Assert.Equal("m4a1", again.Value.PrimaryWeapon);
        Assert.NotEqual(first.Seq, again.Seq);
    }

    [Fact]
    public void AFailedSendGoesBackIntoTheQueue()
    {
        var a = Seeded();
        a.Edit(L(0, "m4a1"), 0);
        Assert.Single(a.Poll(300));
        a.Abandon(0, 300);
        Assert.Equal(AutosaveState.Retrying, a.StateOf(0));
        Assert.Empty(a.Poll(1299));
        Assert.Single(a.Poll(1300));
    }

    [Theory]
    [InlineData(1, 1000)]
    [InlineData(2, 2000)]
    [InlineData(3, 4000)]
    [InlineData(4, 8000)]
    [InlineData(9, 8000)]
    public void BackoffDoublesAndCapsAtEightSeconds(int attempt, long ms) => Assert.Equal(ms, LoadoutAutosave.Backoff(attempt));

    [Fact]
    public void FlushSendsUnsentEditsAtOnce()
    {
        var a = Seeded();
        a.Edit(L(0, "m4a1"), 0);
        Assert.Empty(a.Poll(10));
        a.Flush(10);
        Assert.Single(a.Poll(10));
    }

    [Fact]
    public void UndoStepsBackAndTheStepSaves()
    {
        var a = Seeded("ak47");
        a.Edit(L(0, "m4a1"), 0);
        a.Edit(L(0, "g36"), 1000);
        Assert.True(a.CanUndo(0));
        Assert.True(a.Undo(0, 2000));
        Assert.Equal("m4a1", a.Effective(0)!.PrimaryWeapon);
        var sent = Assert.Single(a.Poll(2000)); // an undo is an edit: it saves without the debounce
        Assert.Equal("m4a1", sent.Value.PrimaryWeapon);
        Assert.True(a.Undo(0, 2000));
        Assert.Equal("ak47", a.Effective(0)!.PrimaryWeapon);
        Assert.False(a.CanUndo(0));
        Assert.False(a.Undo(0, 2000));
    }

    [Fact]
    public void UndoWithNoEditsDoesNothing() => Assert.False(new LoadoutAutosave().Undo(0, 0));

    [Fact]
    public void EditingToTheSameValueChangesNothing()
    {
        var a = Seeded("ak47");
        a.Edit(L(0, "ak47"), 0);
        Assert.Empty(a.Poll(10_000));
        Assert.False(a.HasUnconfirmed(0));
        Assert.False(a.CanUndo(0));
    }

    [Fact]
    public void AServerCopyDoesNotOverwriteAnUnconfirmedDraft()
    {
        var a = Seeded();
        a.Edit(L(0, "m4a1"), 0);
        a.Seed(L(0, "ak47"));
        Assert.Equal("m4a1", a.Effective(0)!.PrimaryWeapon);
    }

    [Fact]
    public void SlotsSaveIndependently()
    {
        var a = new LoadoutAutosave();
        a.Seed(L(0));
        a.Seed(L(2));
        a.Edit(L(0, "m4a1"), 0);
        a.Edit(L(2, "g36"), 0);
        var sent = a.Poll(300);
        Assert.Equal(new[] { 0, 2 }, sent.Select(s => s.Slot));
    }

    // ---- names ----

    [Theory]
    [InlineData("  Assault  ", "Assault")]
    [InlineData("Class 1", "Class 1")]
    public void NamesAreTrimmed(string raw, string want)
    {
        Assert.Equal(want, LoadoutRules.ValidateName(raw, out var err));
        Assert.Null(err);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("12345678901234567890123456")] // 26 characters
    [InlineData("bad\tname")]
    public void BadNamesAreRefusedWithAReason(string raw)
    {
        Assert.Null(LoadoutRules.ValidateName(raw, out var err));
        Assert.NotNull(err);
    }

    [Fact]
    public void A24CharacterNameIsTheLongestAllowed()
    {
        var name = new string('x', LoadoutRules.MaxNameChars);
        Assert.Equal(name, LoadoutRules.ValidateName(name, out _));
        Assert.Null(LoadoutRules.ValidateName(name + "x", out _));
    }

    [Fact]
    public void DefaultNamesAreClassOneToFive() =>
        Assert.Equal(new[] { "Class 1", "Class 2", "Class 3", "Class 4", "Class 5" }, Enumerable.Range(0, Loadout.SlotCount).Select(LoadoutRules.DefaultName));

    [Fact]
    public void TheStarterKitCarriesTheDefaultName()
    {
        var data = GameData.Load(Path.Combine(AppContext.BaseDirectory, "data"));
        var kit = LoadoutRules.DefaultLoadout(2, data.Weapons, data.Perks);
        Assert.Equal("Class 3", kit.Name);
        Assert.NotEqual("", kit.PrimaryWeapon);
        Assert.Null(LoadoutRules.ValidateLoadout(kit, data.Weapons, 1, Array.Empty<Unlock>(), data.Perks.Perks.ToDictionary(p => p.Id, p => p.UnlockLevel), data.Killstreaks.UnlockLevels));
    }

    [Fact]
    public void CopyKeepsTheDestinationSlotAndName()
    {
        var src = new Loadout(1, "m4a1", "pistol", "fleet_foot", "uav", "", "", "Raid");
        var dst = new Loadout(3, "ak47", "glock17", "", "", "", "", "Class 4");
        var copy = LoadoutRules.CopyInto(src, dst);
        Assert.Equal(3, copy.Slot);
        Assert.Equal("Class 4", copy.Name);
        Assert.Equal("m4a1", copy.PrimaryWeapon);
        Assert.Equal("uav", copy.Streak1);
    }

    [Theory]
    [InlineData(ProtocolCase.Memory)]
    [InlineData(ProtocolCase.Sqlite)]
    public async Task LoadoutNamesAreStoredAndReadBack(ProtocolCase backend)
    {
        using var store = backend == ProtocolCase.Memory
            ? new InMemoryProgressionStore(Curve) as IProgressionStore
            : SqliteProgressionStore.Open(Path.Combine(Path.GetTempPath(), $"shooter-test-{Guid.NewGuid():N}", "shooter.db"), _ => { }, Curve);
        var p = await store.Profiles.GetOrCreateAsync("name-token");
        await store.Loadouts.SaveAsync(p.Id, new Loadout(2, "ak47", "pistol", "", "", "", "", "Raid night"));
        var all = await store.Loadouts.GetAllAsync(p.Id);
        Assert.Equal("Raid night", all[2].Name);
    }

    [Fact]
    public void TheNameMigrationNamesExistingSlotsByNumber()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        Run(conn, "CREATE TABLE loadouts (player_id INTEGER NOT NULL, slot INTEGER NOT NULL, primary_weapon TEXT NOT NULL DEFAULT '', PRIMARY KEY (player_id, slot));");
        Run(conn, "INSERT INTO loadouts (player_id, slot, primary_weapon) VALUES (1, 0, 'ak47'), (1, 1, ''), (1, 2, 'm4a1');");
        using var stream = typeof(SqliteProgressionStore).Assembly.GetManifestResourceStream("migrations/0005_loadout_name.sql");
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        Run(conn, reader.ReadToEnd());

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM loadouts ORDER BY slot;";
        using var r = cmd.ExecuteReader();
        var names = new List<string>();
        while (r.Read()) names.Add(r.GetString(0));
        Assert.Equal(new[] { "Class 1", "Class 2", "Class 3" }, names);
    }

    static void Run(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // ---- wire ----

    [Fact]
    public void SaveLoadoutCarriesTheNameAndSeq()
    {
        var msg = new SaveLoadoutMsg(new Loadout(4, "ak47", "pistol", "", "", "", "", "Night"), 77);
        Assert.Equal(msg, Wire.DecodeReliable(Wire.Encode(msg)));
    }

    [Fact]
    public void TruncatedSaveLoadoutIsRejectedAtEveryCut()
    {
        var full = Wire.Encode(new SaveLoadoutMsg(L(1, "ak47", "Night"), 5));
        for (var cut = 1; cut < full.Length; cut++)
            Assert.Null(Wire.DecodeReliable(full.AsSpan(0, cut)));
    }

    [Fact]
    public void LoadoutAckRoundTrips()
    {
        var ack = new LoadoutAckInfo(2, 9, false, "unknown perk \"x\"");
        Assert.Equal(ack, LoadoutEvents.DecodeAck(LoadoutEvents.EncodeAck(ack)));
        var ok = new LoadoutAckInfo(0, uint.MaxValue, true, "");
        Assert.Equal(ok, LoadoutEvents.DecodeAck(LoadoutEvents.EncodeAck(ok)));
        Assert.Null(LoadoutEvents.DecodeAck(new byte[] { 1, 2 }));
    }

    [Fact]
    public void CopyAndResetPayloadsRoundTrip()
    {
        Assert.Equal((1, 4), LoadoutEvents.DecodeCopy(LoadoutEvents.EncodeCopy(1, 4)));
        Assert.Equal(3, LoadoutEvents.DecodeReset(LoadoutEvents.EncodeReset(3)));
        Assert.Null(LoadoutEvents.DecodeCopy(new byte[] { 1 }));
        Assert.Null(LoadoutEvents.DecodeReset(Array.Empty<byte>()));
    }

    [Fact]
    public void LoadoutEventKindsSitInTheReservedRange()
    {
        Assert.Equal(130, (int)EventKind.CopyLoadout);
        Assert.Equal(131, (int)EventKind.ResetLoadout);
        Assert.Equal(132, (int)EventKind.LoadoutAck);
    }

    public enum ProtocolCase { Memory, Sqlite }
}
