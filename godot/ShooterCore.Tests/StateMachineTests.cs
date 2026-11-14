using System.Runtime.CompilerServices;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>Turns illegal transitions into exceptions for every test in this assembly, so a test that drives a machine along an undeclared edge fails.</summary>
static class StrictStateMachines
{
    [ModuleInitializer]
    internal static void Init() => StateMachineDiagnostics.Strict = true;
}

public class StateMachineTests
{
    enum Door { Closed, Opening, Open, Closing, Broken }

    static readonly StateMachineSpec<Door> Spec = StateMachineSpec<Door>.Define("Test.Door", "A door.", Door.Closed)
        .Allow(Door.Closed, Door.Opening)
        .Allow(Door.Opening, Door.Open)
        .Allow(Door.Open, Door.Closing)
        .Allow(Door.Closing, Door.Closed)
        .FromAnywhere(Door.Broken)
        .Build();

    [Fact]
    public void Starts_in_the_initial_state_with_no_countdown()
    {
        var m = new StateMachine<Door>(Spec);
        Assert.True(m.Is(Door.Closed));
        Assert.False(m.Expired);
        Assert.False(m.Tick(1000));
    }

    [Fact]
    public void Enter_starts_the_clock_and_tick_counts_it_down_like_a_hand_written_timer()
    {
        var m = new StateMachine<Door>(Spec);
        Assert.True(m.Enter(Door.Opening, 1f));
        Assert.False(m.Tick(0.4f));
        Assert.Equal(0.4f, m.Elapsed, 5);
        Assert.Equal(0.6f, m.Left, 5);
        Assert.True(m.Tick(0.6f));
        Assert.True(m.Expired);
        Assert.True(m.Tick(0.1f)); // stays expired until the next Enter
    }

    [Fact]
    public void Entering_the_current_state_is_a_no_op_and_keeps_the_clock()
    {
        var m = new StateMachine<Door>(Spec);
        m.Enter(Door.Opening, 2f);
        m.Tick(0.5f);
        var changes = 0;
        m.Changed += (_, _) => changes++;
        Assert.False(m.Enter(Door.Opening, 9f));
        Assert.Equal(1.5f, m.Left, 5);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void Restart_resets_the_clock_without_a_change_event()
    {
        var m = new StateMachine<Door>(Spec);
        m.Enter(Door.Opening, 2f);
        m.Tick(1f);
        var changes = 0;
        m.Changed += (_, _) => changes++;
        m.Restart(3f);
        Assert.Equal(0f, m.Elapsed);
        Assert.Equal(3f, m.Left);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void Changed_reports_from_and_to_and_previous_is_kept()
    {
        var m = new StateMachine<Door>(Spec);
        (Door, Door)? seen = null;
        m.Changed += (f, t) => seen = (f, t);
        m.Enter(Door.Opening);
        Assert.Equal((Door.Closed, Door.Opening), seen);
        Assert.Equal(Door.Closed, m.Previous);
    }

    [Fact]
    public void An_undeclared_transition_throws_in_strict_mode_and_is_reported_but_taken_otherwise()
    {
        var m = new StateMachine<Door>(Spec);
        Assert.Throws<InvalidOperationException>(() => m.Enter(Door.Open));
        Assert.True(m.Is(Door.Closed)); // the throw came before the change

        StateMachineDiagnostics.Strict = false;
        var reports = new List<string>();
        StateMachineDiagnostics.Report = reports.Add;
        try
        {
            Assert.True(m.Enter(Door.Open));
            Assert.True(m.Is(Door.Open));
            Assert.Single(reports);
            Assert.Contains("Test.Door: Closed -> Open", reports[0]);
        }
        finally { StateMachineDiagnostics.Strict = true; StateMachineDiagnostics.Report = null; }
    }

    [Fact]
    public void From_anywhere_is_legal_from_every_state()
    {
        foreach (var from in Enum.GetValues<Door>())
        {
            Assert.True(Spec.Allows(from, Door.Broken));
        }
    }

    [Fact]
    public void Reset_jumps_without_a_check_or_event()
    {
        var m = new StateMachine<Door>(Spec);
        var changes = 0;
        m.Changed += (_, _) => changes++;
        m.Reset(Door.Open);
        Assert.True(m.Is(Door.Open));
        Assert.Equal(0, changes);
    }

    [Fact]
    public void Spec_reports_terminal_and_unreachable_states()
    {
        Assert.Equal(new[] { "Broken" }, Spec.Terminal);
        Assert.Empty(Spec.Unreachable);
    }
}

public class StateMachineCatalogTests
{
    static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "GLOSSARY.md"))) return d.FullName;
        throw new InvalidOperationException("GLOSSARY.md not found above the test output folder");
    }

    [Fact]
    public void Every_spec_has_a_unique_dotted_name_and_no_unreachable_state()
    {
        var names = new HashSet<string>();
        foreach (var spec in StateMachineCatalog.All)
        {
            Assert.Matches(@"^[A-Z]\w*\.[A-Z]\w*$", spec.Name);
            Assert.True(names.Add(spec.Name), $"duplicate spec name {spec.Name}");
            Assert.True(spec.Summary.Length > 0, $"{spec.Name} has no summary");
            Assert.True(spec.Unreachable.Count == 0, $"{spec.Name}: no transition leads to {string.Join(", ", spec.Unreachable)}");
        }
    }

    /// <summary>docs/state-machines.md is generated from the specs. Out of date: run with UPDATE_STATE_DOCS=1 to rewrite it.</summary>
    [Fact]
    public void The_docs_match_the_specs()
    {
        var path = Path.Combine(RepoRoot(), "docs", "state-machines.md");
        var want = StateMachineDocs.Render(StateMachineCatalog.All);
        if (Environment.GetEnvironmentVariable("UPDATE_STATE_DOCS") == "1") File.WriteAllText(path, want);
        var have = File.Exists(path) ? File.ReadAllText(path) : "";
        Assert.True(have == want, "docs/state-machines.md is out of date: run `UPDATE_STATE_DOCS=1 dotnet test godot/ShooterCore.Tests --filter StateMachineCatalogTests`");
    }
}
