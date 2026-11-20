using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>The Hunter's Log rules (#336): the species table, records, first-times, the Life List and the per-viewer redaction.</summary>
public class HuntBookTests
{
    static readonly DateTime T0 = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
    static readonly IReadOnlySet<string> Nobody = new HashSet<string>();
    static IReadOnlySet<string> Caught(params string[] ids) => new HashSet<string>(ids);

    static HuntRecord Hunt(long player, string animal, float kg, float? distance, int minute, string weapon = "sniper", HitDiagram? d = null) =>
        HuntRecord.ForAnimal(player, animal, "", kg, distance, weapon, d ?? new HitDiagram(100, 120, true, false, 3, 7), 1, 2, 3, T0.AddMinutes(minute))!;

    static HuntRecord Catch(long player, string mystery, int minute) => HuntRecord.ForMystery(player, mystery, 1, 2, 3, T0.AddMinutes(minute));

    [Fact]
    public void DeerAndDoeAreOneIndexSpeciesAndTheRestKeepTheirId()
    {
        Assert.Equal("deer", IndexSpecies.Of("deer"));
        Assert.Equal("deer", IndexSpecies.Of("doe"));
        Assert.Equal("stag", IndexSpecies.Of("stag"));
        Assert.Equal("moose", IndexSpecies.Of("moose"));
        Assert.Null(IndexSpecies.Of("dog"));
        Assert.Null(IndexSpecies.Of("bigfoot"));
        Assert.Equal(10, IndexSpecies.All.Count);
    }

    [Fact]
    public void EveryWeighedSpeciesOfAnimalsJsonHasAnIndexSpeciesAndEveryIndexSpeciesIsInTheFile()
    {
        var animals = GameData.Load(Path.Combine(AppContext.BaseDirectory, "data")).Animals;
        foreach (var a in animals.Animals.Where(a => a.WeightKg != null))
            Assert.True(IndexSpecies.Of(a.Id) != null, $"{a.Id} has a weight but no index species");
        foreach (var id in IndexSpecies.All)
            Assert.True(animals.Animals.Any(a => a.Id == id || id == "deer" && a.Id == "doe"), $"index species {id} is not in animals.json");
    }

    [Fact]
    public void AnAnimalWithoutWeightOrOutsideTheTableIsNoHunt()
    {
        Assert.Null(HuntRecord.ForAnimal(1, "dog", "", 20, 5, "ak47", null, 0, 0, 0, T0));
        Assert.Null(HuntRecord.ForAnimal(1, "deer", "", 0, 5, "ak47", null, 0, 0, 0, T0));
    }

    [Fact]
    public void ARowKeepsTheExactSpeciesTheVariantAndTenthsOfAKgAndMetre()
    {
        var h = HuntRecord.ForAnimal(1, "doe", "", 61.46f, 123.449f, "ak47", null, 0, 0, 0, T0)!;
        Assert.Equal(("deer", "doe", "", 61.5f, 123.4f), (h.Species, h.Exact, h.Variant, h.Kg, h.DistanceM));
        Assert.Equal("moose_male_01", HuntRecord.ForAnimal(1, "moose", "moose_male_01", 500, 10, "ak47", null, 0, 0, 0, T0)!.Variant);
    }

    [Fact]
    public void ARunOverHasNoDistanceNoDiagramAndNoLongestShot()
    {
        var book = new HuntBook();
        var run = HuntRecord.ForAnimal(1, "boar", "", 90, 4, HuntWeapons.RunOver, new HitDiagram(1, 2, true, false, 3, 4), 0, 0, 0, T0)!;
        Assert.Null(run.DistanceM);
        Assert.Null(run.Diagram);
        Assert.False(run.CountsForLongest);
        var o = book.Add(run);
        Assert.True(o.FirstTime);
        Assert.True(o.Heaviest);
        Assert.False(o.Longest);
        Assert.Null(book.Records(Nobody).Single(r => r.Species == "boar").Longest);
        Assert.Equal(90f, book.Records(Nobody).Single(r => r.Species == "boar").Heaviest!.Kg);
    }

