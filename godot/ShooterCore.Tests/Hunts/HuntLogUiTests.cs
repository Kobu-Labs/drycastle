using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Hunts;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Wildlife;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Hunts;

/// <summary>The pure logic of the Hunter's Log screen (#336): its pages and Esc, the loaders, the table's query, the tiles and cards, the announcement lines, the pictures.</summary>
public class HuntLogUiTests
{
    static HuntView Hunt(long player = 1, string species = "deer", string exact = "deer", string variant = "", float? kg = 80, float? m = 120, string weapon = "sniper", HitDiagram? d = null, bool hidden = false) =>
        new(player, "Vesna", species, exact, variant, kg, m, weapon, d, 1_780_000_000, hidden);

    // ---- pages and Esc ----

    [Fact]
    public void Esc_on_a_players_log_goes_to_My_Log_then_closes()
    {
        StateMachineDiagnostics.Strict = true;
        var nav = new HuntLogNav();
        nav.ShowTab(2);
        nav.OpenPlayer(7, "Dax");
        Assert.Equal(HuntLogPage.PlayerLog, nav.Page);
        Assert.Equal(0, nav.TabIndex);
        Assert.Equal(HuntLogBack.ToMyLog, nav.Back());
        Assert.Equal(HuntLogPage.MyLog, nav.Page);
        Assert.False(nav.ViewingOther);
        Assert.Equal(HuntLogBack.Close, nav.Back());
        nav.ShowTab(1);
        Assert.Equal(HuntLogBack.Close, nav.Back());
    }

    [Fact]
    public void A_players_log_stays_parked_on_the_first_tab_while_another_tab_shows()
    {
        StateMachineDiagnostics.Strict = true;
        var nav = new HuntLogNav();
        nav.OpenPlayer(7, "Dax");
        nav.ShowTab(2);
        Assert.Equal(HuntLogBack.Close, nav.Back()); // not on the player's page: Esc closes
        nav.ShowTab(0);
        Assert.Equal(HuntLogPage.PlayerLog, nav.Page);
        Assert.Equal("Dax", nav.SubjectName);
        nav.OpenPlayer(0, ""); // own id: My Log
        Assert.Equal(HuntLogPage.MyLog, nav.Page);
        nav.OpenPlayer(8, "Ilka");
        nav.OpenPlayer(9, "Tomek"); // a player's log from a player's log
        Assert.Equal(9, nav.Subject);
        nav.Reset();
        Assert.Equal(HuntLogPage.MyLog, nav.Page);
        Assert.Equal(0, nav.Subject);
    }

    // ---- loaders ----

    [Fact]
    public void A_stale_answer_is_dropped_and_the_current_one_taken()
    {
        StateMachineDiagnostics.Strict = true;
        var l = new HuntLoader<string>();
        var first = l.Begin();
        var second = l.Begin();
        Assert.NotEqual(first, second);
        Assert.False(l.Accept(first, HuntStatus.Ok, "old"));
        Assert.Equal(HuntLoad.Loading, l.Phase);
        Assert.True(l.Accept(second, HuntStatus.Ok, "new"));
        Assert.Equal("new", l.Data);
        Assert.Equal(LoadView.Data, l.Show);
        Assert.False(l.Accept(second, HuntStatus.Ok, "dup")); // nothing is out any more
    }

    [Fact]
    public void Error_and_unknown_and_timeout_have_their_own_views_and_retry_works()
    {
        StateMachineDiagnostics.Strict = true;
        var l = new HuntLoader<string>();
        Assert.Equal(LoadView.Spinner, l.Show);
        l.Accept(l.Begin(), HuntStatus.Error, null);
        Assert.Equal(LoadView.Error, l.Show);
        l.Accept(l.Begin(), HuntStatus.Unknown, null);
        Assert.Equal(LoadView.Missing, l.Show);
        l.Begin();
        Assert.False(l.Tick(HuntLoader<string>.TimeoutSeconds - 1));
        Assert.True(l.Tick(2));
        Assert.Equal(LoadView.Error, l.Show);
        var r = l.Begin(); // Retry
        Assert.Equal(LoadView.Spinner, l.Show);
        Assert.True(l.Accept(r, HuntStatus.Ok, "x"));
    }

