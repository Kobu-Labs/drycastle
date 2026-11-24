using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Drycastle.LauncherCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Launcher;

public class LauncherTests : IDisposable
{
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "launcher-test-" + Guid.NewGuid().ToString("N"));
    public LauncherTests() => Directory.CreateDirectory(_tmp);
    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

    sealed class FakeHandler : HttpMessageHandler
    {
        public readonly Dictionary<string, byte[]> Files = new();
        public readonly List<string> Requests = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Requests.Add(r.RequestUri!.ToString());
            return Task.FromResult(Files.TryGetValue(r.RequestUri!.ToString(), out var b)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(b) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    static byte[] Zip(params (string name, string text)[] files)
    {
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (n, t) in files) { using var w = new StreamWriter(z.CreateEntry(n).Open()); w.Write(t); }
        return ms.ToArray();
    }

    static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    static Manifest Make(string version, string mapId, byte[] game, byte[] map) => new(2, version, "dev", "abc", 17,
        new MapRef(mapId, new FileRef("map.zip", "https://x/" + mapId + "/map.zip", Sha(map), map.Length)))
    {
        Game = new FileRef("game.zip", "https://x/" + version + "/game.zip", Sha(game), game.Length),
        Exe = "game.exe",
    };

    static Installer Inst(FakeHandler h, string root) => new(new HttpClient(h), root);
    sealed class Sink : IProgress<InstallProgress> { public readonly List<InstallProgress> Seen = new(); public void Report(InstallProgress p) => Seen.Add(p); }

    [Fact]
    public async Task Fresh_install_downloads_both_and_writes_state()
    {
        var g = Zip(("game.exe", "v1"), ("assets/a.txt", "a")); var m = Zip(("manifest.json", "{}"));
        var man = Make("dev-1", "map-1", g, m);
        var h = new FakeHandler(); h.Files[man.Game.Url] = g; h.Files[man.Map.File.Url] = m;
        var inst = Inst(h, _tmp);
        await inst.InstallAsync(man, new Sink(), default);
        var s = inst.Installed();
        Assert.Equal("dev-1", s!.Version);
        Assert.True(File.Exists(Path.Combine(inst.GameDir, "assets/a.txt")));
        Assert.True(File.Exists(Path.Combine(inst.GameDir, "map/manifest.json")));
        Assert.False(Directory.Exists(Path.Combine(_tmp, "dl")));
    }

    [Fact]
    public async Task Game_update_keeps_map_and_skips_its_download()
    {
        var g1 = Zip(("game.exe", "v1")); var g2 = Zip(("game.exe", "v2")); var m = Zip(("manifest.json", "{}"));
        var man1 = Make("dev-1", "map-1", g1, m); var man2 = Make("dev-2", "map-1", g2, m);
        var h = new FakeHandler(); h.Files[man1.Game.Url] = g1; h.Files[man2.Game.Url] = g2; h.Files[man1.Map.File.Url] = m;
        var inst = Inst(h, _tmp);
        await inst.InstallAsync(man1, new Sink(), default);
        h.Requests.Clear();
        await inst.InstallAsync(man2, new Sink(), default);
        Assert.Equal("v2", File.ReadAllText(Path.Combine(inst.GameDir, "game.exe")));
        Assert.True(File.Exists(Path.Combine(inst.GameDir, "map/manifest.json")));
        Assert.DoesNotContain(h.Requests, r => r.Contains("map.zip"));
        Assert.Equal("dev-2", inst.Installed()!.Version);
    }

    [Fact]
    public async Task Map_only_update_keeps_game()
    {
        var g = Zip(("game.exe", "v1")); var m1 = Zip(("manifest.json", "one")); var m2 = Zip(("manifest.json", "two"));
        var man1 = Make("dev-1", "map-1", g, m1); var man2 = Make("dev-1", "map-2", g, m2);
        var h = new FakeHandler(); h.Files[man1.Game.Url] = g; h.Files[man1.Map.File.Url] = m1; h.Files[man2.Map.File.Url] = m2;
        var inst = Inst(h, _tmp);
        await inst.InstallAsync(man1, new Sink(), default);
        h.Requests.Clear();
        await inst.InstallAsync(man2, new Sink(), default);
        Assert.Equal("two", File.ReadAllText(Path.Combine(inst.GameDir, "map/manifest.json")));
        Assert.DoesNotContain(h.Requests, r => r.Contains("game.zip"));
        Assert.Equal("map-2", inst.Installed()!.MapId);
    }

    [Fact]
    public async Task Bad_checksum_fails_and_keeps_previous_install()
    {
        var g1 = Zip(("game.exe", "v1")); var g2 = Zip(("game.exe", "v2")); var m = Zip(("manifest.json", "{}"));
        var man1 = Make("dev-1", "map-1", g1, m); var man2 = Make("dev-2", "map-1", g2, m);
        var h = new FakeHandler(); h.Files[man1.Game.Url] = g1; h.Files[man1.Map.File.Url] = m; h.Files[man2.Game.Url] = Zip(("game.exe", "tampered"));
        var inst = Inst(h, _tmp);
        await inst.InstallAsync(man1, new Sink(), default);
        await Assert.ThrowsAsync<InvalidDataException>(() => inst.InstallAsync(man2, new Sink(), default));
        Assert.Equal("dev-1", inst.Installed()!.Version);
        Assert.Equal("v1", File.ReadAllText(Path.Combine(inst.GameDir, "game.exe")));
    }

    [Fact]
    public async Task Cancel_leaves_previous_install_playable()
    {
        var g1 = Zip(("game.exe", "v1")); var m = Zip(("manifest.json", "{}")); var g2 = Zip(("game.exe", "v2"));
        var man1 = Make("dev-1", "map-1", g1, m); var man2 = Make("dev-2", "map-1", g2, m);
        var h = new FakeHandler(); h.Files[man1.Game.Url] = g1; h.Files[man1.Map.File.Url] = m; h.Files[man2.Game.Url] = g2;
        var inst = Inst(h, _tmp);
        await inst.InstallAsync(man1, new Sink(), default);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inst.InstallAsync(man2, new Sink(), cts.Token));
        Assert.Equal("dev-1", inst.Installed()!.Version);
    }

    [Fact]
    public void Zip_slip_is_rejected()
    {
        var zip = Path.Combine(_tmp, "evil.zip");
        File.WriteAllBytes(zip, Zip(("../escaped.txt", "x")));
        Assert.Throws<InvalidDataException>(() => Installer.ExtractSafe(zip, Path.Combine(_tmp, "out")));
        Assert.False(File.Exists(Path.Combine(_tmp, "escaped.txt")));
    }

    [Fact]
    public void Recover_restores_interrupted_swap()
    {
        var inst = Inst(new FakeHandler(), _tmp);
        Directory.CreateDirectory(inst.GameDir + ".old"); File.WriteAllText(Path.Combine(inst.GameDir + ".old", "game.exe"), "old");
        Directory.CreateDirectory(Path.Combine(_tmp, "staging"));
        inst.Recover();
        Assert.True(File.Exists(Path.Combine(inst.GameDir, "game.exe")));
        Assert.False(Directory.Exists(Path.Combine(_tmp, "staging")));
    }

    [Fact]
    public void Plan_reports_what_to_download()
    {
        var man = Make("dev-2", "map-1", new byte[10], new byte[100]);
        Assert.Equal((true, true, 110L), Installer.Plan(null, man, false));
        var have = new InstalledState("dev-1", "map-1", "game.exe", "dev", 17);
        Assert.Equal((true, false, 10L), Installer.Plan(have, man, true));
        Assert.Equal((false, false, 0L), Installer.Plan(have with { Version = "dev-2" }, man, true));
        Assert.Equal((false, true, 100L), Installer.Plan(have with { Version = "dev-2" }, man, false));
    }

    [Fact]
    public void Feed_picks_newest_by_channel_and_ignores_map_releases()
    {
        const string json = """
        [
         {"tag_name":"map-1","prerelease":true,"draft":false,"published_at":"2026-11-12T00:00:00Z","assets":[{"name":"map.zip","browser_download_url":"u"}]},
         {"tag_name":"dev-9","prerelease":true,"draft":false,"published_at":"2026-11-11T00:00:00Z","assets":[{"name":"manifest.json","browser_download_url":"dev9"}]},
         {"tag_name":"v1.0.0","prerelease":false,"draft":false,"published_at":"2026-11-10T00:00:00Z","assets":[{"name":"manifest.json","browser_download_url":"v1"}]},
         {"tag_name":"dev-10","prerelease":true,"draft":true,"published_at":"2026-11-13T00:00:00Z","assets":[{"name":"manifest.json","browser_download_url":"d10"}]}
        ]
        """;
        var rel = ReleaseFeed.ParseReleases(json);
        Assert.Equal("v1.0.0", ReleaseFeed.Pick(rel, "stable")!.Tag);
        Assert.Equal("dev-9", ReleaseFeed.Pick(rel, "dev")!.Tag);
        Assert.Null(ReleaseFeed.Pick(new List<ReleaseInfo>(), "stable"));
    }

    [Fact]
    public void Manifest_validation()
    {
        const string ok = """
        {"schema":2,"version":"dev-1","channel":"dev","commit":"c","protocol":17,"map":{"id":"m","file":{"name":"m","url":"u","sha256":"s","size":1}},
        "platforms":{"windows":{"exe":"g.exe","game":{"name":"g","url":"u","sha256":"s","size":1}},"linux":{"exe":"g.x86_64","game":{"name":"gl","url":"ul","sha256":"s","size":2}},"macos":{"exe":"G.app/Contents/MacOS/G","game":{"name":"gm","url":"um","sha256":"s","size":3}}}}
        """;
        Assert.Equal("dev-1", Manifest.Parse(ok, "windows").Version);
        Assert.Equal("g.exe", Manifest.Parse(ok, "windows").Exe);
        var lin = Manifest.Parse(ok, "linux");
        Assert.Equal("g.x86_64", lin.Exe);
        Assert.Equal("ul", lin.Game.Url);
        var mac = Manifest.Parse(ok, Manifest.MacOs);
        Assert.Equal("G.app/Contents/MacOS/G", mac.Exe);
        Assert.Equal("um", mac.Game.Url);
        Assert.Throws<FormatException>(() => Manifest.Parse(ok, "freebsd"));
        Assert.Throws<FormatException>(() => Manifest.Parse(ok.Replace("\"schema\":2", "\"schema\":99"), "windows"));
        Assert.Throws<FormatException>(() => Manifest.Parse(ok.Replace("\"g.exe\"", "\"../g.exe\""), "windows"));
    }

    [Fact]
    public void Settings_survive_garbage()
    {
        File.WriteAllText(Path.Combine(_tmp, "settings.json"), "{{{");
        Assert.Equal("stable", LauncherSettings.Load(_tmp).Channel);
        new LauncherSettings { Channel = "dev" }.Save(_tmp);
        Assert.Equal("dev", LauncherSettings.Load(_tmp).Channel);
    }

    [Fact]
    public async Task Game_zip_without_exe_keeps_previous_install()
    {
        var g1 = Zip(("game.exe", "v1")); var g2 = Zip(("other.txt", "x")); var m = Zip(("manifest.json", "{}"));
        var man1 = Make("dev-1", "map-1", g1, m); var man2 = Make("dev-2", "map-1", g2, m);
        var h = new FakeHandler(); h.Files[man1.Game.Url] = g1; h.Files[man1.Map.File.Url] = m; h.Files[man2.Game.Url] = g2;
        var inst = Inst(h, _tmp);
        await inst.InstallAsync(man1, new Sink(), default);
        await Assert.ThrowsAsync<InvalidDataException>(() => inst.InstallAsync(man2, new Sink(), default));
        Assert.Equal("dev-1", inst.Installed()!.Version);
        Assert.True(File.Exists(Path.Combine(inst.GameDir, "map/manifest.json")));
    }

    [Fact]
    public void Manifest_without_map_file_is_rejected_and_scale_is_clamped()
    {
        const string bad = """{"schema":2,"version":"v","channel":"dev","commit":"c","protocol":1,"map":{"id":"m"},"platforms":{"windows":{"exe":"g.exe","game":{"name":"g","url":"u","sha256":"s","size":1}}}}""";
        Assert.Throws<FormatException>(() => Manifest.Parse(bad, "windows"));
        File.WriteAllText(Path.Combine(_tmp, "settings.json"), "{\"UiScale\":0}");
        Assert.Equal(75, LauncherSettings.Load(_tmp).UiScale);
    }

    [Fact]
    public void Feed_parses_the_single_release_object_of_releases_latest()
    {
        const string one = """{"tag_name":"v1.2.0","prerelease":false,"draft":false,"published_at":"2026-11-10T00:00:00Z","assets":[{"name":"manifest.json","browser_download_url":"m"}]}""";
        Assert.Equal("v1.2.0", ReleaseFeed.Pick(ReleaseFeed.ParseReleases(one), "stable")!.Tag);
    }

    // ---- private asset pack ----

    const string PackPass = "correct horse";

    /// <summary>The encrypted pack as the publish script makes it (fast key derivation for the tests), and a manifest that names it.</summary>
    (Manifest man, byte[] enc) WithPack(Manifest man, string packId, byte[] packZip, string password = PackPass)
    {
        var plain = Path.Combine(_tmp, "p-" + Guid.NewGuid().ToString("N")); var encPath = plain + ".enc";
        File.WriteAllBytes(plain, packZip);
        PackCrypto.Encrypt(plain, encPath, password, 1000);
        var enc = File.ReadAllBytes(encPath);
        File.Delete(plain); File.Delete(encPath);
        var pack = new PackRef(packId, new FileRef("pack.zip.enc", "http://host/" + packId + ".zip.enc", Sha(enc), enc.Length), Sha(packZip), 1000);
        return (man with { Pack = pack }, enc);
    }

    [Fact]
    public async Task Pack_installs_with_the_password_and_outlives_a_game_update()
    {
        var g1 = Zip(("game.exe", "v1")); var g2 = Zip(("game.exe", "v2")); var m = Zip(("manifest.json", "{}")); var p = Zip(("synty/a.glb", "model"));
        var (man1, enc) = WithPack(Make("dev-1", "map-1", g1, m), "pack-1", p);
        var man2 = Make("dev-2", "map-1", g2, m) with { Pack = man1.Pack };
        var h = new FakeHandler(); h.Files[man1.Game.Url] = g1; h.Files[man2.Game.Url] = g2; h.Files[man1.Map.File.Url] = m; h.Files[man1.Pack!.File.Url] = enc;
        var inst = Inst(h, _tmp);
        var sink = new Sink();
        await inst.InstallAsync(man1, sink, default, PackPass);
        Assert.Equal("model", File.ReadAllText(Path.Combine(inst.PackDir, "synty/a.glb")));
        Assert.Equal("pack-1", inst.Installed()!.PackId);
        Assert.True(inst.PackInstalled(man1));
        Assert.Contains(sink.Seen, x => x.Phase == InstallPhase.DownloadingPack);
        h.Requests.Clear();
        await inst.InstallAsync(man2, new Sink(), default, PackPass);
        Assert.Equal("v2", File.ReadAllText(Path.Combine(inst.GameDir, "game.exe")));
        Assert.Equal("model", File.ReadAllText(Path.Combine(inst.PackDir, "synty/a.glb")));
        Assert.Equal("pack-1", inst.Installed()!.PackId);
        Assert.DoesNotContain(h.Requests, r => r.Contains(".enc"));
        Assert.False(Installer.PlanPack(inst.Installed(), man2, PackPass, true));
    }

    [Fact]
    public async Task Wrong_pack_password_still_installs_the_game()
    {
        var g = Zip(("game.exe", "v1")); var m = Zip(("manifest.json", "{}")); var p = Zip(("synty/a.glb", "model"));
        var (man, enc) = WithPack(Make("dev-1", "map-1", g, m), "pack-1", p);
        var h = new FakeHandler(); h.Files[man.Game.Url] = g; h.Files[man.Map.File.Url] = m; h.Files[man.Pack!.File.Url] = enc;
        var inst = Inst(h, _tmp);
        var e = await Assert.ThrowsAsync<PackException>(() => inst.InstallAsync(man, new Sink(), default, "not the password"));
        Assert.True(e.WrongPassword);
        Assert.Equal("dev-1", inst.Installed()!.Version);
        Assert.Null(inst.Installed()!.PackId);
        Assert.False(Directory.Exists(inst.PackDir));
        Assert.False(Directory.Exists(Path.Combine(_tmp, "dl")));
        // the right password afterwards fetches the pack alone
        h.Requests.Clear();
        await inst.InstallAsync(man, new Sink(), default, PackPass);
        Assert.True(inst.PackInstalled(man));
        Assert.DoesNotContain(h.Requests, r => r.Contains("game.zip") || r.Contains("map.zip"));
    }

    [Fact]
    public async Task Without_a_password_the_pack_is_not_requested()
    {
        var g = Zip(("game.exe", "v1")); var m = Zip(("manifest.json", "{}"));
        var (man, enc) = WithPack(Make("dev-1", "map-1", g, m), "pack-1", Zip(("a", "b")));
        var h = new FakeHandler(); h.Files[man.Game.Url] = g; h.Files[man.Map.File.Url] = m; h.Files[man.Pack!.File.Url] = enc;
        var inst = Inst(h, _tmp);
        await inst.InstallAsync(man, new Sink(), default, "  ");
        Assert.Equal("dev-1", inst.Installed()!.Version);
        Assert.DoesNotContain(h.Requests, r => r.Contains(".enc"));
        Assert.False(inst.PackInstalled(man));
    }

    [Fact]
    public async Task Pack_host_down_or_corrupt_download_still_installs_the_game()
    {
        var g = Zip(("game.exe", "v1")); var m = Zip(("manifest.json", "{}"));
        var (man, enc) = WithPack(Make("dev-1", "map-1", g, m), "pack-1", Zip(("a", "b")));
        var h = new FakeHandler(); h.Files[man.Game.Url] = g; h.Files[man.Map.File.Url] = m; // the pack url answers 404
        var inst = Inst(h, _tmp);
        var e = await Assert.ThrowsAsync<PackException>(() => inst.InstallAsync(man, new Sink(), default, PackPass));
        Assert.False(e.WrongPassword);
        Assert.Equal("dev-1", inst.Installed()!.Version);
        enc[^1] ^= 1; h.Files[man.Pack!.File.Url] = enc; // a damaged download fails its checksum, not the password
        e = await Assert.ThrowsAsync<PackException>(() => inst.InstallAsync(man, new Sink(), default, PackPass));
        Assert.False(e.WrongPassword);
        Assert.False(Directory.Exists(inst.PackDir));
    }

    [Fact]
    public async Task New_pack_replaces_the_old_one()
    {
        var g = Zip(("game.exe", "v1")); var m = Zip(("manifest.json", "{}"));
        var (man1, enc1) = WithPack(Make("dev-1", "map-1", g, m), "pack-1", Zip(("old.glb", "1")));
        var (man2, enc2) = WithPack(Make("dev-1", "map-1", g, m), "pack-2", Zip(("new.glb", "2")));
        var h = new FakeHandler(); h.Files[man1.Game.Url] = g; h.Files[man1.Map.File.Url] = m; h.Files[man1.Pack!.File.Url] = enc1; h.Files[man2.Pack!.File.Url] = enc2;
        var inst = Inst(h, _tmp);
        await inst.InstallAsync(man1, new Sink(), default, PackPass);
        Assert.True(Installer.PlanPack(inst.Installed(), man2, PackPass, true));
        await inst.InstallAsync(man2, new Sink(), default, PackPass);
        Assert.True(File.Exists(Path.Combine(inst.PackDir, "new.glb")));
        Assert.False(File.Exists(Path.Combine(inst.PackDir, "old.glb")));
        Assert.Equal("pack-2", inst.Installed()!.PackId);
    }

    [Fact]
    public void Manifest_pack_entry_is_optional_and_validated()
    {
        const string head = "{\"schema\":2,\"version\":\"v1\",\"channel\":\"stable\",\"commit\":\"c\",\"protocol\":1,"
            + "\"platforms\":{\"linux\":{\"exe\":\"g\",\"game\":{\"name\":\"g.zip\",\"url\":\"u\",\"sha256\":\"s\",\"size\":1}}},"
            + "\"map\":{\"id\":\"m\",\"file\":{\"name\":\"map.zip\",\"url\":\"u\",\"sha256\":\"s\",\"size\":1}}";
        Assert.Null(Manifest.Parse(head + "}", "linux").Pack);
        var pack = Manifest.Parse(head + ",\"pack\":{\"id\":\"p1\",\"plain_sha256\":\"ab\",\"iterations\":5,\"file\":{\"name\":\"p.enc\",\"url\":\"http://h/p.enc\",\"sha256\":\"s\",\"size\":9}}}", "linux").Pack;
        Assert.Equal(("p1", "ab", 5, 9L), (pack!.Id, pack.PlainSha256, pack.Iterations, pack.File.Size));
        Assert.Throws<FormatException>(() => Manifest.Parse(head + ",\"pack\":{\"id\":\"p1\",\"file\":{\"name\":\"p.enc\",\"url\":\"\",\"sha256\":\"s\",\"size\":9}}}", "linux"));
    }

    [Fact]
    public void Settings_keep_the_pack_password()
    {
        new LauncherSettings { PackPassword = "  secret " }.Save(_tmp);
        Assert.Equal("secret", LauncherSettings.Load(_tmp).PackPassword);
    }
}