    [Fact]
    public void AFirstKillThatIsAlsoARecordIsOneOutcomeAndAnOrdinaryHuntIsNone()
    {
        var book = new HuntBook();
        var first = book.Add(Hunt(1, "wolf", 40, 100, 0));
        Assert.Equal(new HuntOutcome(true, true, true), first);
        Assert.True(first.Announce);

        var other = book.Add(Hunt(2, "wolf", 30, 50, 1)); // a first for player 2, no record
        Assert.Equal(new HuntOutcome(true, false, false), other);

        var ordinary = book.Add(Hunt(1, "wolf", 35, 60, 2)); // neither first nor record
        Assert.Equal(default, ordinary);
        Assert.False(ordinary.Announce);

        var heavier = book.Add(Hunt(2, "wolf", 41, 10, 3)); // a heaviest, not a first
        Assert.Equal(new HuntOutcome(false, true, false), heavier);
        var farther = book.Add(Hunt(2, "wolf", 5, 101, 4));
        Assert.Equal(new HuntOutcome(false, false, true), farther);
    }

    [Fact]
    public void ATieKeepsTheEarlierHunt()
    {
        var book = new HuntBook();
        book.Add(Hunt(1, "bear", 200, 80, 0));
        var tie = book.Add(Hunt(2, "bear", 200, 80, 5));
        Assert.False(tie.Heaviest);
        Assert.False(tie.Longest);
        var card = book.Records(Nobody).Single(r => r.Species == "bear");
        Assert.Equal(1, card.Heaviest!.PlayerId);
        Assert.Equal(1, card.Longest!.PlayerId);
        Assert.Equal(1, card.First!.PlayerId);
    }

    [Fact]
    public void ThePlayersOwnBestsAlsoKeepTheEarlierOnAndCountTheirSpecies()
    {
        var book = new HuntBook();
        book.Add(Hunt(1, "deer", 80, 50, 0));
        book.Add(Hunt(1, "doe", 80, 50, 1)); // merged into Deer; a tie
        book.Add(Hunt(1, "stag", 200, 30, 2));
        book.Add(Hunt(1, "fox", 5, 10, 3, HuntWeapons.RunOver));
        book.SetName(1, "Ann");
        var log = book.PlayerLog(1, Nobody)!;
        Assert.Equal(("Ann", 4, 3), (log.Name, log.Hunts, log.Species));
        Assert.Equal("stag", log.Heaviest!.Species);
        Assert.Equal(50f, log.Longest!.DistanceM);
        Assert.Equal("deer", log.Longest.Species);
        var deer = log.Life.Single(e => e.Species == "deer");
        Assert.Equal((true, 80f, 50f), (deer.Caught, deer.BestKg, deer.LongestM));
        Assert.Equal(DateTimeOffset.Parse("2026-11-01T12:00:00Z").ToUnixTimeSeconds(), deer.CaughtAtUnix);
        var fox = log.Life.Single(e => e.Species == "fox");
        Assert.Equal((true, 5f, (float?)null), (fox.Caught, fox.BestKg, fox.LongestM));
        Assert.False(log.Life.Single(e => e.Species == "moose").Caught);
    }

    [Fact]
    public void TheLifeListHasATileForEveryIndexSpeciesAndEveryMysteryInOrder()
    {
        var book = new HuntBook();
        book.SetName(1, "Ann");
        var life = book.PlayerLog(1, Nobody)!.Life;
        Assert.Equal(13, life.Count);
        Assert.Equal(IndexSpecies.All, life.Take(10).Select(e => e.Species));
        Assert.All(life, e => Assert.False(e.Caught));
        Assert.Null(book.PlayerLog(99, Nobody)); // never heard of
    }

    [Fact]
    public void AMysteryHoldsItsFirstCatchAndItsLongestShotAndHasNoWeight()
    {
        var book = new HuntBook();
        var a = book.Add(Catch(1, Mysteries.Bigfoot, 0));
        Assert.Equal(new HuntOutcome(true, false, false), a); // a first, no distance so no longest
        var b = book.Add(Catch(2, Mysteries.Bigfoot, 1));
        Assert.Equal(new HuntOutcome(true, false, false), b);
        var card = book.Records(Caught(Mysteries.Bigfoot)).Single(r => r.Species == Mysteries.Bigfoot);
        Assert.Null(card.Heaviest);
        Assert.Null(card.Longest);
        Assert.Equal(1, card.First!.PlayerId);
        Assert.True(card.Caught);
        Assert.Equal(HuntWeapons.Event, card.First.Weapon);
        Assert.Null(card.First.Kg);
    }