    [Fact]
    public void Stale_data_shows_while_a_newer_request_is_out_and_clear_forgets_it()
    {
        StateMachineDiagnostics.Strict = true;
        var l = new HuntLoader<string>();
        l.Accept(l.Begin(), HuntStatus.Ok, "kept");
        var again = l.Begin();
        Assert.Equal(LoadView.Data, l.Show);
        l.Clear();
        Assert.Equal(LoadView.Spinner, l.Show);
        Assert.False(l.Accept(again, HuntStatus.Ok, "late")); // the question changed
        Assert.Null(l.Data);
    }

    [Fact]
    public void Data_asks_through_the_send_and_learns_the_viewer_and_the_players()
    {
        StateMachineDiagnostics.Strict = true;
        var sent = new List<(EventKind, byte[])>();
        var d = new HuntLogData((k, p) => sent.Add((k, p)));
        d.RequestLog(0);
        d.RequestRecords();
        d.RequestHunts();
        Assert.Equal(new[] { EventKind.PlayerLogRequest, EventKind.RecordsRequest, EventKind.HuntsRequest }, sent.Select(s => s.Item1));
        var q = HuntEvents.DecodeRequest(sent[2].Item2)!;
        Assert.Equal(d.Hunts.Req, q.Req);
        Assert.Equal(HuntSort.Time, q.Sort);
        Assert.True(q.Descending);

        d.OnPage(new HuntsAnswer(d.Hunts.Req, HuntStatus.Ok, 42, 0, 1, new[] { Hunt(player: 5) }));
        Assert.Equal(42, d.ViewerId);
        Assert.True(d.ViewerKnown);
        Assert.Equal("Vesna", d.People.NameOf(5));
        // a stale answer teaches the viewer but is not shown
        var stale = (ushort)(d.Hunts.Req - 1);
        d.OnPage(new HuntsAnswer(stale, HuntStatus.Ok, 43, 0, 9, Array.Empty<HuntView>()));
        Assert.Equal(1u, d.Hunts.Data!.Total);
    }

    [Fact]
    public void An_empty_page_past_the_end_goes_back_to_the_last_page()
    {
        StateMachineDiagnostics.Strict = true;
        var sent = new List<(EventKind, byte[])>();
        var d = new HuntLogData((k, p) => sent.Add((k, p)));
        d.Table.SetPage(5);
        d.RequestHunts();
        d.OnPage(new HuntsAnswer(d.Hunts.Req, HuntStatus.Ok, 1, 5, 120, Array.Empty<HuntView>()));
        Assert.Equal(2, d.Table.Page); // 120 hunts: pages 0..2
        Assert.Equal(2, sent.Count);
        Assert.Equal(2, HuntEvents.DecodeRequest(sent[1].Item2)!.Page);
        Assert.Equal(LoadView.Spinner, d.Hunts.Show);
    }

    [Fact]
    public void Another_players_log_is_not_shown_in_place_of_the_last_while_it_loads()
    {
        StateMachineDiagnostics.Strict = true;
        var d = new HuntLogData((_, _) => { });
        var own = new PlayerLogView(1, "Me", 0, 0, null, null, Array.Empty<LifeEntry>());
        d.RequestLog(0);
        d.OnLog(new PlayerLogAnswer(d.Log.Req, HuntStatus.Ok, 1, own));
        d.RequestLog(0);
        Assert.Equal(LoadView.Data, d.Log.Show); // the same log again: the held copy stays
        d.RequestLog(9);
        Assert.Equal(LoadView.Spinner, d.Log.Show);
        d.OnLog(new PlayerLogAnswer(d.Log.Req, HuntStatus.Unknown, 1, null));
        Assert.Equal(LoadView.Missing, d.Log.Show);
    }

