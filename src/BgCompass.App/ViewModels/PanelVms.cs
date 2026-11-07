using System.Windows.Input;
using BgCompass.App.Runtime;
using BgCompass.Core;
using BgCompass.Core.Advice;
using BgCompass.Core.Sim;

namespace BgCompass.App.ViewModels;

/// <summary>Why there is nothing to show: no log, logging off. Null when there is data. Goes into a tooltip.</summary>
public static class EmptyStates
{
    public static string? NoLog(UiState s)
    {
        if (s.IsReplay) return null;
        if (s.Log.PowerLogPath is null)
            return s.Log.Config is { PowerEnabled: false }
                ? "Hearthstone не пишет Power.log. Настройки → «Включить логи», затем перезапустите игру."
                : "Power.log не найден. Запустите Hearthstone или укажите папку игры в настройках.";
        return null;
    }

    public static string NoLogTitle(UiState s) =>
        s.Log.Config is { PowerEnabled: false } ? "Логи выключены" : "Лог не найден";
}

/// <summary>Advice lines joined for a tooltip. The sentences live here, not on the panel.</summary>
public static class Tips
{
    public static string Join(IEnumerable<AdviceLine> lines) => string.Join("\n", lines.Select(l => l.Text));
}

public enum ChipState { Unseen, Confirmed, Absent }

public sealed class TribeChipVm : ViewModelBase
{
    readonly AppHost _host;
    ChipState _state;

    public TribeChipVm(AppHost host, string tribe)
    {
        _host = host;
        Tribe = tribe;
        Label = Ru.Tribe(tribe);
        Toggle = new RelayCommand(() => _host.ToggleTribe(Tribe));
    }

    public string Tribe { get; }
    public string Label { get; }
    public ICommand Toggle { get; }
    public ChipState State { get => _state; private set { if (Set(ref _state, value)) { Raise(nameof(IsConfirmed)); Raise(nameof(IsAbsent)); Raise(nameof(Tip)); } } }
    public bool IsConfirmed => State == ChipState.Confirmed;
    public bool IsAbsent => State == ChipState.Absent;
    public string Tip => State switch
    {
        ChipState.Confirmed => "Замечено в лобби",
        ChipState.Absent => "Отмечено: нет в лобби. Нажмите, чтобы снять",
        _ => "Не замечено, но это не значит, что его нет. Нажмите, если племени нет",
    };

    public void Update(ChipState s) => State = s;
}

/// <summary>What the panel header says and the tribe marks. The tribes page binds to this too.</summary>
public sealed class TopBarVm : ViewModelBase
{
    string _round = "", _tier = "", _status = "", _statusTip = "";
    bool _replay, _inMatch;

    public TopBarVm(AppHost host, Action openMain)
    {
        Chips = Core.Advice.TribeNames.Real.Select(t => new TribeChipVm(host, t)).ToList();
        OpenMain = new RelayCommand(openMain);
    }

    public IReadOnlyList<TribeChipVm> Chips { get; }
    public ICommand OpenMain { get; }
    public string Round { get => _round; private set => Set(ref _round, value); }
    public string Tier { get => _tier; private set => Set(ref _tier, value); }
    public string Status { get => _status; private set { if (Set(ref _status, value)) Raise(nameof(HasStatus)); } }
    public bool HasStatus => _status.Length > 0;
    public string StatusTip { get => _statusTip; private set => Set(ref _statusTip, value); }
    public bool IsReplay { get => _replay; private set => Set(ref _replay, value); }
    public bool InMatch { get => _inMatch; private set => Set(ref _inMatch, value); }

