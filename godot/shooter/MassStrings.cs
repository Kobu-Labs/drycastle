namespace Drycastle.Shooter;

/// <summary>English strings of the Holy Mass: the seat prompts and the toasts. Registered with <see cref="Strings.Register"/>. Keys are prefixed mass.</summary>
public static class MassStrings
{
    public static readonly (string Key, string Text)[] Table =
    {
        ("mass.sit", "{0}  Sit down"),
        ("mass.stand", "{0} or {1}  Stand up"),
        ("mass.bells", "The church bells ring. The Holy Mass is about to begin."),
        ("mass.blessed", "You feel a divine blessing"),
        ("mass.cursed", "You feel cursed"),
    };
}