    // ---- the table's query ----

    [Fact]
    public void A_header_click_sorts_by_it_words_ascending_first_and_a_second_click_reverses()
    {
        var t = new HuntTableState();
        Assert.Equal((HuntSort.Time, true), (t.Sort, t.Descending));
        t.SetPage(3);
        t.ClickHeader(HuntSort.Hunter);
        Assert.Equal((HuntSort.Hunter, false, 0), (t.Sort, t.Descending, t.Page));
        t.ClickHeader(HuntSort.Hunter);
        Assert.True(t.Descending);
        t.ClickHeader(HuntSort.Kg);
        Assert.True(t.Descending); // numbers: biggest first
        t.ClickHeader(HuntSort.Weapon);
        Assert.False(t.Descending);
        t.ClickHeader(HuntSort.Time);
        Assert.True(t.Descending);
    }

    [Fact]
    public void A_filter_goes_back_to_the_first_page_and_all_hunts_of_a_species_sorts_by_weight()
    {
        var t = new HuntTableState();
        t.SetPage(2);
        t.SetWeapon("ak47");
        Assert.Equal(0, t.Page);
        Assert.True(t.Filtered);
        t.SetPlayer(5);
        t.JumpToSpecies("stag");
        Assert.Equal(("stag", "", 0L, HuntSort.Kg, true), (t.Species, t.Weapon, t.PlayerId, t.Sort, t.Descending));
        var r = t.ToRequest(9);
        Assert.Equal(new HuntsRequest(9, 0, HuntSort.Kg, true, "stag", "", 0), r);
        t.JumpToPlayer(4);
        Assert.Equal(("", 4L, HuntSort.Time), (t.Species, t.PlayerId, t.Sort));
        t.ClearFilters();
        Assert.False(t.Filtered);
        t.SetSpecies(Mysteries.AllHidden);
        Assert.Equal("?", t.ToRequest(1).Species);
    }

    [Fact]
    public void Pages_count_window_and_range()
    {
        Assert.Equal(1, HuntTableState.PageCount(0));
        Assert.Equal(1, HuntTableState.PageCount(50));
        Assert.Equal(2, HuntTableState.PageCount(51));
        Assert.Equal(new[] { 0, 1, 2 }, HuntTableState.PageWindow(0, 3));
        Assert.Equal(new[] { 0, 1, 2, 3, -1, 20 }, HuntTableState.PageWindow(1, 21));
        Assert.Equal(new[] { 0, -1, 8, 9, 10, 11, 12, -1, 20 }, HuntTableState.PageWindow(10, 21));
        Assert.Equal(new[] { 0 }, HuntTableState.PageWindow(0, 1));
        Assert.Equal((51, 100), HuntTableState.Range(1, 50));
        Assert.Equal((101, 107), HuntTableState.Range(2, 7));
        Assert.Equal((0, 0), HuntTableState.Range(0, 0));
    }

    [Fact]
    public void People_are_listed_by_name()
    {
        var p = new HuntPeople();
        p.Learn(3, "tomek"); p.Learn(2, "Dax"); p.Learn(4, "Ilka"); p.Learn(0, "guest"); p.Learn(5, "");
        Assert.Equal(new[] { "Dax", "Ilka", "tomek" }, p.Sorted().Select(x => x.Name));
        p.Learn(2, "Aaron"); // renamed
        Assert.Equal("Aaron", p.Sorted()[0].Name);
    }

    // ---- tiles, cards, words ----

