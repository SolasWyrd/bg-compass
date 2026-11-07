using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace BgCompass.Core.Cards;

public sealed record CardInfo(
    string Id,
    int DbfId,
    string Name,
    string NameEn,
    string Text,
    string TextEn,
    string Type,
    int? Tier,
    int? Attack,
    int? Health,
    int? Armor,
    int? Cost,
    IReadOnlyList<string> Tribes,
    IReadOnlyList<string> Mechanics,
    IReadOnlyList<string> ReferencedTags,
    bool IsPoolMinion,
    bool IsPoolSpell,
    bool IsBgHero,
    bool IsDarkGift,
    bool IsDuosExclusive,
    int? PremiumDbfId,
    int? NormalDbfId,
    int? SkinParentDbfId,
    int? HeroPowerDbfId);

/// <summary>Where the loaded card data came from, shown to the player so stale data never passes as current.</summary>
public sealed record CardDataVersion(int? Build, DateTimeOffset FetchedAt, string Source);

public sealed partial class CardCatalog
{
    static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    readonly Dictionary<string, CardInfo> _byId;
    readonly Dictionary<int, CardInfo> _byDbf;

    public CardDataVersion Version { get; }
    public IReadOnlyList<CardInfo> All { get; }
    public IReadOnlyList<CardInfo> PoolMinions { get; }

    public CardCatalog(IReadOnlyList<CardInfo> cards, CardDataVersion version)
    {
        All = cards;
        Version = version;
        _byId = cards.ToDictionary(c => c.Id);
        _byDbf = cards.GroupBy(c => c.DbfId).ToDictionary(g => g.Key, g => g.First());
        PoolMinions = cards.Where(c => c.IsPoolMinion).OrderBy(c => c.Tier).ThenBy(c => c.Name).ToList();
    }

    public CardInfo? Get(string? id) => id is not null && _byId.TryGetValue(id, out var c) ? c : null;
    public CardInfo? GetByDbf(int? dbf) => dbf is int d && _byDbf.TryGetValue(d, out var c) ? c : null;

    /// <summary>Maps a golden copy or a hero skin to the card rules are written against. Null for unknown cards.</summary>
    public CardInfo? Resolve(string? id)
    {
        var card = Get(id);
        if (card is null) return null;
        if (card.SkinParentDbfId is not null && GetByDbf(card.SkinParentDbfId) is { } parent) return parent;
        if (card.NormalDbfId is not null && GetByDbf(card.NormalDbfId) is { } normal) return normal;
        return card;
    }

    public CardInfo? HeroPowerOf(CardInfo hero) => GetByDbf(hero.HeroPowerDbfId);

    /// <summary>Builds a catalog from the two HearthstoneJSON locale files, keeping Battlegrounds cards only.</summary>
    public static CardCatalog FromHearthstoneJson(Stream enUs, Stream ruRu, CardDataVersion version)
    {
        using var en = JsonDocument.Parse(enUs);
        using var ru = JsonDocument.Parse(ruRu);
        var ruById = new Dictionary<string, JsonElement>();
        foreach (var c in ru.RootElement.EnumerateArray())
            if (c.TryGetProperty("id", out var id) && id.GetString() is { } s) ruById[s] = c;

        var cards = new List<CardInfo>();
        foreach (var c in en.RootElement.EnumerateArray())
        {
            var id = Str(c, "id");
            if (id is null || !IsBattlegroundsCard(c, id)) continue;
            var loc = ruById.GetValueOrDefault(id, c);
            var tribes = Arr(c, "races");
            if (tribes.Count == 0 && Str(c, "race") is { } race) tribes = [race];
            var nameEn = Str(c, "name") ?? id;
            var textEn = CleanText(Str(c, "text"));
            cards.Add(new CardInfo(
                id,
                Int(c, "dbfId") ?? 0,
                Str(loc, "name") ?? nameEn,
                nameEn,
                ruById.ContainsKey(id) ? CleanText(Str(loc, "text")) : textEn,
                textEn,
                Str(c, "type") ?? "",
                Int(c, "techLevel"),
                Int(c, "attack"),
                Int(c, "health"),
                Int(c, "armor"),
                Int(c, "cost"),
                tribes,
                Arr(c, "mechanics"),
                Arr(c, "referencedTags"),
                Bool(c, "isBattlegroundsPoolMinion"),
                Bool(c, "isBattlegroundsPoolSpell"),
                Bool(c, "battlegroundsHero"),
                Bool(c, "isBattlegroundsDarkGift"),
                Bool(c, "isBattlegroundsDuosExclusive"),
                Int(c, "battlegroundsPremiumDbfId"),
                Int(c, "battlegroundsNormalDbfId"),
                Int(c, "battlegroundsSkinParentId"),
                Int(c, "heroPowerDbfId")));
        }
        return new CardCatalog(cards, version);
    }

    static bool IsBattlegroundsCard(JsonElement c, string id) =>
        Str(c, "set") == "BATTLEGROUNDS"
        || c.TryGetProperty("techLevel", out _)
        || Bool(c, "battlegroundsHero")
        || id.StartsWith("BG", StringComparison.Ordinal)
        || id.StartsWith("TB_Bacon", StringComparison.Ordinal);

    public void Save(Stream target)
    {
        using var gz = new GZipStream(target, CompressionLevel.SmallestSize, leaveOpen: true);
        JsonSerializer.Serialize(gz, new Stored(Version, All), Json);
    }

    public static CardCatalog Load(Stream source)
    {
        using var gz = new GZipStream(source, CompressionMode.Decompress, leaveOpen: true);
        var stored = JsonSerializer.Deserialize<Stored>(gz, Json)
            ?? throw new InvalidDataException("Empty card cache");
        return new CardCatalog(stored.Cards, stored.Version);
    }

    /// <summary>The snapshot shipped inside the app, so a first offline start still has cards.</summary>
    public static CardCatalog LoadEmbedded()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames().Single(n => n.EndsWith("cards.snapshot.json.gz", StringComparison.Ordinal));
        using var s = asm.GetManifestResourceStream(name)!;
        return Load(s);
    }

    /// <summary>Strips the game's markup so text can be shown and matched as plain sentences.</summary>
    public static string CleanText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var t = Markup().Replace(text, "").Replace('\n', ' ').Replace(' ', ' ').Replace("$", "").Replace("#", "");
        return Spaces().Replace(t, " ").Trim();
    }

    [GeneratedRegex(@"</?[bi]>|\[x\]", RegexOptions.IgnoreCase)] private static partial Regex Markup();
    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();

    static string? Str(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static int? Int(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
    static bool Bool(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
    static IReadOnlyList<string> Arr(JsonElement e, string k) =>
        e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
            : [];

    sealed record Stored(CardDataVersion Version, IReadOnlyList<CardInfo> Cards);
}
