using Godot;
using Drycastle.Shooter.Net.Server.Houses;
using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Jobs;
using Drycastle.ShooterCore.Thieves;
using NVec2 = System.Numerics.Vector2;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Cartel;

/// <summary>
/// The Trap house heist (#496; GLOSSARY "Heist", "Trap house"; docs/cartel.md "Trap house", ADR 0046), the second Heist on the template (<see cref="HeistServer"/>): a random hollow house with road
/// access (<see cref="TrapHouseServer.Pick"/>, held in the book as <see cref="TrapHouseHeistRules.Key"/>) is dressed once the run began, a Rival crew holds it from the dressing's posts, product and cash Duffels
/// lie on its tables and its safe is armed for the party (<see cref="DrillServer.Arm"/>; the open safe's cash Duffels load like the rest). The objective's area is the house and the road spot where the
/// van parks; the Assaults come to it. Its end strips the house, disarms the safe and removes the defenders (the template removes the van, the Duffels and the Assaults). Numbers in cartel.json
/// <c>jobs.trap_house</c>; <c>/cartel job trap_house [tier]</c> to test.
/// </summary>
internal sealed class TrapHouseJobKind : IJobKind
{
    public const string KindId = "trap_house";
    const string Section = "cartel.json: jobs.trap_house";

    readonly HeistServer _s;
    readonly TrapHouseServer _houses;
    readonly DrillServer _drills;
    readonly RivalCrewServer _rivals;
    readonly TrapHouseHeistData _data;

    public TrapHouseJobKind(HeistServer s, CartelFile file, TrapHouseServer houses, DrillServer drills, RivalCrewServer rivals)
    {
        _s = s; _houses = houses; _drills = drills; _rivals = rivals;
        _data = DataFile.Section<TrapHouseHeistData>(file.JobSection(KindId), Section);
        _data.ValidateTrapHouse(Section, file.Tiers.Count);
    }

    public string Id => KindId;
    public string Name => _data.Name;
    public float Seconds(int tier) => HeistRules.Seconds(_data, tier);

    public IJobObjective? Begin(Job job, JobBook book, out string? refusal)
    {
        refusal = null;
        if (_s.Map().Contact is not { } contact) { refusal = "This map has no Cartel."; return null; }
        if (_houses.Data is not { } houseData) { refusal = "This map has no hollow house to take."; return null; }
        var min = HeistRules.MinDuffels(_data, job.Tier, _s.Parties.MembersOf(job.Owner).Count); // before the van's capacity: never fewer than the Heist's
        // planned only: the props go up once the run began, so a start the drop point or the van refuses shows nobody anything
        if (_houses.Pick(min) is not { } picked) { refusal = $"No trap house is free now (road access, nobody inside, {min} duffel spots or more)."; return null; }
        var (candidate, dressing) = picked;
        if (!book.Reserve(job, TrapHouseHeistRules.Key(dressing.House))) { refusal = "That house is another job's."; return null; }
        var c = TrapHouseHeistRules.Centre(candidate.House);
        var goal = new Goal(this, dressing, c, candidate.RoadGround, new NVec3(c.X, _s.Ground(c.X, c.Y), c.Y));
        if (_s.Begin(job, book, _data, goal, contact, out refusal) is not { } run) return null;
        _houses.Apply(dressing);
        goal.Hold(run, houseData);
        return run;
    }

    /// <summary>One Job's house: its dressing, its defenders and its safe's Drill, and the template's goal (the area, the line's Drill part, the marker on the safe).</summary>
    sealed class Goal(TrapHouseJobKind kind, TrapHouseDressing dressing, NVec2 centre2, NVec2 road, NVec3 centre) : IHeistGoal
    {
        HeistRun? _run;
        ThiefGang? _crew;
        Drill? _drill;

        public string Place => "the trap house";
        public string FillLine => "Take the duffels off the tables and the open safe and throw them in the van";
        public NVec3 Centre => centre;
        public bool InArea(NVec3 p) => TrapHouseHeistRules.InArea(centre2, road, kind._data.AreaM, p.X, p.Z);

        DrillState? DrillNow => _drill != null && kind._drills.Of(dressing.House) == _drill ? _drill.State : null;

        public string Line(Heist heist, string line) => TrapHouseHeistRules.Line(heist, line, DrillNow);

        public NVec3? Marker(Heist heist, NVec3? marker) => TrapHouseHeistRules.MarkSafe(heist, DrillNow) && dressing.Safe is { } safe ? safe.Pos : marker;

        /// <summary>The run began: the defenders on their posts, the Duffels on the tables, the safe armed, all for the run's party set.</summary>
        public void Hold(HeistRun run, TrapHouseFile houseData)
        {
            _run = run;
            var tier = run.Job.Tier;
            var party = run.Party;
            var cash = dressing.Safe != null ? DrillRules.CashDuffels(houseData.Drill, tier) : 0;
            var tables = TrapHouseHeistRules.TableDuffels(dressing.Duffels.Count, run.Heist.Min, run.Heist.Max, cash);
            var placed = 0;
            foreach (var spot in dressing.Duffels.Take(tables))
                if (DuffelRules.Parse(spot.Kind) is { } k && kind._s.Duffels.Book.Place(k, party, spot.Pos, spot.Yaw) != null) placed++;
            var (gunmen, enforcers) = TrapHouseHeistRules.Defenders(kind._data, tier, party.Count, dressing.Posts.Count);
            _crew = kind._rivals.NewCrew(centre, p => party.Contains(p));
            var posted = 0;
            var order = HeistRules.CrewOrder(gunmen, enforcers);
            for (var i = 0; i < order.Count; i++)
                if (_crew.AddPosted(order[i], dressing.Posts[i].Pos, dressing.Posts[i].Yaw) != null) posted++;
            _drill = kind._drills.Arm(dressing.House, party, tier);
            kind._drills.Done += OnSafeOpen;
            GD.Print($"server: {run.Job}: trap house {dressing.House}, {placed} duffel(s) on the tables, {posted} defender(s), {(_drill != null ? $"the safe armed ({cash} cash)" : "no safe")}");
        }

        void OnSafeOpen(Drill drill, IReadOnlyList<Duffel> paid)
        {
            if (drill != _drill || _run == null) return;
            _run.Tell($"The safe is open: {paid.Count} cash duffel{(paid.Count == 1 ? "" : "s")} by it. Throw them in the van too.");
        }

        public void End()
        {
            kind._drills.Done -= OnSafeOpen;
            if (_drill != null && kind._drills.Of(dressing.House) == _drill) kind._drills.Disarm(dressing.House);
            if (_crew != null) kind._rivals.Remove(_crew);
            kind._houses.Strip(dressing.House);
        }
    }
}
