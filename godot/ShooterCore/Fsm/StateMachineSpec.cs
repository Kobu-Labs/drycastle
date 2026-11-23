using System.Text;

namespace Drycastle.ShooterCore.Fsm;

/// <summary>What the catalog and the docs need from any <see cref="StateMachineSpec{TState}"/>, whatever its state type.</summary>
public interface IStateMachineSpec
{
    /// <summary>Stable id, "Owner.Concept" (<c>"Demon.Phase"</c>): what a log line, a test and docs/state-machines.md call the machine.</summary>
    string Name { get; }
    /// <summary>One sentence: what the machine decides.</summary>
    string Summary { get; }
    string InitialName { get; }
    IReadOnlyList<string> StateNames { get; }
    /// <summary>Declared transitions as (from, to) names, in declaration order.</summary>
    IReadOnlyList<(string From, string To)> TransitionNames { get; }
    /// <summary>The note a state was given with <see cref="StateMachineSpec{TState}.Builder.Note"/>, or null.</summary>
    string? NoteOf(string state);
    /// <summary>States no declared transition leads to (other than the initial one).</summary>
    IReadOnlyList<string> Unreachable { get; }
    /// <summary>States no declared transition leaves: the ends. A machine with none never finishes.</summary>
    IReadOnlyList<string> Terminal { get; }
}

/// <summary>
/// The declared shape of one state machine: its states (the values of an enum), the first one, and which transitions are legal. Built once (a static
/// readonly next to the owner), shared by every instance, and listed in <see cref="StateMachineCatalog"/> so docs/state-machines.md and the tests can
/// see every machine in the game. The spec is the single place a reader, a reviewer or an agent looks to learn "what can follow what".
/// </summary>
public sealed class StateMachineSpec<TState> : IStateMachineSpec where TState : struct, Enum
{
    readonly Dictionary<TState, HashSet<TState>> _next;
    readonly List<(TState From, TState To)> _order;
    readonly Dictionary<TState, string> _notes;
    readonly HashSet<TState> _fromAny;

    StateMachineSpec(string name, string summary, TState initial, Dictionary<TState, HashSet<TState>> next, List<(TState, TState)> order, HashSet<TState> fromAny, Dictionary<TState, string> notes)
    {
        Name = name; Summary = summary; Initial = initial; _next = next; _order = order; _fromAny = fromAny; _notes = notes;
    }

    public string Name { get; }
    public string Summary { get; }
    public TState Initial { get; }

    /// <summary>Starts a spec. The initial state is what a new machine is in.</summary>
    public static Builder Define(string name, string summary, TState initial) => new(name, summary, initial);

    /// <summary>Whether <paramref name="from"/> may go to <paramref name="to"/>. Staying put is always legal (and does nothing). A state declared with <see cref="Builder.FromAnywhere"/> is legal from every state.</summary>
    public bool Allows(TState from, TState to) =>
        EqualityComparer<TState>.Default.Equals(from, to) || _fromAny.Contains(to) || (_next.TryGetValue(from, out var set) && set.Contains(to));

    static IEnumerable<TState> AllStates => Enum.GetValues<TState>();

    public string InitialName => Initial.ToString();
    public IReadOnlyList<string> StateNames => AllStates.Select(s => s.ToString()).ToList();
    public IReadOnlyList<(string From, string To)> TransitionNames
    {
        get
        {
            var list = _order.Select(t => (t.From.ToString(), t.To.ToString())).ToList();
            foreach (var any in _fromAny) list.Add(("*", any.ToString()));
            return list;
        }
    }
    public string? NoteOf(string state) => Enum.TryParse<TState>(state, out var s) && _notes.TryGetValue(s, out var n) ? n : null;
    public IReadOnlyList<string> Unreachable => AllStates
        .Where(s => !EqualityComparer<TState>.Default.Equals(s, Initial) && !_fromAny.Contains(s) && !_next.Values.Any(set => set.Contains(s)))
        .Select(s => s.ToString()).ToList();
    public IReadOnlyList<string> Terminal => AllStates
        .Where(s => !_next.TryGetValue(s, out var set) || set.Count == 0 || set.All(t => EqualityComparer<TState>.Default.Equals(t, s)))
        .Select(s => s.ToString()).ToList();

