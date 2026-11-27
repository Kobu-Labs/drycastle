using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Jobs;

namespace Drycastle.ShooterCore.Cartel;

/// <summary>
/// The trivial kind of Job for the tests and the admin's <c>/cartel job test [tier]</c>: nothing in the world; it succeeds once it has run <c>succeed_seconds</c> with its leader
/// alive, and fails as any Job does (its timer, a death, a quit, a round reset). Numbers in cartel.json <c>jobs.test</c>.
/// </summary>
public sealed class TestJobKind : IJobKind
{
    public const string KindId = "test";

    /// <summary>cartel.json <c>jobs.test</c>.</summary>
    public sealed class Data
    {
        public string Name { get; set; } = "Test job";
        /// <summary>The timer per tier, tier 1 first: one number per tier of cartel.json.</summary>
        public float[] Seconds { get; set; } = { 120, 110, 100, 90, 80 };
        public float SucceedSeconds { get; set; } = 20;
    }

    readonly Data _data;

    public TestJobKind(CartelFile file)
    {
        _data = DataFile.Section<Data>(file.JobSection(KindId), "cartel.json: jobs.test");
        CartelTiers.Check(_data.Seconds, file.Tiers.Count, "cartel.json: jobs.test.seconds", CartelTiers.Positive, "more than 0");
        if (!float.IsFinite(_data.SucceedSeconds) || _data.SucceedSeconds <= 0) throw new DataFileException("cartel.json: jobs.test.succeed_seconds must be more than 0");
    }

    public string Id => KindId;
    public string Name => _data.Name;
    public float Seconds(int tier) => CartelTiers.Of(_data.Seconds, tier);

    public IJobObjective? Begin(Job job, JobBook book, out string? refusal)
    {
        refusal = null;
        return new Objective(_data.SucceedSeconds);
    }

    sealed class Objective(float succeedSeconds) : IJobObjective
    {
        public JobStep Step(Job job, float dt) => job.Elapsed + dt >= succeedSeconds ? JobStep.Succeeded : JobStep.Running;
    }
}
