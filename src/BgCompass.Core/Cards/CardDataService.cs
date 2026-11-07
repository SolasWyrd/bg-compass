namespace BgCompass.Core.Cards;

/// <summary>
/// Keeps the card catalog in step with the installed game. Order of preference: the exact build the log
/// reports, then "latest" (flagged when it cannot be matched), then the disk cache, then the embedded snapshot.
/// </summary>
public sealed class CardDataService(string cacheDir, HttpClient http)
{
    const string Api = "https://api.hearthstonejson.com/v1";
    string CachePath => Path.Combine(cacheDir, "cards.cache.json.gz");

    public CardCatalog Current { get; private set; } = CardCatalog.LoadEmbedded();

    /// <summary>Set when the data in use is known not to match the running game, or could not be refreshed.</summary>
    public string? Warning { get; private set; }

    public event Action? Changed;

    public void LoadCached()
    {
        try
        {
            if (!File.Exists(CachePath)) return;
            using var f = File.OpenRead(CachePath);
            var cached = CardCatalog.Load(f);
            if (cached.Version.FetchedAt >= Current.Version.FetchedAt) Current = cached;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            Warning = "Кэш карт повреждён, используется встроенный снимок.";
        }
    }

    /// <summary>Call with the log's BuildNumber when known. Never throws for network trouble; sets <see cref="Warning"/>.</summary>
    public async Task RefreshAsync(int? gameBuild, CancellationToken ct = default)
    {
        if (gameBuild is int b && Current.Version.Build == b)
        {
            Warning = null;
            return;
        }

        var exact = gameBuild is int build && await TryDownloadAsync(build.ToString(), build, ct);
        if (!exact && !await TryDownloadAsync("latest", null, ct))
        {
            Warning = Describe(gameBuild, "Не удалось обновить карты");
            Changed?.Invoke();
            return;
        }
        Warning = exact || gameBuild is null ? null : Describe(gameBuild, "Для вашей сборки игры данных пока нет, используется последняя версия");
        Changed?.Invoke();
    }

    string Describe(int? gameBuild, string prefix) =>
        $"{prefix}: данные от {Current.Version.FetchedAt:yyyy-MM-dd}"
        + (Current.Version.Build is int b ? $", сборка {b}" : "")
        + (gameBuild is int g ? $"; игра — сборка {g}." : ".");

    async Task<bool> TryDownloadAsync(string folder, int? build, CancellationToken ct)
    {
        try
        {
            using var en = await GetAsync($"{Api}/{folder}/enUS/cards.json", ct);
            using var ru = await GetAsync($"{Api}/{folder}/ruRU/cards.json", ct);
            if (en is null || ru is null) return false;
            var catalog = CardCatalog.FromHearthstoneJson(en, ru, new CardDataVersion(build, DateTimeOffset.UtcNow, $"HearthstoneJSON/{folder}"));
            if (catalog.PoolMinions.Count == 0) return false;
            Directory.CreateDirectory(cacheDir);
            var tmp = CachePath + ".tmp";
            await using (var f = File.Create(tmp)) catalog.Save(f);
            File.Move(tmp, CachePath, overwrite: true);
            Current = catalog;
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return false;
        }
    }

    async Task<Stream?> GetAsync(string url, CancellationToken ct)
    {
        var r = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!r.IsSuccessStatusCode) return null;
        var ms = new MemoryStream();
        await r.Content.CopyToAsync(ms, ct);
        ms.Position = 0;
        return ms;
    }
}