    [Fact]
    public void A_tile_is_outlined_until_hunted_and_a_hidden_one_has_no_numbers()
    {
        var todo = LifeTileModel.From(new LifeEntry("fox", false, false, null, null, 0));
        Assert.Equal(TileLook.Outline, todo.Look);
        Assert.False(todo.Hidden);
        var got = LifeTileModel.From(new LifeEntry("fox", false, true, 6.5f, 143f, 1_700_000_000));
        Assert.Equal((TileLook.Filled, 6.5f, 143f), (got.Look, got.BestKg, got.LongestM));
        var blob = LifeTileModel.From(new LifeEntry("mystery-1", true, true, 9f, 9f, 5)); // even if a bad server sent numbers
        Assert.Equal((true, TileLook.Filled, (float?)null, (float?)null, 0L), (blob.Hidden, blob.Look, blob.BestKg, blob.LongestM, blob.CaughtAtUnix));
        Assert.True(LifeTileModel.From(new LifeEntry("bigfoot", false, true, null, 4f, 9)).Mystery);
        Assert.False(got.Mystery);
    }

    [Fact]
    public void A_card_is_hidden_unhunted_or_two_hits_and_a_mystery_shows_its_first_catch()
    {
        var hv = Hunt(kg: 200); var lg = Hunt(m: 300);
        Assert.Equal(RecordKind.Hidden, RecordCardModel.From(new SpeciesRecordView("mystery-1", true, true, null, null, hv)).Kind);
        var blob = RecordCardModel.From(new SpeciesRecordView("mystery-2", true, false, null, null, null));
        Assert.Equal((TileLook.Outline, null, null), (blob.HiddenLook, blob.Left, blob.Right));
        Assert.Equal(RecordKind.Unhunted, RecordCardModel.From(new SpeciesRecordView("fox", false, false, null, null, null)).Kind);
        var stag = RecordCardModel.From(new SpeciesRecordView("stag", false, true, hv, lg, Hunt(kg: 1)));
        Assert.Equal((RecordKind.Hunted, hv, lg, false), (stag.Kind, stag.Left, stag.Right, stag.Mystery));
        var bf = RecordCardModel.From(new SpeciesRecordView("bigfoot", false, true, null, lg, hv));
        Assert.Equal((hv, lg, true), (bf.Left, bf.Right, bf.Mystery)); // first catch, then the longest shot
    }

    [Fact]
    public void Numbers_keep_a_tenth_and_no_trailing_zero()
    {
        Assert.Equal("231", HuntFormat.Kg(231f));
        Assert.Equal("4.5", HuntFormat.Number(4.46f));
        Assert.Equal("1.3", HuntFormat.Number(1.25f + 0.04f));
        Assert.Equal("0", HuntFormat.Number(0.04f));
    }

    [Fact]
    public void The_shape_key_is_the_exact_species_or_species_slash_variant()
    {
        Assert.Equal("doe", HuntFormat.ShapeKey("doe", ""));
        Assert.Equal("moose/moose_male_01", HuntFormat.ShapeKey("moose", "moose_male_01"));
    }

    [Fact]
    public void Ago_picks_the_unit()
    {
        const long now = 1_780_000_000;
        Assert.Equal((AgoUnit.Now, 0), HuntFormat.Ago(now, now - 59));
        Assert.Equal((AgoUnit.Now, 0), HuntFormat.Ago(now, now + 500)); // a client clock behind the server's
        Assert.Equal((AgoUnit.Minutes, 1), HuntFormat.Ago(now, now - 60));
        Assert.Equal((AgoUnit.Minutes, 59), HuntFormat.Ago(now, now - 3599));
        Assert.Equal((AgoUnit.Hours, 1), HuntFormat.Ago(now, now - 3600));
        Assert.Equal((AgoUnit.Hours, 23), HuntFormat.Ago(now, now - 86399));
        Assert.Equal((AgoUnit.Days, 1), HuntFormat.Ago(now, now - 86400));
        Assert.Equal((AgoUnit.Days, 60), HuntFormat.Ago(now, now - 60 * 86400));
        Assert.Equal((AgoUnit.Date, 0), HuntFormat.Ago(now, now - 61 * 86400));
        Assert.Equal("1970-01-02", HuntFormat.Date(86400));
    }