    public void Apply(UiState s)
    {
        var m = s.Match;
        InMatch = s.InMatch;
        Round = m.Phase switch
        {
            MatchPhase.HeroPick => "Выбор героя",
            MatchPhase.GameOver => "Итог",
            MatchPhase.Idle => "BG Compass",
            _ => m.Round > 0 ? $"Раунд {m.Round}" : "Партия",
        };
        Tier = m.TavernTier is int t && m.Phase is MatchPhase.Recruit or MatchPhase.Combat ? $"Таверна {t}" : "";
        IsReplay = s.IsReplay;
        foreach (var c in Chips)
            c.Update(m.ConfirmedTribes.Contains(c.Tribe) ? ChipState.Confirmed
                : s.AbsentTribes.Contains(c.Tribe) ? ChipState.Absent : ChipState.Unseen);

        if (s.Cards.Refreshing) { Status = "Обновляю карты"; StatusTip = "Загрузка базы карт с HearthstoneJSON"; }
        else if (s.Cards.Warning is { } w) { Status = "Карты могут быть устаревшими"; StatusTip = w; }
        else { Status = ""; StatusTip = ""; }
    }
}

public sealed class StatusVm(ICommand open) : ViewModelBase
{
    string _title = "", _tip = "";
    public ICommand Open { get; } = open;
    public string Title { get => _title; private set => Set(ref _title, value); }
    public string Tip { get => _tip; private set => Set(ref _tip, value); }

    public void Apply(UiState s)
    {
        Title = EmptyStates.NoLogTitle(s);
        Tip = EmptyStates.NoLog(s) ?? "";
    }
}

public sealed class HeroOfferVm(HeroAdvice a, bool recommended, AdviceLine? reason)
{
    public string Name { get; } = a.Option.Name.Length > 0 ? a.Option.Name : a.Option.CardId;
    public bool IsRecommended { get; } = recommended;
    /// <summary>A pick that comes from a rule, not from the player's own numbers, gets an outlined tile.</summary>
    public bool IsGuess { get; } = recommended && reason?.Source != AdviceSource.Personal;
    public bool HasStats { get; } = a.PersonalAveragePlace is not null;
    public string Tile { get; } = a.PersonalAveragePlace is double p ? Ru.Num(p) : "–";
    public string Sub { get; } = string.Join(" · ", new[]
    {
        a.Armor is int ar ? $"броня {ar}" : null,
        a.PersonalGames is > 0 ? Ru.Games(a.PersonalGames.Value) : "нет игр",
    }.Where(s => s is not null));
    public string Tip { get; } = string.Join("\n", (recommended && reason is not null ? new[] { reason.Text } : []).Concat(a.Lines.Select(l => l.Text)));
}

public sealed class HeroVm : ViewModelBase
{
    IReadOnlyList<HeroOfferVm> _offers = [];
    string _empty = "";
    HeroPickAdvice? _lastAdvice;
    int _lastOffers;

    public IReadOnlyList<HeroOfferVm> Offers { get => _offers; private set => Set(ref _offers, value); }
    public string Empty { get => _empty; private set { if (Set(ref _empty, value)) Raise(nameof(HasEmpty)); } }
    public bool HasEmpty => _empty.Length > 0;

    public void Apply(UiState s)
    {
        var advice = s.Hero;
        if (ReferenceEquals(advice, _lastAdvice) && s.Match.HeroOffers.Count == _lastOffers && advice is not null) return;
        _lastAdvice = advice;
        _lastOffers = s.Match.HeroOffers.Count;
        if (advice is null)
        {
            Offers = [];
            Empty = s.Match.HeroOffers.Count == 0 ? "Жду героев" : "Загружаю карты";
            return;
        }
        Empty = "";
        Offers = advice.Heroes.OrderBy(h => h.Option.ScreenSlot)
            .Select(h => new HeroOfferVm(h, h.Option.CardId == advice.RecommendedCardId, advice.RecommendationReason))
            .ToList();
    }
}

public sealed class TabVm(string title, Action select) : ViewModelBase
{
    string _title = title;
    bool _selected;
    public string Title { get => _title; set => Set(ref _title, value); }
    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
    public ICommand Select { get; } = new RelayCommand(select);
}

public sealed class DirectionVm(ArchetypeFit f, bool expanded, Action<string> toggle)
{
    const int MaxNeed = 4, MaxNext = 3;

    /// <summary>Cards in the shop come first, since those are the ones that can be bought now.</summary>
    static IEnumerable<CardRef> ShopFirst(IEnumerable<CardRef> cards) => cards.OrderByDescending(c => c.InShop);