    [Fact]
    public void ARecordReplayEqualsTheLiveBook()
    {
        var hunts = new[] { Hunt(1, "deer", 70, 40, 0), Hunt(2, "stag", 150, 90, 1), Hunt(1, "stag", 160, 20, 2), Catch(2, Mysteries.Bigfoot, 3), Hunt(2, "deer", 71, 41, 4) };
        var live = new HuntBook();
        foreach (var h in hunts) live.Add(h);
        var replay = HuntBook.From(hunts.Select(h => new HuntRow(h, "p" + h.PlayerId)));
        var all = Caught(Mysteries.Bigfoot);
        Assert.Equal(live.Records(all).Select(r => (r.Species, r.Heaviest?.Kg, r.Longest?.DistanceM, r.First?.PlayerId)),
            replay.Records(all).Select(r => (r.Species, r.Heaviest?.Kg, r.Longest?.DistanceM, r.First?.PlayerId)));
        Assert.Equal(all, replay.CaughtBy(2));
        Assert.Empty(replay.CaughtBy(1));
    }

    // ---- redaction ----

    [Fact]
    public void AViewerWhoHasNotCaughtAMysteryIsSentAHiddenIdAndNoNameShapeOrDiagramButHunterTimeDistanceAndWeaponStay()
    {
        var h = new HuntRecord(0, 7, Mysteries.Bigfoot, Mysteries.Bigfoot, "", null, 12.5f, "sniper", new HitDiagram(1, 2, true, true, 3, 4), 0, 0, 0, T0);
        var hidden = HuntRedaction.View(h, "Ann", Nobody);
        Assert.True(hidden.Hidden);
        Assert.Equal(Mysteries.HiddenId(Mysteries.Bigfoot), hidden.Species);
        Assert.DoesNotContain("bigfoot", hidden.Species);
        Assert.Equal(("", "", (HitDiagram?)null), (hidden.Exact, hidden.Variant, hidden.Diagram));
        Assert.Equal(("Ann", 12.5f, "sniper", 7L), (hidden.Hunter, hidden.DistanceM, hidden.Weapon, hidden.PlayerId));
        Assert.Equal(DateTimeOffset.Parse("2026-11-01T12:00:00Z").ToUnixTimeSeconds(), hidden.AtUnix);

        var shown = HuntRedaction.View(h, "Ann", Caught(Mysteries.Bigfoot));
        Assert.False(shown.Hidden);
        Assert.Equal((Mysteries.Bigfoot, Mysteries.Bigfoot), (shown.Species, shown.Exact));
        Assert.NotNull(shown.Diagram);
        Assert.False(HuntRedaction.View(Hunt(1, "deer", 60, 5, 0), "x", Nobody).Hidden); // an animal is never hidden
    }

    [Fact]
    public void HiddenIdsAreStablePerCreatureAndDistinct()
    {
        var ids = Mysteries.All.Select(m => Mysteries.HiddenId(m.Id)).ToList();
        Assert.Equal(3, ids.Distinct().Count());
        Assert.All(ids, i => Assert.True(Mysteries.IsHiddenId(i)));
        Assert.False(Mysteries.IsHiddenId("bigfoot"));
    }

    [Fact]
    public void TheFourViewerAndOwnerCasesOfALifeTile()
    {
        var book = new HuntBook();
        book.SetName(1, "Owner");
        book.SetName(2, "Viewer");
        book.Add(Catch(1, Mysteries.Bigfoot, 0)); // owner caught it

        // viewer not caught, owner caught: hidden, drawn filled
        var a = book.PlayerLog(1, book.CaughtBy(2))!.Life.Single(e => e.Species == Mysteries.HiddenId(Mysteries.Bigfoot));
        Assert.Equal((true, true), (a.Hidden, a.Caught));
        Assert.Equal((null, null, 0L), (a.BestKg, a.LongestM, a.CaughtAtUnix));

        // neither caught: hidden, outline
        var c = book.PlayerLog(2, Nobody)!.Life.Single(e => e.Species == Mysteries.HiddenId(Mysteries.Bigfoot));
        Assert.Equal((true, false), (c.Hidden, c.Caught));

        book.Add(Catch(2, Mysteries.Bigfoot, 1)); // the viewer's own first catch
        // both caught: full
        var both = book.PlayerLog(1, book.CaughtBy(2))!.Life.Single(e => e.Species == Mysteries.Bigfoot);
        Assert.Equal((false, true), (both.Hidden, both.Caught));
        Assert.True(both.CaughtAtUnix > 0);
        // viewer caught, owner not: real name, outline
        book.SetName(3, "Third");
        var d = book.PlayerLog(3, book.CaughtBy(2))!.Life.Single(e => e.Species == Mysteries.Bigfoot);
        Assert.Equal((false, false), (d.Hidden, d.Caught));
    }