    [Fact]
    public void A_hidden_creatures_filter_is_the_one_mystery_key()
    {
        Assert.Equal("?", HuntFormat.FilterKey("mystery-2"));
        Assert.Equal("bigfoot", HuntFormat.FilterKey("bigfoot"));
        Assert.Equal("stag", HuntFormat.FilterKey("stag"));
    }

    [Fact]
    public void A_hunt_that_is_the_record_holds_its_mark()
    {
        var heavy = Hunt(species: "stag", kg: 231, m: 90);
        var far = Hunt(player: 2, species: "stag", kg: 150, m: 400);
        var cards = new[] { new SpeciesRecordView("stag", false, true, heavy, far, heavy) };
        Assert.Equal(RecordMark.Weight, HuntRecords.MarkOf(cards, heavy));
        Assert.Equal(RecordMark.Shot, HuntRecords.MarkOf(cards, far));
        Assert.Equal(RecordMark.None, HuntRecords.MarkOf(cards, Hunt(species: "stag", kg: 231.5f, m: 90)));
        Assert.Equal(RecordMark.None, HuntRecords.MarkOf(cards, Hunt(species: "deer", kg: 231, m: 90)));
        Assert.Equal(RecordMark.None, HuntRecords.MarkOf(null, heavy)); // the records have not arrived
    }

    // ---- announcements ----

    [Theory]
    [InlineData(HuntAnnounceFlags.First, HuntLineKind.First)]
    [InlineData(HuntAnnounceFlags.Heaviest, HuntLineKind.RecordHeaviest)]
    [InlineData(HuntAnnounceFlags.Longest, HuntLineKind.RecordLongest)]
    [InlineData(HuntAnnounceFlags.First | HuntAnnounceFlags.Heaviest, HuntLineKind.RecordHeaviest)] // a first that is also a record is one line, the record
    [InlineData(HuntAnnounceFlags.Heaviest | HuntAnnounceFlags.Longest, HuntLineKind.RecordHeaviest)]
    [InlineData(HuntAnnounceFlags.First | HuntAnnounceFlags.Longest, HuntLineKind.RecordLongest)]
    public void A_record_outranks_a_first(HuntAnnounceFlags flags, HuntLineKind expected) =>
        Assert.Equal(expected, HuntLine.Of(new HuntAnnounceMsg(flags, Hunt()), 0).Kind);

    [Fact]
    public void A_mystery_has_no_weight_so_its_record_is_the_shot_and_a_hidden_line_says_so()
    {
        var hidden = Hunt(species: "mystery-1", exact: "", kg: null, m: 187, weapon: "sniper", hidden: true);
        var line = HuntLine.Of(new HuntAnnounceMsg(HuntAnnounceFlags.First | HuntAnnounceFlags.Heaviest | HuntAnnounceFlags.Longest, hidden), 1);
        Assert.Equal((HuntLineKind.RecordLongest, true, true), (line.Kind, line.Hidden, line.Mystery));
        var caught = Hunt(species: "bigfoot", exact: "bigfoot", kg: null, m: null, weapon: HuntWeapons.Event);
        var first = HuntLine.Of(new HuntAnnounceMsg(HuntAnnounceFlags.First, caught), 1);
        Assert.Equal((HuntLineKind.First, false, true), (first.Kind, first.Hidden, first.Mystery));
    }

    [Fact]
    public void A_line_is_mine_only_for_a_known_viewer_who_is_the_hunter()
    {
        var msg = new HuntAnnounceMsg(HuntAnnounceFlags.First, Hunt(player: 42));
        Assert.True(HuntLine.Of(msg, 42).Mine);
        Assert.False(HuntLine.Of(msg, 41).Mine);
        Assert.False(HuntLine.Of(new HuntAnnounceMsg(HuntAnnounceFlags.First, Hunt(player: 0)), 0).Mine); // a guest is nobody's
    }