    public string Name { get; } = f.Name;
    public string Percent { get; } = Ru.Pct(f.Fit);
    public bool IsClear { get; } = f.Fit >= Archetypes.ClearDirection;
    public bool Expanded { get; } = expanded;
    public ICommand Toggle { get; } = new RelayCommand(() => toggle(f.Id));
    public IReadOnlyList<CardChipVm> Need { get; } = ShopFirst(f.MissingCoreNow).Take(MaxNeed).Select(c => new CardChipVm(c)).ToList();
    public IReadOnlyList<CardChipVm> Next { get; } = ShopFirst(f.NextTierTargets).Take(MaxNext).Select(c => new CardChipVm(c)).ToList();
    public bool HasNeed => Need.Count > 0;
    public bool HasNext => Next.Count > 0;
    public string Tip { get; } = string.Join("\n", new[]
    {
        f.HaveCore.Count > 0 ? "Есть: " + string.Join(", ", f.HaveCore.Select(c => c.Name)) : null,
        f.MissingCoreNow.Count > MaxNeed ? "Ещё нужно: " + string.Join(", ", ShopFirst(f.MissingCoreNow).Skip(MaxNeed).Select(c => c.Name)) : null,
        f.NextTierTargets.Count > MaxNext ? "Ещё дальше: " + string.Join(", ", ShopFirst(f.NextTierTargets).Skip(MaxNext).Select(c => c.Name)) : null,
        Tips.Join(f.Lines),
    }.Where(s => s is not null));
}

public sealed class HintPillVm(string label, string tip, bool alert)
{
    public string Label { get; } = label;
    public string Tip { get; } = tip;
    public bool IsAlert { get; } = alert;
}

/// <summary>Shop cards the top directions want now. The panel lists them, the game layer rings them.</summary>
public static class BuildPicks
{
    public static IEnumerable<(CardRef Card, ArchetypeFit Direction)> Now(BoardAdvice a) =>
        a.Directions.Take(3)
            .SelectMany(d => d.MissingCoreNow.Concat(d.NextTierTargets).Where(c => c.InShop).Select(c => (Card: c, Direction: d)))
            .DistinctBy(p => p.Card.CardId);
}

public sealed class BuildVm : ViewModelBase
{
    BoardAdvice? _last;
    string? _expanded;
    string _empty = "";
    IReadOnlyList<DirectionVm> _directions = [];
    IReadOnlyList<CardChipVm> _buy = [];
    IReadOnlyList<HintPillVm> _hints = [];

    public string Empty { get => _empty; private set { if (Set(ref _empty, value)) { Raise(nameof(HasEmpty)); Raise(nameof(HasData)); } } }
    public bool HasEmpty => _empty.Length > 0;
    public bool HasData => _empty.Length == 0;
    public IReadOnlyList<DirectionVm> Directions { get => _directions; private set => Set(ref _directions, value); }
    public IReadOnlyList<CardChipVm> Buy { get => _buy; private set { if (Set(ref _buy, value)) Raise(nameof(HasBuy)); } }
    public bool HasBuy => _buy.Count > 0;
    public IReadOnlyList<HintPillVm> Hints { get => _hints; private set => Set(ref _hints, value); }

    public void Apply(UiState s)
    {
        var a = s.Board;
        if (ReferenceEquals(a, _last) && a is not null) return;
        _last = a;
        if (a is null)
        {
            Empty = EmptyStates.NoLog(s) is not null ? EmptyStates.NoLogTitle(s) : s.Match.Phase == MatchPhase.Recruit ? "Загружаю карты" : "Нет данных";
            return;
        }
        Empty = s.Match.Board.Count == 0 && s.Match.Shop.Count == 0 ? "Доска и магазин пока пусты" : "";
        Rebuild(a);
    }

    void Rebuild(BoardAdvice a)
    {
        var top = a.Directions.Take(3).ToList();
        var open = top.Any(d => d.Id == _expanded) ? _expanded : top.FirstOrDefault()?.Id;
        Directions = top.Select(d => new DirectionVm(d, d.Id == open, Toggle)).ToList();
        Buy = BuildPicks.Now(a).Select(p => new CardChipVm(p.Card)).ToList();
        var pills = new List<HintPillVm>();
        void Pill(string label, IReadOnlyList<AdviceLine> lines, bool alert = false)
        {
            if (lines.Count > 0) pills.Add(new($"{label} {lines.Count}", Tips.Join(lines), alert));
        }
        Pill("Пробелы", a.Gaps, alert: true);
        Pill("Синергии", a.Synergies);
        Pill("Позиции", a.Positioning);
        Pill("Ограничения", a.Limits);
        Hints = pills;
    }