    [Fact]
    public void AFirstCatchUnlocksTheOldRowsTheRecordsAndOtherTilesAtOnce()
    {
        var book = new HuntBook();
        book.SetName(1, "Owner");
        book.Add(Catch(1, Mysteries.Bigfoot, 0));
        var before = book.Records(book.CaughtBy(2)).Single(r => r.Species == Mysteries.HiddenId(Mysteries.Bigfoot));
        Assert.True(before.Hidden);
        Assert.True(before.Caught);
        Assert.True(before.First!.Hidden);
        Assert.Equal("Owner", before.First.Hunter);

        book.Add(Catch(2, Mysteries.Bigfoot, 5));
        var after = book.Records(book.CaughtBy(2)).Single(r => r.Species == Mysteries.Bigfoot);
        Assert.False(after.Hidden);
        Assert.Equal(Mysteries.Bigfoot, after.First!.Species);
        Assert.Equal("Owner", after.First.Hunter); // still the first catcher's
    }

    [Fact]
    public void TheTableFilterForAHiddenMysteryIsTheOneMysteryHuntsOption()
    {
        Assert.Null(HuntRedaction.FilterFor("", Nobody));
        Assert.Equal(new[] { "deer" }, HuntRedaction.FilterFor("deer", Nobody));
        Assert.Equal(new[] { "bigfoot", "unicorn", "scylla" }, HuntRedaction.FilterFor(Mysteries.AllHidden, Nobody));
        Assert.Equal(new[] { "bigfoot", "unicorn", "scylla" }, HuntRedaction.FilterFor("bigfoot", Nobody)); // asking by name reveals nothing
        Assert.Equal(new[] { "bigfoot" }, HuntRedaction.FilterFor("bigfoot", Caught("bigfoot")));
        Assert.Equal(new[] { "unicorn", "scylla" }, HuntRedaction.FilterFor(Mysteries.AllHidden, Caught("bigfoot")));
        Assert.Empty(HuntRedaction.FilterFor("nonsense", Nobody)!);
    }

    [Fact]
    public void HiddenRecordsKeepHolderDistanceAndWeapon()
    {
        var book = new HuntBook();
        book.SetName(1, "Ann");
        var far = new HuntRecord(0, 1, Mysteries.Scylla, Mysteries.Scylla, "", null, 300f, "sniper", null, 0, 0, 0, T0);
        var o = book.Add(far);
        Assert.Equal(new HuntOutcome(true, false, true), o); // a mystery can hold a longest shot
        var card = book.Records(Nobody).Single(r => r.Species == Mysteries.HiddenId(Mysteries.Scylla));
        Assert.Equal(("Ann", 300f, "sniper", true), (card.Longest!.Hunter, card.Longest.DistanceM, card.Longest.Weapon, card.Longest.Hidden));
    }

    [Fact]
    public void ARecordPlacesTheMysteriesAfterTheIndexSpeciesAndNamesNobodyWhoWasNeverHunted()
    {
        var recs = new HuntBook().Records(Nobody);
        Assert.Equal(13, recs.Count);
        Assert.Equal(IndexSpecies.All, recs.Take(10).Select(r => r.Species));
        Assert.All(recs, r => Assert.False(r.Caught));
        Assert.All(recs, r => Assert.Null(r.Heaviest));
    }

