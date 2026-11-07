using System.Globalization;

namespace BgCompass.Core.Advice;

/// <summary>Small Russian text helpers shared by the advisors.</summary>
static class AdviceText
{
    static readonly CultureInfo Ru = new("ru-RU");

    public static string Num(double value, string format = "0.0") => value.ToString(format, Ru);

    /// <summary>"2026-11-06" -> "06.11.2026"; anything unparsable is shown as written.</summary>
    public static string Date(string iso) =>
        DateOnly.TryParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.ToString("dd.MM.yyyy", Ru) : iso;

    public static string Plural(int n, string one, string few, string many)
    {
        var m100 = Math.Abs(n) % 100;
        var m10 = m100 % 10;
        if (m100 is >= 11 and <= 14) return many;
        return m10 == 1 ? one : m10 is >= 2 and <= 4 ? few : many;
    }

    public static string Games(int n) => $"{n} {Plural(n, "игра", "игры", "игр")}";

    /// <summary>The first <paramref name="max"/> characters of a card text, cut at a word, without a trailing full stop.</summary>
    public static string Clip(string text, int max = 110)
    {
        // Callers wrap the result in «», so quotes inside the card text switch to „“.
        var t = text.Trim().Replace('«', '„').Replace('»', '“');
        if (t.Length > max)
        {
            var cut = t.LastIndexOf(' ', max);
            t = t[..(cut > 40 ? cut : max)].TrimEnd(',', ';', ' ') + "…";
        }
        return t.TrimEnd('.');
    }

    public static string Names(IEnumerable<string> names, int max = 4)
    {
        var list = names.Distinct().ToList();
        return list.Count <= max
            ? string.Join(", ", list.Select(n => $"«{n}»"))
            : string.Join(", ", list.Take(max).Select(n => $"«{n}»")) + $" и ещё {list.Count - max}";
    }
}
