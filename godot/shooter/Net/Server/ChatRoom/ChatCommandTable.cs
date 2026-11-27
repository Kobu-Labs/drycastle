using Drycastle.Shooter.Net.Server.BotRoster;
using Drycastle.Shooter.Net.Server.Cartel;
using Drycastle.Shooter.Net.Server.Fauna;
using Drycastle.Shooter.Net.Server.Gear;
using Drycastle.Shooter.Net.Server.Houses;
using Drycastle.Shooter.Net.Server.Parish;
using Drycastle.Shooter.Net.Server.Playground;
using Drycastle.Shooter.Net.Server.Scuds;
using Drycastle.Shooter.Net.Server.Squads;
using Drycastle.Shooter.Net.Server.Statuses;
using Drycastle.Shooter.Net.Server.VillageRadio;

namespace Drycastle.Shooter.Net.Server.ChatRoom;

/// <summary>Which class answers which chat command. The grammar and the help of each are in <c>ShooterCore/Chat/ChatCommands.cs</c>; a command here and not there never parses.</summary>
internal static class ChatCommandTable
{
    public static void Register(ChatServer chat, AdminCommands admin, BotServer bots, AdminSpawner spawner, WildlifeServer wildlife, SandboxServer sandbox, ItemsServer gear,
        CompanionServer companions, CitizenServer citizens, ChurchServer church, RozhlasServer rozhlas, StatusServer status, ScudServer scud, TeamServer teams, HouseDestructionServer houses, CartelServer cartel, TrapHouseServer trapHouses, RivalCrewServer rivals, DuffelServer duffels, DrillServer drills)
    {
        chat.Register("bots", bots.ChatCommand);
        chat.Register("spawn", spawner.ChatCommand);
        chat.Register("unicorn", wildlife.ChatUnicorn);
        chat.Register("phoenix", wildlife.ChatPhoenix);
        chat.Register("sandbox", sandbox.ChatCommand);
        chat.Register("give", gear.ChatGive);
        chat.Register("companion", companions.ChatCommand);
        chat.Register("time", admin.Time);
        chat.Register("explode", admin.Explode);
        chat.Register("restart", admin.Restart);
        chat.Register("event", admin.Event);
        chat.Register("citizens", citizens.Chat);
        chat.Register("church", church.Chat);
        chat.Register("rozhlas", rozhlas.ChatCommand);
        chat.Register("status", status.ChatCommand);
        chat.Register("scud", scud.Chat);
        chat.Register("squad", teams.ChatSquad);
        chat.Register("carve", houses.ChatCarve);
        chat.Register("houses", houses.ChatHouses);
        chat.Register("rubble", houses.ChatRubble);
        chat.Register("cartel", cartel.Chat);
        chat.Register("traphouse", trapHouses.ChatTrapHouse);
        chat.Register("gunman", rivals.ChatGunman);
        chat.Register("duffel", duffels.ChatCommand);
        chat.Register("drill", drills.Chat);
    }
}
