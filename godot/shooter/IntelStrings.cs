namespace Drycastle.Shooter;

/// <summary>English strings of the event announcement and the intel screen at the base. Registered with <see cref="Strings.Register"/>. Keys are prefixed intel.</summary>
public static class IntelStrings
{
    public static readonly (string Key, string Text)[] Table =
    {
        // the announcement (a toast and a chat line; the village loudspeaker plays with it)
        ("intel.announce", "Suspicious activity detected. Report to the base for more information."),
        ("intel.announce.at", "Suspicious activity detected. Report to the base, grid {0}, for more information."),
        // the screen
        ("intel.title", "INTEL BRIEFING"),
        ("intel.when.upcoming", "BEGINS IN"),
        ("intel.when.underway", "STATUS"),
        ("intel.status.underway", "UNDER WAY"),
        ("intel.where", "WHERE"),
        ("intel.where.grid", "GRID {0}"),
        ("intel.where.none", "NO FIXED LOCATION"),
        ("intel.where.nomap", "MAP UNAVAILABLE"),
        ("intel.risks", "RISKS"),
        ("intel.risks.none", "None reported"),
        ("intel.test", "TEST - NOT A REAL EVENT"),
        ("intel.empty.title", "NO ACTIVITY REPORTED"),
        ("intel.empty.body", "All quiet in the village. When suspicious activity is detected, the details appear here."),
        ("intel.map.north", "N"),
        ("intel.map.nolocation", "NO FIXED LOCATION"),
        ("intel.tab", "{0}  {1}"),
        ("intel.tab.active", ">  {0}  {1}"),
    };
}