    [Fact]
    public void HuntQueryRulesFilterSortAndPage()
    {
        var rows = new[]
        {
            new HuntRow(Hunt(1, "deer", 80, 10, 0) with { Id = 1 }, "Bea"),
            new HuntRow(Hunt(2, "stag", 200, 50, 1) with { Id = 2 }, "Ann"),
            new HuntRow(Hunt(1, "deer", 90, null, 2, HuntWeapons.RunOver) with { Id = 3 }, "Bea"),
            new HuntRow(Catch(2, Mysteries.Bigfoot, 3) with { Id = 4 }, "Ann"),
        };
        HuntQuery Q(HuntSort sort, bool desc, IReadOnlyList<string>? species = null, long player = 0, string weapon = "") =>
            new(0, species, weapon, player, sort, desc, new[] { Mysteries.Bigfoot });
        IEnumerable<long> Ids(HuntQuery q) => HuntQueryRules.Apply(rows, q).Rows.Select(r => r.Hunt.Id);

        Assert.Equal(new long[] { 4, 3, 2, 1 }, Ids(Q(HuntSort.Time, true)));
        Assert.Equal(new long[] { 1, 2, 3, 4 }, Ids(Q(HuntSort.Time, false)));
        Assert.Equal(new long[] { 3, 1 }, Ids(Q(HuntSort.Time, true, new[] { "deer" })));
        Assert.Equal(new long[] { 3 }, Ids(Q(HuntSort.Time, true, weapon: HuntWeapons.RunOver)));
        Assert.Equal(new long[] { 4, 2 }, Ids(Q(HuntSort.Time, true, player: 2)));
        Assert.Equal(new long[] { 3, 1, 2, 4 }, Ids(Q(HuntSort.Species, false))); // hidden last
        Assert.Equal(new long[] { 4, 2, 3, 1 }, Ids(Q(HuntSort.Hunter, false))); // ties: the newer hunt first
        Assert.Equal(new long[] { 2, 3, 1, 4 }, Ids(Q(HuntSort.Kg, true))); // the null weight last in either direction
        Assert.Equal(new long[] { 1, 3, 2, 4 }, Ids(Q(HuntSort.Kg, false)));
        Assert.Equal(new long[] { 2, 1, 4, 3 }, Ids(Q(HuntSort.Distance, true)));
        Assert.Equal(4, HuntQueryRules.Apply(rows, Q(HuntSort.Time, true)).Total);
        Assert.Empty(HuntQueryRules.Apply(rows, Q(HuntSort.Time, true, Array.Empty<string>())).Rows);
        Assert.Empty(HuntQueryRules.Apply(rows, new HuntQuery(1, null, "", 0, HuntSort.Time, true, Array.Empty<string>())).Rows); // page 1 of 4 rows
    }

    // ---- the Unicorn: a mystery that is a wild animal ----

    static HuntRecord Unicorn(long player, float? distance, int minute, string weapon = "sniper", HitDiagram? d = null) =>
        HuntRecord.ForUnicorn(player, distance, weapon, d ?? new HitDiagram(100, 120, true, true, 3, 7), 1, 2, 3, T0.AddMinutes(minute));

    [Fact]
    public void AUnicornShotIsAMysteryRowWithADistanceADiagramAndNoWeight()
    {
        var d = new HitDiagram(10, 20, false, true, 2, 5);
        var h = Unicorn(7, 123.449f, 0, "sniper", d);
        Assert.True(h.IsMystery);
        Assert.Equal((Mysteries.Unicorn, Mysteries.Unicorn, "", (float?)null, (float?)123.4f, "sniper"), (h.Species, h.Exact, h.Variant, h.Kg, h.DistanceM, h.Weapon));
        Assert.Equal(d, h.Diagram);
        Assert.Equal((1f, 2f, 3f), (h.X, h.Y, h.Z));
        Assert.True(h.CountsForLongest);
        Assert.True(Mysteries.Find(Mysteries.Unicorn)!.Live);
    }

    [Fact]
    public void AUnicornRunOverHasNoDistanceNoDiagramAndNoLongestShot()
    {
        var run = Unicorn(7, 4, 0, HuntWeapons.RunOver);
        Assert.Equal(((float?)null, (HitDiagram?)null, false), (run.DistanceM, run.Diagram, run.CountsForLongest));
        Assert.Null(HuntRecord.ForUnicorn(7, null, "ak47", null, 0, 0, 0, T0).DistanceM);
    }