    void Toggle(string id)
    {
        _expanded = id;
        if (_last is not null) Rebuild(_last);
    }
}

public sealed class SeatVm(PlayerView p, Action<int> select, bool selected)
{
    public int PlayerId { get; } = p.PlayerId;
    public string Place { get; } = p.Place is int pl ? pl.ToString() : "–";
    public string Name { get; } = p.HeroName.Length > 0 ? p.HeroName : p.HeroCardId;
    public string Health { get; } = p.IsDead ? "–" : p.Health is int h ? h.ToString() : "–";
    public string Tier { get; } = p.TavernTier is int t ? t.ToString() : "–";
    public bool IsLocal { get; } = p.IsLocal;
    public bool IsNext { get; } = p.IsNextOpponent;
    public bool IsDead { get; } = p.IsDead;
    public bool IsSelected { get; } = selected;
    public ICommand Select { get; } = new RelayCommand(() => select(p.PlayerId));
}

public sealed class EncounterVm(Encounter e)
{
    public bool IsWin { get; } = e.Result == EncounterResult.Win;
    public bool IsLoss { get; } = e.Result == EncounterResult.Loss;
    public string Score { get; } = e.Result switch
    {
        EncounterResult.Win => e.DamageToEnemy is int d ? $"+{d}" : "+",
        EncounterResult.Loss => e.DamageToMe is int d ? $"−{d}" : "−",
        EncounterResult.Tie => "=",
        _ => "?",
    };
    public string Text { get; } = $"Раунд {e.Round}: " + e.Result switch
    {
        EncounterResult.Win => "победа" + (e.DamageToEnemy is int d ? $", урон сопернику {d}" : ""),
        EncounterResult.Loss => "поражение" + (e.DamageToMe is int d ? $", урон вам {d}" : ""),
        EncounterResult.Tie => "ничья",
        _ => "итог неизвестен",
    };
}

public sealed class OpponentVm : ViewModelBase
{
    int? _picked;
    UiState? _last;
    string _heading = "", _boardAge = "", _empty = "", _focusTier = "–";
    bool _stale, _hasFocus;
    IReadOnlyList<MinionVm> _board = [];
    IReadOnlyList<EncounterVm> _encounters = [];
    IReadOnlyList<SeatVm> _seats = [];

    public string Heading { get => _heading; private set => Set(ref _heading, value); }
    public string FocusTier { get => _focusTier; private set => Set(ref _focusTier, value); }
    public bool HasFocus { get => _hasFocus; private set => Set(ref _hasFocus, value); }
    public string BoardAge { get => _boardAge; private set => Set(ref _boardAge, value); }
    public bool IsStale { get => _stale; private set => Set(ref _stale, value); }
    public IReadOnlyList<MinionVm> Board { get => _board; private set { if (Set(ref _board, value)) Raise(nameof(HasBoard)); } }
    public bool HasBoard => _board.Count > 0;
    public IReadOnlyList<EncounterVm> Encounters { get => _encounters; private set { if (Set(ref _encounters, value)) Raise(nameof(HasEncounters)); } }
    public bool HasEncounters => _encounters.Count > 0;
    public IReadOnlyList<SeatVm> Seats { get => _seats; private set => Set(ref _seats, value); }
    public string Empty { get => _empty; private set { if (Set(ref _empty, value)) { Raise(nameof(HasEmpty)); Raise(nameof(HasData)); } } }
    public bool HasEmpty => _empty.Length > 0;
    public bool HasData => _empty.Length == 0;

    void Pick(int id)
    {
        _picked = _picked == id ? null : id;
        if (_last is not null) Rebuild(_last);
    }

    public void Apply(UiState s)
    {
        if (_last is not null && ReferenceEquals(_last.Match, s.Match)) return;
        Rebuild(s);
    }

