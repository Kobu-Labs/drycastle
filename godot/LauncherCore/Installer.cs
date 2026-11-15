using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Drycastle.LauncherCore;

public enum InstallPhase { DownloadingGame, DownloadingMap, DownloadingPack, Installing }

public readonly record struct InstallProgress(InstallPhase Phase, long Done, long Total);

/// <summary>
/// Installs a manifest under a root folder: <c>game/</c> (exe, pck, assets, with <c>game/map/</c> inside), <c>installed.json</c>.
/// Archives are downloaded to <c>dl/</c>, checked against the manifest sha256, extracted to a staging folder and swapped in, so a failed or
/// cancelled update leaves the previous install playable. The game zip is skipped when the version is unchanged, the map zip when the map id is.
/// <para>
/// The private asset pack (<c>game/assets-private/</c>, <see cref="PackRef"/>) is optional and never blocks the game: it is fetched only with a
/// pack password, kept across game updates like the map, and when it fails (wrong password, host down) the game and map are still installed
/// and a <see cref="PackException"/> is thrown afterwards.
/// </para>
/// </summary>
public sealed class Installer
{
    readonly HttpClient _http;
    public string Root { get; }
    public string GameDir => Path.Combine(Root, "game");
    string StatePath => Path.Combine(Root, "installed.json");
    string Dl => Path.Combine(Root, "dl");
    string Staging => Path.Combine(Root, "staging");
    /// <summary>The folder the game reads the private pack from (next to the executable; next to the .app on macOS).</summary>
    public const string PackFolder = "assets-private";
    public string PackDir => Path.Combine(GameDir, PackFolder);

    public Installer(HttpClient http, string root) { _http = http; Root = root; }

