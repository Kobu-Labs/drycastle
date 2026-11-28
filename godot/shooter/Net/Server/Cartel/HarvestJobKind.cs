using Godot;
using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Jobs;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Plantation;
using Drycastle.ShooterCore.Status;
using Drycastle.Shooter.Net.Server.Highs;
using NVec2 = System.Numerics.Vector2;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Cartel;

/// <summary>
/// The Harvest heist (#495; GLOSSARY "Heist"; docs/cartel.md "Harvest"), the first Heist on the template (<see cref="HeistServer"/>): the objective is the Contact's own plantation (the plantation zone
/// nearest him), its area the outline grown by area_margin_m. While it runs, a party member's hold of the interact key in a plantation (fill_hold_seconds) fills a weed Duffel in their hands instead
/// of High, until the maximum is filled, once the van has reached the plantation (before that the Assault clock has not started); a hold that cannot
/// fill (the van not there, carrying a Duffel, the maximum filled) does not count (<see cref="ReachOf"/>) and the player is told why (the prompt: <see cref="EventKind.HarvestFill"/>); a hold that can shows the ring and,
/// after fill_show_after_seconds, everyone sees the filler bend and reach (<see cref="StatusKind.Filling"/>). Outside a Harvest the plantation is unchanged. The Assaults come in from the road side. Numbers in cartel.json <c>jobs.harvest</c>.
/// </summary>
internal sealed class HarvestJobKind : IJobKind, IPlantationFill
{
    public const string KindId = HarvestData.KindId;
    const string Section = "cartel.json: jobs.harvest";

    readonly HeistServer _s;
    readonly HarvestData _data;

    public HarvestJobKind(HeistServer s, CartelFile file)
    {
        _s = s;
        _data = DataFile.Section<HarvestData>(file.JobSection(KindId), Section);
        _data.Validate(Section, file.Tiers.Count);
    }

    public string Id => KindId;
    public string Name => _data.Name;
    public float Seconds(int tier) => HeistRules.Seconds(_data, tier);

    public IJobObjective? Begin(Job job, JobBook book, out string? refusal)
    {
        refusal = null;
        if (_s.Map().Contact is not { } contact) { refusal = "This map has no Cartel."; return null; }
        var rings = _s.Polygons().Rings(PlantationRules.Kind);
        var at = PlantationArea.Nearest(rings.Select(r => (IReadOnlyList<NVec2>)r.Points).ToList(), new NVec2(contact.Position.X, contact.Position.Z));
        if (at < 0) { refusal = "This map has no plantation to harvest."; return null; }
        var area = new PlantationArea(rings[at].Points, _data.AreaMarginM);
        var goal = new Goal(area, new NVec3(area.Centre.X, _s.Ground(area.Centre.X, area.Centre.Y), area.Centre.Y));
        return _s.Begin(job, book, _data, goal, contact, out refusal);
    }

    /// <summary>The player's running Harvest, or null (no job, or another kind).</summary>
    HeistRun? RunOf(ServerClient c) => _s.RunOf(c.Id) is { Job.Kind: HarvestJobKind } run ? run : null;

    public double? HoldSecondsFor(ServerClient c) => RunOf(c) != null ? _data.FillHoldSeconds : null;

    public double ShowAfterSeconds => _data.FillShowAfterSeconds;

    /// <summary>
    /// What the hold does for the player (<see cref="HarvestFillRules.Reach"/>): a hold that cannot fill (the van not at the plantation yet, a Duffel in the hands, the maximum filled) does not count, rather than run its
    /// seconds and be refused; the player is told which it is (the prompt), no chat line.
    /// </summary>
    public FillReach ReachOf(ServerClient c, out int max)
    {
        max = 0;
        if (RunOf(c) is not { } run) return FillReach.Off;
        max = run.Heist.Max;
        return HarvestFillRules.Reach(run.Heist.Phase, run.Filled, max, _s.Duffels.Book.CarriedBy(c.Id) != null);
    }

    public void Completed(ServerClient c)
    {
        if (RunOf(c) is not { } run || ReachOf(c, out _) != FillReach.Ready) return; // the same rules the hold was counted under
        run.Party.Refresh(_s.Parties); // a squad mate who joined since the last tick is in the party the Duffel belongs to
        if (_s.Duffels.Give(DuffelKind.Weed, run.Party, c) == null) return; // carrying one already (told), or not alive
        run.Filled++;
        GD.Print($"server: {c.Name} #{c.Id} filled a weed duffel for {run.Job} ({run.Filled}/{run.Heist.Max})");
    }

    const string Place = "the plantation";

    sealed class Goal(PlantationArea area, NVec3 centre) : IHeistGoal
    {
        public string Place => HarvestJobKind.Place;
        public string FillLine => "Fill weed duffels in the plantation (hold {interact}) and throw them in the van";
        public NVec3 Centre => centre;
        public bool InArea(NVec3 p) => area.Contains(p.X, p.Z);
    }
}
