using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using BgCompass.Core;
using BgCompass.Core.Advice;

namespace BgCompass.App.ViewModels;

public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RelayCommand(Action run, Func<bool>? canRun = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canRun?.Invoke() ?? true;
    public void Execute(object? parameter) => run();
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>Russian wording shared by the views.</summary>
public static class Ru
{
    public static readonly CultureInfo Culture = new("ru-RU");

    public static string Plural(int n, string one, string few, string many)
    {
        var m100 = Math.Abs(n) % 100;
        var m10 = m100 % 10;
        if (m100 is >= 11 and <= 14) return many;
        return m10 == 1 ? one : m10 is >= 2 and <= 4 ? few : many;
    }

    public static string Games(int n) => $"{n} {Plural(n, "игра", "игры", "игр")}";
    public static string RoundsAgo(int n) => $"{n} {Plural(n, "раунд", "раунда", "раундов")} назад";
    public static string Num(double v, string f = "0.0") => v.ToString(f, Culture);
    public static string Pct(double fraction) => $"{Math.Round(fraction * 100):0}%";

    public static string Tribe(string tribe)
    {
        var t = Core.Advice.TribeNames.Ru(tribe);
        return t.Length == 0 ? t : char.ToUpper(t[0], Culture) + t[1..];
    }

    static readonly Dictionary<string, string> Mechanics = new()
    {
        ["BATTLECRY"] = "Боевой клич", ["DEATHRATTLE"] = "Предсмертный хрип", ["BACON_RALLY"] = "Боевой раж",
        ["DIVINE_SHIELD"] = "Божественный щит", ["TAUNT"] = "Провокация", ["REBORN"] = "Перерождение",
        ["WINDFURY"] = "Неистовство ветра", ["STEALTH"] = "Маскировка", ["VENOMOUS"] = "Токсичность",
        ["POISONOUS"] = "Ядовитость", ["MAGNETIC"] = "Магнетизм", ["AVENGE"] = "Месть",
        ["START_OF_COMBAT"] = "Начало боя", ["END_OF_TURN_TRIGGER"] = "Конец хода",
    };

    /// <summary>Keywords the catalog filter offers, in a stable order.</summary>
    public static IReadOnlyList<string> KeywordNames { get; } = Mechanics.Values.Distinct().ToList();

    public static string? MechanicLabel(string mechanic) => Mechanics.GetValueOrDefault(mechanic);

    public static IEnumerable<string> Keywords(IEnumerable<string> mechanics) =>
        mechanics.Select(m => Mechanics.GetValueOrDefault(m)).Where(s => s is not null).Distinct()!;

    public static string Keywords(MinionKeywords k)
    {
        var parts = new List<string>();
        void Add(MinionKeywords flag, string name) { if (k.HasFlag(flag)) parts.Add(name); }
        Add(MinionKeywords.Taunt, "Провокация");
        Add(MinionKeywords.DivineShield, "Божественный щит");
        Add(MinionKeywords.Poisonous, "Ядовитость");
        Add(MinionKeywords.Venomous, "Токсичность");
        Add(MinionKeywords.Reborn, "Перерождение");
        Add(MinionKeywords.Windfury, "Неистовство ветра");
        Add(MinionKeywords.Stealth, "Маскировка");
        Add(MinionKeywords.Deathrattle, "Предсмертный хрип");
        return string.Join(", ", parts);
    }
}

public sealed class MinionVm(MinionView v)
{
    public string Name { get; } = v.Name.Length > 0 ? v.Name : v.CardId;
    public string Stats { get; } = $"{v.Attack}/{v.Health}";
    public bool Golden { get; } = v.Golden;
    public int Tier { get; } = v.TechLevel;
    public string Tip { get; } = string.Join("\n", new[]
    {
        v.TechLevel > 0 ? $"Таверна {v.TechLevel}" : null,
        v.Tribes.Count > 0 ? string.Join(", ", v.Tribes.Select(Ru.Tribe)) : null,
        Ru.Keywords(v.Keywords) is { Length: > 0 } k ? k : null,
        v.Golden ? "Золотое" : null,
    }.Where(s => s is not null));
}

public sealed class CardChipVm(CardRef c)
{
    public string Name { get; } = c.Name;
    public int Tier { get; } = c.Tier;
    public bool InShop { get; } = c.InShop;
    public bool OnBoard { get; } = c.OnBoard;
    public string Tip { get; } = c.InShop ? $"В магазине, таверна {c.Tier}" : c.OnBoard ? "Уже на доске" : $"Таверна {c.Tier}";
}