    void Rebuild(UiState s)
    {
        _last = s;
        var m = s.Match;
        if (m.Players.Count == 0)
        {
            Empty = EmptyStates.NoLog(s) is not null ? EmptyStates.NoLogTitle(s) : "Игроков пока нет в логе";
            Seats = []; Board = []; Encounters = []; HasFocus = false;
            return;
        }
        Empty = "";
        if (_picked is int pid && m.Players.All(p => p.PlayerId != pid)) _picked = null;
        var focus = (_picked is int id ? m.Players.FirstOrDefault(p => p.PlayerId == id) : null)
            ?? m.Players.FirstOrDefault(p => p.IsNextOpponent && !p.IsLocal);
        Seats = m.Players.OrderBy(p => p.Place ?? 99).Select(p => new SeatVm(p, Pick, p.PlayerId == focus?.PlayerId)).ToList();
        HasFocus = focus is not null;
        if (focus is null)
        {
            Heading = ""; BoardAge = ""; IsStale = false; Board = []; Encounters = [];
            return;
        }
        Heading = focus.HeroName.Length > 0 ? focus.HeroName : focus.HeroCardId;
        FocusTier = focus.TavernTier is int tt ? tt.ToString() : "–";
        if (focus.LastBoard is { } lb && lb.Minions.Count > 0)
        {
            var age = m.Round - lb.Round;
            IsStale = age > 0;
            BoardAge = age > 0 ? $"доска {Ru.RoundsAgo(age)}" : "доска этого раунда";
            Board = lb.Minions.OrderBy(x => x.Position).Select(x => new MinionVm(x)).ToList();
        }
        else
        {
            IsStale = false;
            BoardAge = "доску ещё не видели";
            Board = [];
        }
        Encounters = m.Encounters.Where(e => e.OpponentPlayerId == focus.PlayerId).OrderByDescending(e => e.Round)
            .Select(e => new EncounterVm(e)).ToList();
    }
}

public sealed class SimFactVm(string label, string value, string tip = "", bool alert = false)
{
    public string Label { get; } = label;
    public string Value { get; } = value;
    public string Tip { get; } = tip;
    public bool IsAlert { get; } = alert;
}

public sealed class SimVm : ViewModelBase
{
    SimView? _last;
    CombatSnapshot? _lastCombat;
    string _headline = "", _message = "", _note = "";
    double _win, _tie, _loss;
    string _winText = "", _tieText = "", _lossText = "";
    bool _hasResult;
    IReadOnlyList<SimFactVm> _facts = [];

    public string Headline { get => _headline; private set => Set(ref _headline, value); }
    public bool HasResult { get => _hasResult; private set => Set(ref _hasResult, value); }
    public Avalonia.Controls.GridLength WinCol => new(Math.Max(_win, 0.0001), Avalonia.Controls.GridUnitType.Star);
    public Avalonia.Controls.GridLength TieCol => new(Math.Max(_tie, 0.0001), Avalonia.Controls.GridUnitType.Star);
    public Avalonia.Controls.GridLength LossCol => new(Math.Max(_loss, 0.0001), Avalonia.Controls.GridUnitType.Star);
    public string WinText { get => _winText; private set => Set(ref _winText, value); }
    public string TieText { get => _tieText; private set => Set(ref _tieText, value); }
    public string LossText { get => _lossText; private set => Set(ref _lossText, value); }
    public IReadOnlyList<SimFactVm> Facts { get => _facts; private set => Set(ref _facts, value); }
    public string Message { get => _message; private set { if (Set(ref _message, value)) Raise(nameof(HasMessage)); } }
    public bool HasMessage => _message.Length > 0;
    public string Note { get => _note; private set => Set(ref _note, value); }
    public string LimitsText => CombatSimulator.Limits;

