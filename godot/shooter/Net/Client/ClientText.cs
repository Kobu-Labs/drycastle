using Drycastle.Shooter.Menu;

namespace Drycastle.Shooter.Net.Client;

/// <summary>The names the client shows for ids: the translation when there is one, else the id.</summary>
public static class ClientText
{
    public static string WeaponName(string id) => Translated("weapon." + id, id);

    public static string StreakName(string id) => Translated("streak." + id, id);

    static string Translated(string key, string fallback)
    {
        var t = Strings.T(key);
        return t == key ? fallback : t;
    }
}
