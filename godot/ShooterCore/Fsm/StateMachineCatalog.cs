using System.Reflection;

namespace Drycastle.ShooterCore;

/// <summary>
/// Every state machine in the game that has a <see cref="StateMachineSpec{TState}"/>. Nothing to register: any <c>static</c> field of a type in this assembly
/// whose type is an <see cref="IStateMachineSpec"/> is found by reflection, so a new machine shows up in docs/state-machines.md and the catalog tests by
/// existing. Machines whose view lives in the Godot project keep their enum and spec here, with the pure logic, so the tests can see them.
/// </summary>
public static class StateMachineCatalog
{
    static IReadOnlyList<IStateMachineSpec>? _all;

    public static IReadOnlyList<IStateMachineSpec> All => _all ??= Discover();

    static List<IStateMachineSpec> Discover()
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var found = new Dictionary<string, IStateMachineSpec>();
        foreach (var type in typeof(StateMachineCatalog).Assembly.GetTypes())
        {
            if (type.IsGenericTypeDefinition) continue;
            foreach (var field in type.GetFields(flags))
            {
                if (!typeof(IStateMachineSpec).IsAssignableFrom(field.FieldType) || field.GetValue(null) is not IStateMachineSpec spec) continue;
                if (!found.TryAdd(spec.Name, spec)) throw new InvalidOperationException($"two state machine specs are named {spec.Name}: names are the id in logs and docs/state-machines.md, make one unique");
            }
        }
        return found.Values.OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
    }
}
