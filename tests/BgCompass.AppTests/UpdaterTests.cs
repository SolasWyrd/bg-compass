using System.Net;
using System.Security.Cryptography;
using System.Text;
using BgCompass.App.Runtime;
using Xunit;

namespace BgCompass.AppTests;

public sealed class FakeHandler(Func<Uri, (HttpStatusCode, byte[])> respond) : HttpMessageHandler
{
    public List<Uri> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request.RequestUri!);
        var (code, body) = respond(request.RequestUri!);
        return Task.FromResult(new HttpResponseMessage(code) { Content = new ByteArrayContent(body) });
    }
}

public sealed class UpdaterTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "bgc-upd-" + Guid.NewGuid().ToString("N")[..8]);
    string Exe => Path.Combine(_dir, "BgCompass.exe");

    public UpdaterTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    static string ReleaseJson(string tag, bool withHash = true, bool draft = false) => $$"""
        {"tag_name":"{{tag}}","draft":{{(draft ? "true" : "false")}},"prerelease":false,"assets":[
          {"name":"BgCompass.exe","browser_download_url":"https://example.test/BgCompass.exe"}
          {{(withHash ? ",{\"name\":\"BgCompass.exe.sha256\",\"browser_download_url\":\"https://example.test/BgCompass.exe.sha256\"}" : "")}}
        ]}
        """;

    [Theory]
    [InlineData("v0.2.0", true)]
    [InlineData("0.1.1", true)]
    [InlineData("v0.1.0", false)]
    [InlineData("v0.0.9", false)]
    [InlineData("v1.0", true)]
    public void Tag_is_compared_numerically(string tag, bool newer)
    {
        var v = UpdateLogic.ParseTag(tag);
        Assert.NotNull(v);
        Assert.Equal(newer, UpdateLogic.IsNewer(v, new Version(0, 1, 0, 0)));
    }

    [Fact] public void Ten_is_greater_than_nine() =>
        Assert.True(UpdateLogic.IsNewer(UpdateLogic.ParseTag("v0.10.0")!, UpdateLogic.ParseTag("v0.9.0")!));

    [Theory]
    [InlineData("")] [InlineData("latest")] [InlineData("v")] [InlineData("v1")] [InlineData("v1.x.0")] [InlineData("v1.0.0-beta")] [InlineData("v-1.0")]
    public void Malformed_tag_is_rejected(string tag) => Assert.Null(UpdateLogic.ParseTag(tag));

    [Fact]
    public void Release_with_both_assets_is_selected()
    {
        var r = UpdateLogic.SelectRelease(ReleaseJson("v0.2.0"), new Version(0, 1, 0, 0), out _);
        Assert.NotNull(r);
        Assert.Equal(new Version(0, 2, 0), r.Version);
        Assert.EndsWith("BgCompass.exe.sha256", r.HashUrl.AbsoluteUri);
    }

    [Fact]
    public void Missing_hash_asset_draft_old_tag_and_garbage_mean_no_update()
    {
        var cur = new Version(0, 1, 0, 0);
        Assert.Null(UpdateLogic.SelectRelease(ReleaseJson("v0.2.0", withHash: false), cur, out var reason));
        Assert.Contains("BgCompass.exe.sha256", reason);
        Assert.Null(UpdateLogic.SelectRelease(ReleaseJson("v0.2.0", draft: true), cur, out _));
        Assert.Null(UpdateLogic.SelectRelease(ReleaseJson("v0.1.0"), cur, out _));
        Assert.Null(UpdateLogic.SelectRelease(ReleaseJson("nonsense"), cur, out _));
        Assert.Null(UpdateLogic.SelectRelease("<html>", cur, out _));
    }

    [Fact]
    public void Http_asset_urls_are_ignored() =>
        Assert.Null(UpdateLogic.SelectRelease(ReleaseJson("v0.2.0").Replace("https://", "http://"), new Version(0, 1), out _));

    [Fact]
    public void Hash_file_takes_the_first_token()
    {
        var h = new string('A', 64);
        Assert.Equal(new string('a', 64), UpdateLogic.ParseHash($"{h}  BgCompass.exe\n"));
        Assert.Null(UpdateLogic.ParseHash("zz  BgCompass.exe"));
        Assert.Null(UpdateLogic.ParseHash(""));
    }

    Updater NewUpdater(FakeHandler h, Action<string?>? ready = null) =>
        new(Exe, new Version(0, 1, 0, 0), ready ?? (_ => { }), () => { }, h);

    [Fact]
    public async Task Hash_mismatch_deletes_the_download()
    {
        var body = Encoding.UTF8.GetBytes("new exe");
        using var up = NewUpdater(new FakeHandler(_ => (HttpStatusCode.OK, body)));
        var ok = await up.DownloadVerifiedAsync(new Uri("https://example.test/x"), Exe + ".new", new string('0', 64), default);
        Assert.False(ok);
        Assert.False(File.Exists(Exe + ".new"));
    }

    [Fact]
    public async Task Matching_hash_keeps_the_download()
    {
        var body = Encoding.UTF8.GetBytes("new exe");
        using var up = NewUpdater(new FakeHandler(_ => (HttpStatusCode.OK, body)));
        Assert.True(await up.DownloadVerifiedAsync(new Uri("https://example.test/x"), Exe + ".new", Sha(body), default));
        Assert.Equal(body, File.ReadAllBytes(Exe + ".new"));
    }

    [Fact]
    public void Swap_moves_files_and_rolls_back_when_the_second_move_fails()
    {
        File.WriteAllText(Exe, "old");
        File.WriteAllText(Exe + ".new", "new");
        Updater.Swap(Exe);
        Assert.Equal("new", File.ReadAllText(Exe));
        Assert.Equal("old", File.ReadAllText(Exe + ".old"));
        Assert.False(File.Exists(Exe + ".new"));

        File.Delete(Exe + ".old");
        File.Delete(Exe + ".new"); // no .new: the second move fails
        Assert.ThrowsAny<IOException>(() => Updater.Swap(Exe));
        Assert.Equal("new", File.ReadAllText(Exe));
        Assert.False(File.Exists(Exe + ".old"));
    }

    [Fact]
    public void Leftovers_are_removed()
    {
        File.WriteAllText(Exe + ".old", "x");
        File.WriteAllText(Exe + ".new", "x");
        Updater.CleanLeftovers(Exe);
        Assert.False(File.Exists(Exe + ".old"));
        Assert.False(File.Exists(Exe + ".new"));
    }

    [Fact]
    public async Task Full_cycle_installs_and_reports_the_version()
    {
        var body = Encoding.UTF8.GetBytes("v2 exe");
        File.WriteAllText(Exe, "v1");
        var h = new FakeHandler(u => u.AbsolutePath switch
        {
            "/repos/SolasWyrd/bg-compass/releases/latest" => (HttpStatusCode.OK, Encoding.UTF8.GetBytes(ReleaseJson("v0.2.0"))),
            "/BgCompass.exe.sha256" => (HttpStatusCode.OK, Encoding.UTF8.GetBytes(Sha(body) + "  BgCompass.exe\n")),
            "/BgCompass.exe" => (HttpStatusCode.OK, body),
            _ => (HttpStatusCode.NotFound, []),
        });
        string? ready = null;
        using var up = NewUpdater(h, v => ready = v);
        Assert.Equal("0.2.0", await up.CheckOnceAsync());
        Assert.Equal("0.2.0", ready);
        Assert.Equal(body, File.ReadAllBytes(Exe));
        Assert.Equal("v1", File.ReadAllText(Exe + ".old"));
    }

    [Fact]
    public async Task Offline_or_rate_limited_is_quiet_and_changes_nothing()
    {
        File.WriteAllText(Exe, "v1");
        using var up = NewUpdater(new FakeHandler(_ => (HttpStatusCode.Forbidden, [])));
        Assert.Null(await up.CheckOnceAsync());
        Assert.Equal("v1", File.ReadAllText(Exe));
        Assert.False(File.Exists(Exe + ".old"));
    }

    [Fact]
    public void A_restart_waits_for_this_process_and_drops_the_previous_wait()
    {
        var args = UpdateLogic.RestartArgs(["--speed", "2", "--wait-pid", "111"], 222);
        Assert.Equal(["--speed", "2", "--wait-pid", "222"], args);
    }

    [Fact]
    public void Only_the_published_exe_name_applies() =>
        Assert.False(Updater.IsPublishedExe("/usr/bin/dotnet"));
}