    /// <summary>%LOCALAPPDATA%/Drycastle, ~/Library/Application Support/Drycastle on macOS (.NET would give ~/.local/share there), ~/.local/share/Drycastle on Linux.</summary>
    public static string DefaultRoot() => Path.Combine(
        OperatingSystem.IsMacOS()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support")
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Drycastle");

    public InstalledState? Installed()
    {
        try
        {
            var s = JsonSerializer.Deserialize<InstalledState>(File.ReadAllText(StatePath));
            return s != null && File.Exists(ExePath(s)) ? s : null;
        }
        catch { return null; }
    }

    public string ExePath(InstalledState s) => Path.Combine(GameDir, s.Exe);

    /// <summary>Which downloads an update needs, in bytes (0 = nothing to do).</summary>
    public static (bool Game, bool Map, long Bytes) Plan(InstalledState? have, Manifest want, bool mapDirExists)
    {
        bool game = have is null || have.Version != want.Version;
        bool map = have is null || have.MapId != want.Map.Id || !mapDirExists;
        return (game, map, (game ? want.Game.Size : 0) + (map ? want.Map.File.Size : 0));
    }

    /// <summary>Whether the private asset pack has to be downloaded: the release has one, a password is set, and it is not the installed one.</summary>
    public static bool PlanPack(InstalledState? have, Manifest want, string? password, bool packDirExists) =>
        want.Pack != null && !string.IsNullOrWhiteSpace(password) && (have?.PackId != want.Pack.Id || !packDirExists);

    /// <summary>The pack of this release is installed.</summary>
    public bool PackInstalled(Manifest want) => want.Pack != null && Installed()?.PackId == want.Pack.Id && Directory.Exists(PackDir);

    /// <summary>Removes leftovers of an interrupted update and restores game/ from game.old/ if the swap was cut in half.</summary>
    public void Recover()
    {
        Directory.CreateDirectory(Root);
        var old = GameDir + ".old";
        if (Directory.Exists(old))
        {
            if (!Directory.Exists(GameDir)) Directory.Move(old, GameDir); else Directory.Delete(old, true);
        }
        foreach (var d in new[] { Staging, GameDir + ".newmap", GameDir + ".newpack" }) if (Directory.Exists(d)) Directory.Delete(d, true);
    }

    public async Task InstallAsync(Manifest m, IProgress<InstallProgress> progress, CancellationToken ct, string? packPassword = null)
    {
        await Task.Run(Recover, CancellationToken.None);
        var have = Installed();
        var (needGame, needMap, _) = Plan(have, m, Directory.Exists(Path.Combine(GameDir, "map")));
        var needPack = PlanPack(have, m, packPassword, Directory.Exists(PackDir));
        if (!needGame && !needMap && !needPack) return;
        Directory.CreateDirectory(Dl);
        PackException? packError = null;
        try
        {
            string? gameZip = needGame ? await FetchAsync(m.Game, InstallPhase.DownloadingGame, progress, ct) : null;
            string? mapZip = needMap ? await FetchAsync(m.Map.File, InstallPhase.DownloadingMap, progress, ct) : null;
            string? packZip = null;
            if (needPack)
            {
                // The pack never blocks the game: its failure is kept and thrown after the game and map are in place.
                try
                {
                    var enc = await FetchAsync(m.Pack!.File, InstallPhase.DownloadingPack, progress, ct);
                    packZip = await Task.Run(() => Unlock(enc, m.Pack, packPassword!.Trim()), ct);
                }
                catch (PackException e) { packError = e; }
                catch (Exception e) when (e is HttpRequestException or InvalidDataException or IOException)
                {
                    packError = new PackException(e.Message, false, e);
                }
                if (packZip == null && !needGame && !needMap) throw packError!;
            }
            // Point of no return: the Installing report tells the UI to stop offering Cancel (LauncherFlow.CanCancel). This is the last check of ct;
            // Apply swaps folders and runs to the end (or rolls back on its own failure), so it never takes the token.
            progress.Report(new InstallProgress(InstallPhase.Installing, 0, 1));
            ct.ThrowIfCancellationRequested();
            await Task.Run(() => Apply(m, have, gameZip, mapZip, packZip), CancellationToken.None);
            progress.Report(new InstallProgress(InstallPhase.Installing, 1, 1));
            if (packError != null) throw packError;
        }
        finally
        {
            if (Directory.Exists(Dl)) try { Directory.Delete(Dl, true); } catch { }
            if (Directory.Exists(Staging)) try { Directory.Delete(Staging, true); } catch { }
            foreach (var d in new[] { GameDir + ".newmap", GameDir + ".newpack" })
                if (Directory.Exists(d)) try { Directory.Delete(d, true); } catch { }
        }
    }

    /// <summary>Decrypts the downloaded pack and proves the password by the checksum of the zip inside. Returns the zip.</summary>
    string Unlock(string enc, PackRef pack, string password)
    {
        var zip = Path.Combine(Dl, "pack.zip");
        PackCrypto.Decrypt(enc, zip, password, pack.Iterations);
        using var f = File.OpenRead(zip);
        var got = Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant();
        if (!string.Equals(got, pack.PlainSha256, StringComparison.OrdinalIgnoreCase)) throw new PackException("wrong asset pack password", true);
        return zip;
    }

    void Apply(Manifest m, InstalledState? have, string? gameZip, string? mapZip, string? packZip)
    {
        var newMap = GameDir + ".newmap";
        var newPack = GameDir + ".newpack";
        if (mapZip != null) ExtractSafe(mapZip, newMap);
        if (packZip != null) ExtractSafe(packZip, newPack);
        if (gameZip != null)
        {
            ExtractSafe(gameZip, Staging);
            if (!File.Exists(Path.Combine(Staging, m.Exe))) throw new InvalidDataException($"game archive does not contain {m.Exe}");
            var stagedMap = Path.Combine(Staging, "map");
            if (Directory.Exists(stagedMap)) Directory.Delete(stagedMap, true);
            var stagedPack = Path.Combine(Staging, PackFolder);
            if (Directory.Exists(stagedPack)) Directory.Delete(stagedPack, true);
            var old = GameDir + ".old";
            var hadGame = Directory.Exists(GameDir);
            if (mapZip != null) Directory.Move(newMap, stagedMap);
            if (packZip != null) Directory.Move(newPack, stagedPack);
            // Swap with rollback: a failure (for example a locked exe) puts the old install, map included, back.
            try
            {
                if (hadGame) Directory.Move(GameDir, old);
                var oldMap = Path.Combine(old, "map");
                if (mapZip == null && Directory.Exists(oldMap)) Directory.Move(oldMap, stagedMap);
                var oldPack = Path.Combine(old, PackFolder);
                if (packZip == null && Directory.Exists(oldPack)) Directory.Move(oldPack, stagedPack); // the pack outlives game updates, like the map
                Directory.Move(Staging, GameDir);
            }
            catch
            {
                if (mapZip == null && Directory.Exists(stagedMap) && Directory.Exists(old)) try { Directory.Move(stagedMap, Path.Combine(old, "map")); } catch { }
                if (packZip == null && Directory.Exists(stagedPack) && Directory.Exists(old)) try { Directory.Move(stagedPack, Path.Combine(old, PackFolder)); } catch { }
                if (Directory.Exists(old) && !Directory.Exists(GameDir)) try { Directory.Move(old, GameDir); } catch { }
                throw;
            }
            if (Directory.Exists(old)) Directory.Delete(old, true);
        }
        else
        {
            if (mapZip != null)
            {
                var map = Path.Combine(GameDir, "map");
                if (Directory.Exists(map)) Directory.Delete(map, true);
                Directory.Move(newMap, map);
            }
            if (packZip != null)
            {
                if (Directory.Exists(PackDir)) Directory.Delete(PackDir, true);
                Directory.Move(newPack, PackDir);
            }
        }
        if (!File.Exists(Path.Combine(GameDir, m.Exe))) throw new InvalidDataException($"game archive does not contain {m.Exe}");
        var packId = packZip != null ? m.Pack!.Id : Directory.Exists(PackDir) ? have?.PackId : null;
        File.WriteAllText(StatePath, JsonSerializer.Serialize(new InstalledState(m.Version, m.Map.Id, m.Exe, m.Channel, m.Protocol, packId)));
    }

    async Task<string> FetchAsync(FileRef f, InstallPhase phase, IProgress<InstallProgress> progress, CancellationToken ct)
    {
        var path = Path.Combine(Dl, Path.GetFileName(f.Name));
        using var resp = await _http.GetAsync(f.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? f.Size;
        using var sha = SHA256.Create();
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(path))
        {
            var buf = new byte[81920];
            long done = 0;
            int n;
            long lastReport = 0;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                sha.TransformBlock(buf, 0, n, null, 0);
                done += n;
                if (done - lastReport >= 256 * 1024 || done == total) { lastReport = done; progress.Report(new InstallProgress(phase, done, total)); }
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        }
        var got = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
        if (!string.Equals(got, f.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{f.Name} failed its checksum (expected {f.Sha256[..Math.Min(12, f.Sha256.Length)]}, got {got[..12]}); the download is corrupt, try again");
        return path;
    }

    /// <summary>Extracts a zip, refusing entries that would land outside the target (zip-slip).</summary>
    public static void ExtractSafe(string zip, string target)
    {
        if (Directory.Exists(target)) Directory.Delete(target, true);
        Directory.CreateDirectory(target);
        var root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        using var a = ZipFile.OpenRead(zip);
        foreach (var e in a.Entries)
        {
            var dest = Path.GetFullPath(Path.Combine(target, e.FullName));
            if (!dest.StartsWith(root, StringComparison.Ordinal)) throw new InvalidDataException($"unsafe path in archive: {e.FullName}");
            if (e.FullName.EndsWith('/')) { Directory.CreateDirectory(dest); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            e.ExtractToFile(dest, true);
        }
    }
}
