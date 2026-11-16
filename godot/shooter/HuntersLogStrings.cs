namespace Drycastle.Shooter;

/// <summary>English strings of the Hunter's Log (#336): the screen, the pause menu entry, the kill feed lines and the toasts. Registered with <see cref="Strings.Register"/>. Keys: log., hunt., bind.hunters_log, weapon.run_over.</summary>
public static class HuntersLogStrings
{
    public static readonly (string Key, string Text)[] Table =
    {
        ("bind.hunters_log", "Hunter's Log (toggle)"),
        ("pause.hunterslog", "Hunter's Log"),
        // frame
        ("log.title.my", "My Log"), ("log.title.other", "{0}'s Log"), ("log.tab.records", "Records"), ("log.tab.hunts", "All hunts"),
        ("log.close", "Close"), ("log.back", "Back"),
        // my log
        ("log.stat.hunts", "Hunts"), ("log.stat.species", "Species"), ("log.stat.heaviest", "Heaviest"), ("log.stat.longest", "Longest shot"), ("log.none", "-"),
        ("log.life.title", "Life List"), ("log.life.count", "{0} of {1}"),
        ("log.tile.best", "Best {0} kg"), ("log.tile.longest", "Longest {0}"), ("log.tile.caught", "Caught {0}"), ("log.tile.todo", "Not hunted yet"),
        ("log.hunts.own", "All my hunts"), ("log.hunts.other", "All {0}'s hunts"),
        ("log.hidden.mark", "?"), ("hunt.hidden.name", "Xxxxxxxx"),
        ("log.book.empty", "No hunts yet. Shoot a deer, a boar or any wild animal. The first kill of each species takes its record."),
        // records
        ("log.rec.heaviest", "Heaviest"), ("log.rec.longest", "Longest shot"), ("log.rec.first", "First caught"), ("log.rec.todo", "Not hunted yet. The first kill takes the record."),
        ("log.rec.open", "All {0} hunts"), ("log.head", "HEAD"),
        // all hunts
        ("log.col.when", "When"), ("log.col.player", "Player"), ("log.col.species", "Species"), ("log.col.weight", "Weight"), ("log.col.distance", "Distance"), ("log.col.weapon", "Weapon"),
        ("log.sort.tip", "Sort by {0}"),
        ("log.filter.species", "Species"), ("log.filter.weapon", "Weapon"), ("log.filter.player", "Player"), ("log.filter.all", "All"), ("log.filter.allplayers", "All players"),
        ("log.filter.mystery", "Mystery hunts"), ("log.filter.clear", "Clear filters"),
        ("log.page.prev", "Prev"), ("log.page.next", "Next"), ("log.page.tip", "Page {0}"), ("log.range", "{0}-{1} of {2}"), ("log.gap", "..."),
        ("log.pill.kg", "Record kg"), ("log.pill.shot", "Record shot"),
        ("log.hit.pick", "Select a hunt to see where the bullet went."), ("log.hit.none", "No hit diagram"),
        ("log.hit.distance", "Distance"), ("log.hit.weapon", "Weapon"), ("log.hit.hunter", "Hunter"), ("log.hit.when", "When"), ("log.hit.open", "{0}'s log"),
        ("log.kg", "{0} kg"), ("log.m", "{0} m"),
        ("log.ago.now", "just now"), ("log.ago.min", "{0} min ago"), ("log.ago.h", "{0} h ago"), ("log.ago.d", "{0} d ago"),
        // states
        ("log.loading", "Loading the log"), ("log.loading.body", "Asking the server."),
        ("log.empty.hunts", "No hunts match"), ("log.empty.hunts.body", "Clear a filter to see more."),
        ("log.empty.book", "No hunts yet"),
        ("log.error", "Could not load the log"), ("log.error.body", "The server did not answer. Check your connection and try again."), ("log.retry", "Retry"),
        ("log.guest", "No log for a guest"), ("log.guest.body", "Hunts are only logged for a player with a profile. Records and All hunts are open to read."),
        ("log.missing", "No such hunter"), ("log.missing.body", "The log has no entry for this player."),
        // species and weapons the log adds
        ("hunt.species.bigfoot", "Bigfoot"), ("hunt.species.unicorn", "Unicorn"), ("hunt.species.scylla", "Scylla"),
        ("weapon.run_over", "Run over"), ("weapon.event", "Event"), ("weapon.other", "Other"),
        // announcements
        ("hunt.tag.record", "NEW RECORD"), ("hunt.tag.first", "FIRST"),
        ("hunt.line.kg", "{0}: {1}, {2} kg"), ("hunt.line.shot", "{0}: {1}, {2} m longest shot"),
        ("hunt.line.first", "{0} hunted a {1} for the first time"), ("hunt.line.first.mystery", "{0} caught {1} for the first time"),
        ("toast.hunt.kg", "New record: {0}, {1} kg"), ("toast.hunt.shot", "New record: {0}, {1} m longest shot"),
        ("toast.hunt.first", "First {0}: added to your Life List"), ("toast.hunt.first.mystery", "You caught {0} for the first time"),
    };
}