    public sealed class Builder
    {
        readonly string _name, _summary;
        readonly TState _initial;
        readonly Dictionary<TState, HashSet<TState>> _next = new();
        readonly List<(TState, TState)> _order = new();
        readonly HashSet<TState> _fromAny = new();
        readonly Dictionary<TState, string> _notes = new();

        internal Builder(string name, string summary, TState initial) { _name = name; _summary = summary; _initial = initial; }

        /// <summary><paramref name="from"/> may go to each of <paramref name="to"/>.</summary>
        public Builder Allow(TState from, params TState[] to)
        {
            if (!_next.TryGetValue(from, out var set)) _next[from] = set = new HashSet<TState>();
            foreach (var t in to) if (set.Add(t)) _order.Add((from, t));
            return this;
        }

        /// <summary>Every state in <paramref name="from"/> may go to <paramref name="to"/>.</summary>
        public Builder AllowAll(IEnumerable<TState> from, TState to)
        {
            foreach (var f in from) Allow(f, to);
            return this;
        }

        /// <summary><paramref name="to"/> is reachable from every state (a reset, a death, an abort). Drawn as "*" in the docs.</summary>
        public Builder FromAnywhere(params TState[] to) { foreach (var t in to) _fromAny.Add(t); return this; }

        /// <summary>A line for docs/state-machines.md: what being in <paramref name="state"/> means.</summary>
        public Builder Note(TState state, string text) { _notes[state] = text; return this; }

        public StateMachineSpec<TState> Build() => new(_name, _summary, _initial, _next, _order, _fromAny, _notes);
    }
}

/// <summary>Renders the specs of <see cref="StateMachineCatalog"/> as the markdown of docs/state-machines.md (mermaid state diagrams).</summary>
public static class StateMachineDocs
{
    public static string Render(IEnumerable<IStateMachineSpec> specs)
    {
        var sb = new StringBuilder();
        sb.Append("# State machines\n\n");
        sb.Append("Generated from the specs in `godot/ShooterCore/Fsm/StateMachineCatalog.cs` by `StateMachineCatalogTests`. Do not edit by hand: change the spec, then run\n");
        sb.Append("`UPDATE_STATE_DOCS=1 dotnet test godot/ShooterCore.Tests --filter StateMachineCatalogTests`.\n\n");
        sb.Append("How to add or change one: see `godot/ShooterCore/Fsm/StateMachine.cs`. \"From any state\" lines list the transitions that need no edge.\n\n");
        foreach (var spec in specs.OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            sb.Append("## ").Append(spec.Name).Append("\n\n").Append(spec.Summary).Append("\n\n");
            sb.Append("```mermaid\nstateDiagram-v2\n");
            sb.Append("    [*] --> ").Append(spec.InitialName).Append('\n');
            var anywhere = new List<string>();
            foreach (var (from, to) in spec.TransitionNames)
            {
                if (from == "*") { anywhere.Add(to); continue; }
                sb.Append("    ").Append(from).Append(" --> ").Append(to).Append('\n');
            }
            foreach (var end in spec.Terminal) sb.Append("    ").Append(end).Append(" --> [*]\n");
            sb.Append("```\n\n");
            if (anywhere.Count > 0) sb.Append("From any state: ").Append(string.Join(", ", anywhere)).Append("\n\n");
            foreach (var s in spec.StateNames)
                if (spec.NoteOf(s) is { } note) sb.Append("- **").Append(s).Append("**: ").Append(note).Append('\n');
            sb.Append('\n');
        }
        return sb.ToString();
    }
}