    // ---- pictures ----

    // a 4 x 2 standing shape; the right two columns of the top row are the head
    static AnimalShapes Shapes()
    {
        var cells = Convert.ToBase64String(new byte[] { 20, 20, 20, 20, 0, 20, 0x80 | 10, 0x80 | 10 });
        var frame = "{\"z0\":0,\"cols\":4,\"rows\":2,\"cells\":\"" + cells + "\"}";
        return AnimalShapes.Parse("{\"version\":1,\"cell\":0.05,\"species\":{\"deer\":{\"idle\":{\"frames\":[" + frame + "]}},\"moose\":{\"idle\":{\"frames\":[" + frame + "]}}}}");
    }

    [Fact]
    public void A_hit_with_no_diagram_has_no_mark_and_the_looks_standing_shape()
    {
        var s = HuntPicture.Resolve(Shapes(), Hunt(exact: "deer", d: null));
        Assert.False(s.HasMark);
        Assert.NotNull(s.Standing);
        Assert.Equal("deer", s.ShapeKey);
    }

    [Fact]
    public void A_hit_on_the_standing_shape_keeps_its_place_and_a_body_box_hit_draws_the_species_picture()
    {
        var onShape = HuntPicture.Resolve(Shapes(), Hunt(exact: "deer", d: new HitDiagram(128, 64, true, false, (byte)AnimalPose.Idle, 0)));
        Assert.True(onShape.HasMark);
        Assert.InRange(onShape.Along, 0.49f, 0.51f);
        Assert.InRange(onShape.Height, 0.24f, 0.26f);
        var box = HuntPicture.Resolve(Shapes(), Hunt(exact: "deer", d: new HitDiagram(10, 20, false, true, HuntShot.NoShape, 0)));
        Assert.Null(box.Standing);
        Assert.True(box.HasMark);
        Assert.True(box.Head);
        Assert.Equal("deer", box.SpeciesId);
    }

    [Fact]
    public void A_species_with_no_shape_has_none_and_the_variant_picks_its_own_key()
    {
        var none = HuntPicture.Resolve(Shapes(), Hunt(exact: "fox", d: new HitDiagram(1, 1, false, false, 0, 0)));
        Assert.Null(none.Standing);
        Assert.True(none.HasMark);
        Assert.Equal("moose/moose_male_01", HuntPicture.Resolve(Shapes(), Hunt(exact: "moose", variant: "moose_male_01")).ShapeKey);
        Assert.Null(HuntPicture.Resolve(Shapes(), Hunt(exact: "moose", variant: "moose_male_01")).Standing); // no shape of its own look
    }

    // ---- art ----

    [Fact]
    public void The_outline_of_a_mask_is_its_edge_band()
    {
        const int n = 12;
        var mask = new byte[n * n];
        for (var y = 2; y < 10; y++) for (var x = 2; x < 10; x++) mask[y * n + x] = 255;
        var edge = HuntArtMath.Outline(mask, n, n, 2);
        Assert.Equal(255, edge[2 * n + 2]); // the corner
        Assert.Equal(255, edge[3 * n + 5]); // one in from the edge
        Assert.Equal(0, edge[5 * n + 5]);   // the middle
        Assert.Equal(0, edge[0]);           // outside
        Assert.True(edge.Count(b => b == 255) < mask.Count(b => b == 255));
    }

    [Fact]
    public void A_hit_area_is_always_named_when_there_is_a_diagram()
    {
        Assert.Equal(HitArea.None, HitAreas.Of(null));
        Assert.Equal(HitArea.Head, HitAreas.Of(new HitDiagram(100, 250, false, true, 0, 0)));
        Assert.Equal(HitArea.Legs, HitAreas.Of(new HitDiagram(100, 20, false, false, 0, 0)));
        Assert.Equal(HitArea.Body, HitAreas.Of(new HitDiagram(100, 150, false, false, 0, 0)));
    }
}