    [Fact]
    public void TheLongestUnicornShotIsARecordAndATieKeepsTheEarlierOne()
    {
        var book = new HuntBook();
        book.SetName(1, "Ann"); book.SetName(2, "Bea");
        Assert.Equal(new HuntOutcome(true, false, true), book.Add(Unicorn(1, 100, 0)));
        Assert.Equal(new HuntOutcome(true, false, false), book.Add(Unicorn(2, 100, 1))); // a tie: not beaten
        Assert.Equal(new HuntOutcome(false, false, false), book.Add(Unicorn(1, 50, 2)));
        Assert.Equal(new HuntOutcome(false, false, false), book.Add(Unicorn(1, null, 3, HuntWeapons.RunOver))); // a run over holds none
        Assert.Equal(new HuntOutcome(false, false, true), book.Add(Unicorn(1, 180.5f, 4)));
        var card = book.Records(Caught(Mysteries.Unicorn)).Single(r => r.Species == Mysteries.Unicorn);
        Assert.Equal(("Ann", 180.5f), (card.Longest!.Hunter, card.Longest.DistanceM));
        Assert.Equal(("Ann", 100f), (card.First!.Hunter, card.First.DistanceM)); // the first catch stays
        Assert.NotNull(card.Longest.Diagram);
        Assert.Null(card.Heaviest); // no weight
    }

    [Fact]
    public void AFirstUnicornIsAnnouncedAndAnOrdinaryOneIsNot()
    {
        var book = new HuntBook();
        var first = book.Add(Unicorn(1, 30, 0));
        Assert.True(first.FirstTime);
        Assert.True(first.Announce);
        var again = book.Add(Unicorn(1, 20, 1));
        Assert.False(again.Announce);
        Assert.True(book.Add(Unicorn(2, 10, 2)).FirstTime); // another player's first
        Assert.Equal(new[] { Mysteries.Unicorn }, book.CaughtBy(1));
    }

    [Fact]
    public void ARedactedUnicornRowKeepsDistanceAndWeaponButNotNameOrDiagramAndTheLogShowsItsShot()
    {
        var book = new HuntBook();
        book.SetName(1, "Ann");
        book.Add(Unicorn(1, 77.7f, 0));
        var hidden = book.Records(Nobody).Single(r => r.Species == Mysteries.HiddenId(Mysteries.Unicorn)).Longest!;
        Assert.Equal((true, "", "", 77.7f, "sniper"), (hidden.Hidden, hidden.Exact, hidden.Variant, hidden.DistanceM, hidden.Weapon));
        Assert.Null(hidden.Diagram);
        var shown = book.Records(Caught(Mysteries.Unicorn)).Single(r => r.Species == Mysteries.Unicorn).Longest!;
        Assert.Equal((false, Mysteries.Unicorn), (shown.Hidden, shown.Exact));
        Assert.NotNull(shown.Diagram);
        var tile = book.PlayerLog(1, Caught(Mysteries.Unicorn))!.Life.Single(e => e.Species == Mysteries.Unicorn);
        Assert.Equal((true, (float?)null, (float?)77.7f), (tile.Caught, tile.BestKg, tile.LongestM));
        var model = LifeTileModel.From(tile);
        Assert.True(model.Mystery);
    }

    [Fact]
    public void AUnicornHitPictureUsesItsOwnShapeKey()
    {
        Assert.Equal("unicorn", HuntFormat.ShapeKey(Mysteries.Unicorn, ""));
        var h = book_view();
        var shapes = new AnimalShapes();
        var spec = HuntPicture.Resolve(shapes, h);
        Assert.Equal(("unicorn", "unicorn"), (spec.ShapeKey, spec.SpeciesId));
        Assert.True(spec.HasMark); // a diagram: the mark is drawn (on the body box when the species has no baked picture)

        static HuntView book_view()
        {
            var b = new HuntBook();
            b.Add(Unicorn(1, 40, 0, "sniper", new HitDiagram(100, 100, true, false, HuntShot.NoShape, 0)));
            return b.Records(Caught(Mysteries.Unicorn)).Single(r => r.Species == Mysteries.Unicorn).Longest!;
        }
    }
}
