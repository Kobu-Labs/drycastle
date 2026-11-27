using Godot;
using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Chat;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Jobs;
using Drycastle.ShooterCore.Plantation;
using Drycastle.Shooter.Net.Server.Highs;
using NVec2 = System.Numerics.Vector2;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Cartel;

/// <summary>
/// The Harvest heist (#495; GLOSSARY "Heist"; docs/cartel.md "Harvest"), the first Heist on the template (<see cref="HeistServer"/>): the objective is the Contact's own plantation (the plantation zone
/// nearest him), its area the outline grown by area_margin_m. While it runs, a party member's hold of the interact key in a plantation (fill_hold_seconds) fills a weed Duffel in their hands instead
/// of High, until the maximum is filled, once the van has reached the plantation (before that the Assault clock has not started: <see cref="Completed"/> fills nothing); a hold that cannot
/// fill (carrying a Duffel, the maximum filled) does not count (<see cref="Blocks"/>). Outside a Harvest the plantation is unchanged. The Assaults come in from the road side. Numbers in cartel.json <c>jobs.harvest</c>.
/// </summary>
internal sealed class HarvestJobKind : IJobKind, IPlantationFill
{
    public const string KindId = "harvest";
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

    /// <summary>A hold that cannot fill: the player carries a Duffel already (one per player), or the harvest's maximum is filled. It does not count, rather than run its seconds and be refused.</summary>
    public bool Blocks(ServerClient c) => RunOf(c) is { } run && (run.Filled >= run.Heist.Max || _s.Duffels.Book.CarriedBy(c.Id) != null);

    public void Completed(ServerClient c)
    {
        if (RunOf(c) is not { } run) return;
        // the Assault clock starts when the van reaches the area: filling before that would load the most at the edge and drive through with no Assault at all
        if (run.Heist.Phase == HeistPhase.ToObjective) { _s.Ctx.ChatTo(c, ChatLineKind.Info, $"Bring the Cartel van to {Place} first."); return; }
        if (run.Filled >= run.Heist.Max) { _s.Ctx.ChatTo(c, ChatLineKind.Info, $"All {run.Heist.Max} duffels of the harvest are filled: load them and drive."); return; }
        run.SyncParty(); // a squad mate who just joined is in the party the Duffel belongs to
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
