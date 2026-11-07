using System.Text;
using System.Text.Json;
using BgCompass.Core.Advice;
using BgCompass.Core.Cards;

namespace BgCompass.Core.History;

/// <summary>Own games only: places the player actually finished with. No MMR, the log never states it.</summary>
public sealed record HeroStats(int Games, double AveragePlace, double Top4Rate, int Wins);

/// <summary>
/// Append-only JSON Lines file, one <see cref="MatchRecord"/> per line. A game id is stored once.
/// A line that does not parse (a crash mid-write, a hand edit) is skipped and never blocks the rest.
/// </summary>
public sealed class HistoryStore
{
    const string FileName = "history.jsonl";
    static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    readonly string _path;
    readonly object _gate = new();
    HashSet<string>? _ids;

    public HistoryStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, FileName);
    }

    /// <summary>Stores the record; false when this game id is already there.</summary>
    public bool Append(MatchRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.GameId)) return false;
        lock (_gate)
        {
            _ids ??= ReadAll().Select(r => r.GameId).ToHashSet();
            if (!_ids.Add(record.GameId)) return false;

            // One Write call per record so a crash leaves at most one broken last line. If the file already
            // ends in such a line, start on a fresh one, otherwise the new record would be glued onto it.
            var line = JsonSerializer.Serialize(record, Json) + "\n";
            var bytes = Encoding.UTF8.GetBytes(NeedsLeadingNewline() ? "\n" + line : line);
            using var fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
            fs.Write(bytes);
            fs.Flush(true);
            return true;
        }
    }

    bool NeedsLeadingNewline()
    {
        if (!File.Exists(_path)) return false;
        using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (fs.Length == 0) return false;
        fs.Seek(-1, SeekOrigin.End);
        return fs.ReadByte() != '\n';
    }

    /// <summary>All stored games, newest first.</summary>
    public IReadOnlyList<MatchRecord> History()
    {
        lock (_gate) return ReadAll().OrderByDescending(r => r.EndedAt).ToList();
    }

    List<MatchRecord> ReadAll()
    {
        var result = new List<MatchRecord>();
        if (!File.Exists(_path)) return result;
        var seen = new HashSet<string>();
        using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var r = JsonSerializer.Deserialize<MatchRecord>(line, Json);
                if (r is { GameId.Length: > 0, HeroCardId: not null } && seen.Add(r.GameId)) result.Add(r);
            }
            catch (JsonException) { /* corrupt or truncated line: skip */ }
        }
        return result;
    }

    // ---- Aggregates over finished games (Place known) -------------------------------------------

    /// <summary>Per base hero: a skin and its base hero share one entry, keyed by the base hero's card id.</summary>
    public IReadOnlyDictionary<string, HeroStats> HeroStatsByHero(CardCatalog cards) =>
        Aggregate(r => cards.Resolve(r.HeroCardId)?.Id ?? r.HeroCardId);

    /// <summary>Per archetype id. Games with no determined archetype are not counted here.</summary>
    public IReadOnlyDictionary<string, HeroStats> StatsByArchetype() => Aggregate(r => r.ArchetypeId);

    /// <summary>All finished games together; null when there are none.</summary>
    public HeroStats? Overall() => Summarize(History().Where(r => r.Place is > 0).ToList());

    IReadOnlyDictionary<string, HeroStats> Aggregate(Func<MatchRecord, string?> key) =>
        History().Where(r => r.Place is > 0 && key(r) is not null)
            .GroupBy(r => key(r)!)
            .ToDictionary(g => g.Key, g => Summarize(g.ToList())!);

    static HeroStats? Summarize(IReadOnlyList<MatchRecord> games)
    {
        if (games.Count == 0) return null;
        return new HeroStats(
            games.Count,
            games.Average(r => r.Place!.Value),
            games.Count(r => r.Place <= 4) / (double)games.Count,
            games.Count(r => r.Place == 1));
    }

    /// <summary>
    /// The archetype to store with a finished game, from its final board only. Null ("не определена")
    /// unless one family clearly leads, see <see cref="Archetypes.Classify"/>.
    /// </summary>
    public static string? ClassifyBoard(IEnumerable<string> finalBoardCardIds, CardCatalog cards, Archetypes archetypes) =>
        archetypes.Classify(finalBoardCardIds.Select(cards.Resolve).OfType<CardInfo>());
}