    public void Apply(UiState s)
    {
        if (ReferenceEquals(_last, s.Sim) && ReferenceEquals(_lastCombat, s.Match.Combat)) return;
        _last = s.Sim;
        _lastCombat = s.Match.Combat;
        HasResult = false; Facts = []; Message = ""; Note = "";
        var vs = s.Match.Players.FirstOrDefault(p => p.PlayerId == s.Match.Combat?.OpponentPlayerId)?.HeroName;
        Headline = string.IsNullOrEmpty(vs) ? "Бой" : $"против {vs}";
        switch (s.Sim.Status)
        {
            case SimStatus.Running:
                Message = "Считаю…";
                return;
            case SimStatus.None:
                Message = s.Match.Combat is null ? "Состав боя ещё не виден" : "Не рассчитан";
                Note = EmptyStates.NoLog(s) ?? "";
                return;
        }
        var r = s.Sim.Result!;
        if (!r.Available)
        {
            Message = "Расчёт недоступен";
            Note = r.UnavailableReason ?? "Причина не указана.";
            return;
        }
        HasResult = true;
        _win = r.Win; _tie = r.Tie; _loss = r.Loss;
        Raise(nameof(WinCol)); Raise(nameof(TieCol)); Raise(nameof(LossCol));
        WinText = Ru.Pct(r.Win);
        TieText = Ru.Pct(r.Tie);
        LossText = Ru.Pct(r.Loss);
        Note = $"{r.Iterations} прогонов. Оценка, а не прогноз.";
        var facts = new List<SimFactVm>();
        if (r.DamageDealtP50 is int d) facts.Add(new("Нанесёте", d.ToString(), "Типичный урон при победе"));
        if (r.DamageTakenP50 is int p50)
            facts.Add(new("Получите", r.DamageTakenP10 is int a && r.DamageTakenP90 is int b ? $"{p50} ({a}–{b})" : p50.ToString(), "Типичный урон при поражении и обычный разброс"));
        var c = s.Match.Combat;
        facts.Add(c?.FriendlyHealth is > 0 && c.EnemyTier is > 0
            ? new("Смертельный урон", Ru.Pct(r.LethalRisk), "", r.LethalRisk > 0)
            : new("Смертельный урон", "–", "Не оценён: в логе нет здоровья или таверны соперника"));
        if (r.Unmodelled.Count > 0)
            facts.Add(new("Не смоделировано", r.Unmodelled.Count.ToString(), string.Join(", ", r.Unmodelled) + ". Шансы сдвинуты к середине."));
        Facts = facts;
    }
}

public sealed class GameOverVm : ViewModelBase
{
    string _place = "", _hero = "", _rounds = "", _note = "";
    bool _dismissed, _top, _bottom, _hasPlace;
    MatchPhase _lastPhase;

    public GameOverVm() => Dismiss = new RelayCommand(() => Dismissed = true);

    public ICommand Dismiss { get; }
    public string Place { get => _place; private set => Set(ref _place, value); }
    public bool HasPlace { get => _hasPlace; private set => Set(ref _hasPlace, value); }
    public bool IsTop { get => _top; private set => Set(ref _top, value); }
    public bool IsBottom { get => _bottom; private set => Set(ref _bottom, value); }
    public string Hero { get => _hero; private set => Set(ref _hero, value); }
    public string Rounds { get => _rounds; private set => Set(ref _rounds, value); }
    public string Note { get => _note; private set => Set(ref _note, value); }
    public bool Dismissed { get => _dismissed; private set { if (Set(ref _dismissed, value)) Changed?.Invoke(); } }
    public event Action? Changed;

    public void Apply(UiState s)
    {
        var m = s.Match;
        if (m.Phase != MatchPhase.GameOver) { if (_lastPhase == MatchPhase.GameOver) _dismissed = false; _lastPhase = m.Phase; return; }
        _lastPhase = m.Phase;
        Place = m.FinalPlace is int p ? p.ToString() : "?";
        HasPlace = m.FinalPlace is not null;
        IsTop = m.FinalPlace is <= 4;
        IsBottom = m.FinalPlace is > 4;
        Hero = m.LocalHeroName is { Length: > 0 } n ? n : "Герой неизвестен";
        Rounds = m.Round > 0 ? $"{m.Round} {Ru.Plural(m.Round, "раунд", "раунда", "раундов")}" : "";
        Note = m.FinalPlace is null ? "Лог не назвал место: игра записана без места." : "Записано в статистику.";
    }
}
