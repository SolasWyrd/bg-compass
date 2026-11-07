// Card scripts ported from bgtracker sim/engine.py (MIT License); C# port for BG Compass.
using System.Text.RegularExpressions;
using BgCompass.Core.Cards;

namespace BgCompass.Core.Sim;

/// <summary>What a card does in combat, beyond its printed stats and keywords (those come from the snapshot).</summary>
internal sealed class CardScript
{
    /// <summary>Shared, immutable: a card with no combat effect.</summary>
    public static readonly CardScript Plain = new();

    public bool Cleave, Rally;
    public int Overkill; // 0 none, 1 one random neighbour, 2 both neighbours
    public int DrRep;    // extra deathrattle triggers this minion's presence grants
    public int KnightA, KnightH;
    public int DeathA;   // Attack this minion has for each friendly minion that died this combat
    public SummonSpec? Summon;
    public BuffSpec? DrBuff;
    public DeathHook? OnDeath;
    public StartHook? Soc;
    public AttackHook? OnAtk;
    public WatchHook? FAtk, FDeath, OnReborn, OnSummon;

    public bool HasDeathrattle => Summon is not null || DrBuff is not null || OnDeath is not null;

    public bool HasEffect =>
        Cleave || Overkill != 0 || DrRep != 0 || KnightA != 0 || DeathA != 0 || HasDeathrattle
        || Soc is not null || OnAtk is not null || FAtk is not null || FDeath is not null
        || OnReborn is not null || OnSummon is not null;
}

/// <summary>Everything the simulator knows about one card (a golden's own text, its base card, how well it is modelled).</summary>
internal sealed class Entry
{
    public required CardInfo Text { get; init; }
    public required CardInfo Base { get; init; }
    public required CardScript Script { get; init; }
    /// <summary>The card has an effect that matters once combat has started.</summary>
    public bool InCombat { get; init; }
    /// <summary>Every combat effect of the card is scripted.</summary>
    public bool Covered { get; init; }
    /// <summary>On bgtracker's UNMODELLED list: floor-scripted at best, always widens the odds.</summary>
    public bool Hard { get; init; }
    public bool HandFailed { get; init; }
    /// <summary>The odds are widened because of this card.</summary>
    public bool Unmodelled => Hard || (InCombat && !Covered);
}

internal sealed partial class ScriptBook
{
    public CardCatalog Cards { get; }
    readonly Dictionary<string, Entry> _memo = new();
    Dictionary<string, List<CardInfo>>? _minionsByName;

    public ScriptBook(CardCatalog cards) => Cards = cards;

    /// <summary>Cards bgtracker lists as not (fully) modelled; the odds are widened whenever one is on a board.</summary>
    static readonly HashSet<string> HardIds =
    [
        "BG20_104", "BG33_886", "BG33_883", "BG33_430", "BG31_835", "BG34_140", "BG25_009", "BG25_806",
        "BG31_801", "BG_TTN_401", "BG25_011", "BG36_351", "BG31_809", "BG31_925", "BG32_172", "BG36_241",
        "BG34_925", "BG34_320", "BG35_814",
    ];

    public static bool IsHard(string id) => HardIds.Contains(id);

    // ------------------------------------------------------------------ templates

    /// <summary>Builds the starting minion for a snapshot view. Null with an error text when the card is unknown.</summary>
    public Minion? Template(MinionView v, out Entry? entry, out bool unmodelled, out string? error)
    {
        entry = null;
        unmodelled = false;
        error = null;
        var card = Cards.Get(v.CardId);
        if (card is null)
        {
            // A token or new card outside the catalog still fights with its logged stats; its effects count as unmodelled.
            unmodelled = true;
            return Plain(v);
        }

        var textCard = card;
        var goldenMissing = false;
        if (v.Golden && card.NormalDbfId is null)
        {
            if (Cards.GetByDbf(card.PremiumDbfId) is { } g) textCard = g;
            else goldenMissing = true;
        }

        entry = For(textCard);
        unmodelled = entry.Unmodelled || (goldenMissing && entry.InCombat);
        var kw = v.Keywords;
        return new Minion
        {
            Sc = goldenMissing ? CardScript.Plain : entry.Script,
            Atk = v.Attack,
            Hp = v.Health,
            MaxHp = v.Health,
            BaseAtk = textCard.Attack ?? v.Attack,
            BaseHp = textCard.Health ?? v.Health,
            Ds = (kw & MinionKeywords.DivineShield) != 0 ? 1 : 0,
            Tier = v.TechLevel > 0 ? v.TechLevel : textCard.Tier ?? card.Tier ?? 1,
            Taunt = (kw & MinionKeywords.Taunt) != 0,
            Poison = (kw & MinionKeywords.Poisonous) != 0,
            Venom = (kw & MinionKeywords.Venomous) != 0,
            Wf = (kw & MinionKeywords.Windfury) != 0,
            Reborn = (kw & MinionKeywords.Reborn) != 0,
            Stealth = (kw & MinionKeywords.Stealth) != 0,
            Beetle = textCard.NameEn == "Beetle",
            Races = Tribes.Parse(v.Tribes) | Tribes.Parse(textCard.Tribes) | Tribes.Parse(card.Tribes),
            Name = card.Name,
        };
    }

    static Minion Plain(MinionView v)
    {
        var kw = v.Keywords;
        return new Minion
        {
            Sc = CardScript.Plain,
            Atk = v.Attack,
            Hp = v.Health,
            MaxHp = v.Health,
            BaseAtk = v.Attack,
            BaseHp = v.Health,
            Ds = (kw & MinionKeywords.DivineShield) != 0 ? 1 : 0,
            Tier = v.TechLevel > 0 ? v.TechLevel : 1,
            Taunt = (kw & MinionKeywords.Taunt) != 0,
            Poison = (kw & MinionKeywords.Poisonous) != 0,
            Venom = (kw & MinionKeywords.Venomous) != 0,
            Wf = (kw & MinionKeywords.Windfury) != 0,
            Reborn = (kw & MinionKeywords.Reborn) != 0,
            Stealth = (kw & MinionKeywords.Stealth) != 0,
            Races = Tribes.Parse(v.Tribes),
            Name = v.Name,
        };
    }

    // ------------------------------------------------------------------ analysis

    const int RDr = 1, RRally = 2, RWatch = 4, RMisc = 8;

    [GeneratedRegex(@"\([^)]*\)")]
    private static partial Regex ParenRx();

    [GeneratedRegex(@"(?<=[.!])\s+")]
    private static partial Regex SentenceRx();

    // "Your Blood Gems give an extra +X/+Y" only resizes gems played later; every card that plays one in combat is hard-listed.
    [GeneratedRegex(@"^\W*(?:(?:Taunt|Reborn|Divine Shield|Venomous|Poisonous|Windfury|Stealth|Magnetic|Deathrattle|Rally)\W*)*$|^\s*(?:Get|Add|Discover|Refresh|Give you|Reduce|Improve|Gain \d+ Gold|Your next)\b|^\s*If you already have one|^\s*(?:You and your teammate each|Your teammate (?:gets|gains))\b|^\s*Activate\b|^\s*Your Blood Gems give an extra|to your hand|into your hand|[Tt]avern|\bGold\b")]
    private static partial Regex HandOnlyRx();

    [GeneratedRegex(@"\b(?:Whenever|After)\b[^.:]*?(?:\battacks\b|\bdies\b|\bdie\b|\bReborn\b|\bsurvives\b|\bkills\b|(?<!hero )takes damage|during combat|in combat)")]
    private static partial Regex WatcherRx();

    /// <summary>"Whenever this kills a minion, give a minion in your hand +2/+2": the hand is not on the board during combat.</summary>
    [GeneratedRegex(@"\b(?:Whenever|After)\b[^,.:]*,\s*give an? (?:random )?minion in your hand [+-]\d+/[+-]\d+\.?")]
    private static partial Regex HandWatcherRx();

    [GeneratedRegex(@"Your Deathrattles trigger|excess damage|died this (?:combat|game)|(?:minions|enemies) next to")]
    private static partial Regex MiscRx();

    [GeneratedRegex(@"(?:minions|enemies) next to")]
    private static partial Regex CleaveRx();

    [GeneratedRegex(@"Summons? (a|an|one|two|three|four|five|six|seven|eight) (\d+)/(\d+) ([A-Za-z'’-]+)")]
    private static partial Regex SummonRx();

    static bool HandOnlyClause(string clause)
    {
        foreach (var s in SentenceRx().Split(clause))
            if (s.Length > 0 && !HandOnlyRx().IsMatch(s)) return false;
        return true;
    }

    static string Clause(string text, string marker)
    {
        var i = text.IndexOf(marker, StringComparison.Ordinal);
        return i < 0 ? "" : text[(i + marker.Length)..];
    }

    static int Words(string w) => w switch
    {
        "a" or "an" or "one" => 1, "two" => 2, "three" => 3, "four" => 4,
        "five" => 5, "six" => 6, "seven" => 7, "eight" => 8, _ => 1,
    };

    public Entry For(CardInfo c)
    {
        if (_memo.TryGetValue(c.Id, out var e)) return e;
        return _memo[c.Id] = Build(c);
    }

    Entry Build(CardInfo c)
    {
        var baseCard = Cards.Resolve(c.Id) ?? c;
        var text = ParenRx().Replace(c.TextEn ?? "", "");
        var hard = HardIds.Contains(baseCard.Id) || HardIds.Contains(c.Id);

        var reasons = 0;
        var drClause = Clause(text, "Deathrattle:");
        if (drClause.Length > 0 && !HandOnlyClause(drClause)) reasons |= RDr;
        var isRally = c.Mechanics.Any(x => x.Equals("BACON_RALLY", StringComparison.OrdinalIgnoreCase));
        var rallyClause = Clause(text, "Rally:");
        if ((isRally || rallyClause.Length > 0) && !HandOnlyClause(rallyClause)) reasons |= RRally;
        if (WatcherRx().IsMatch(HandWatcherRx().Replace(text, ""))) reasons |= RWatch;
        if (MiscRx().IsMatch(text)) reasons |= RMisc;

        var sc = new CardScript { Rally = isRally };
        var derivedCovers = 0;
        if (CleaveRx().IsMatch(text)) { sc.Cleave = true; derivedCovers |= RMisc; }
        if (DeriveSummon(c, text, drClause, out var spec, out var drComplete))
        {
            sc.Summon = spec;
            if (drComplete) derivedCovers |= RDr;
        }

        var handled = false;
        var handFailed = false;
        if (Handlers.TryGetValue(c.Id, out var h) || Handlers.TryGetValue(baseCard.Id, out h))
        {
            var m = h.Rx.Match(text);
            if (m.Success) { h.Apply(m, sc, this); handled = true; }
            else handFailed = true;
        }

        var inCombat = reasons != 0 || hard || handled || handFailed;
        var covered = !handFailed && (handled || (reasons != 0 && (reasons & ~derivedCovers) == 0));
        return new Entry
        {
            Text = c, Base = baseCard,
            Script = sc.HasEffect ? sc : CardScript.Plain,
            InCombat = inCombat, Covered = covered, Hard = hard, HandFailed = handFailed,
        };
    }

    /// <summary>"Deathrattle: Summon two 1/1 Skeletons" and friends, read straight from the card text.</summary>
    bool DeriveSummon(CardInfo c, string text, string drClause, out SummonSpec? spec, out bool complete)
    {
        spec = null;
        complete = false;
        if (!c.Mechanics.Any(x => x.Equals("DEATHRATTLE", StringComparison.OrdinalIgnoreCase)) || drClause.Length == 0)
            return false;
        var m = SummonRx().Match(drClause);
        if (!m.Success) return false;
        var tailEnd = drClause.IndexOf('.', m.Index + m.Length);
        var tail = tailEnd < 0 ? drClause[(m.Index + m.Length)..] : drClause[(m.Index + m.Length)..tailEnd];
        var noun = m.Groups[4].Value;
        if (noun.Length > 1 && noun[^1] == 's') noun = noun[..^1];
        var atk = int.Parse(m.Groups[2].Value);
        var hp = int.Parse(m.Groups[3].Value);
        spec = new SummonSpec(Words(m.Groups[1].Value), atk, hp,
            tail.Contains("Taunt", StringComparison.Ordinal), tail.Contains("Reborn", StringComparison.Ordinal),
            TokenRaces(noun, atk, hp), noun);
        complete = true;
        foreach (var s in SentenceRx().Split(drClause))
            if (s.Length > 0 && !SummonRx().IsMatch(s) && !HandOnlyRx().IsMatch(s)) complete = false;
        return true;
    }

    Tribe TokenRaces(string noun, int atk, int hp)
    {
        var t = Tribes.Parse(noun);
        if (t != Tribe.None) return t;
        if (_minionsByName is null)
        {
            _minionsByName = new(StringComparer.Ordinal);
            foreach (var card in Cards.All)
            {
                if (card.Type != "MINION") continue;
                if (!_minionsByName.TryGetValue(card.NameEn, out var list)) _minionsByName[card.NameEn] = list = [];
                list.Add(card);
            }
        }
        if (!_minionsByName.TryGetValue(noun, out var found)) return Tribe.None;
        var exact = found.FirstOrDefault(x => x.Attack == atk && x.Health == hp && x.Tribes.Count > 0);
        var pick = exact ?? found.FirstOrDefault(x => x.Tribes.Count > 0);
        return pick is null ? Tribe.None : Tribes.Parse(pick.Tribes);
    }

    // ------------------------------------------------------------------ hand-written scripts

    sealed record Handler(Regex Rx, Action<Match, CardScript, ScriptBook> Apply);

    static readonly Dictionary<string, Handler> Handlers = BuildHandlers();

    static int I(Match m, int g) => int.Parse(m.Groups[g].Value);

    static Dictionary<string, Handler> BuildHandlers()
    {
        var d = new Dictionary<string, Handler>(StringComparer.Ordinal);
        void H(string ids, string pattern, Action<Match, CardScript, ScriptBook> apply)
        {
            var h = new Handler(new Regex(pattern, RegexOptions.CultureInvariant), apply);
            foreach (var id in ids.Split(',')) d[id] = h;
        }

        H("BGS_018,TB_BaconUps_085", @"Deathrattle: Your Beasts have \+(\d+)/\+(\d+) until next turn",
            (m, sc, _) => sc.DrBuff = new BuffSpec(I(m, 1), I(m, 2), Tribe.Beast, 0, false));
        H("BG36_202", @"Deathrattle: Give a random friendly Beast \+(\d+)/\+(\d+)",
            (m, sc, _) => sc.DrBuff = new BuffSpec(I(m, 1), I(m, 2), Tribe.Beast, 1, false));
        H("BG28_309", @"Give (a|\d+) different friendly Undead Reborn",
            (m, sc, _) => sc.DrBuff = new BuffSpec(0, 0, Tribe.Undead, m.Groups[1].Value == "a" ? 1 : I(m, 1), true));
        H("BG25_022", @"Deathrattle: Give a friendly Undead \+(\d+)/\+(\d+)",
            (m, sc, _) => sc.DrBuff = new BuffSpec(I(m, 1), I(m, 2), Tribe.Undead, 1, false));
        H("BG31_925", @"Deathrattle: Give all your Naga \+(\d+)/\+(\d+)",
            (m, sc, _) => sc.DrBuff = new BuffSpec(I(m, 1), I(m, 2), Tribe.Naga, 0, false));

        H("BG34_690", @"Deathrattle: Your Undead have \+(\d+) Attack this game", (m, sc, _) =>
        {
            var a = I(m, 1);
            sc.OnDeath = (f, mm, own, en) => UndeadAttack(own, a);
        });
        H("BG33_323", @"Rally: Your Undead have \+(\d+) Attack this game", (m, sc, _) =>
        {
            var a = I(m, 1);
            sc.OnAtk = (f, mm, tg, own, en) => UndeadAttack(own, a);
        });

        H("BG35_604", @"Summon two (Golden )?Sewer Rats that summon (\d+)/(\d+) Turtles", (m, sc, b) =>
        {
            var g = m.Groups[1].Success;
            var rat = b.Cards.Get(g ? "BG19_010_G" : "BG19_010");
            int ra = rat?.Attack ?? (g ? 6 : 3), rh = rat?.Health ?? (g ? 4 : 2);
            var ratSc = new CardScript { Summon = new SummonSpec(1, I(m, 2), I(m, 3), true, false, Tribe.Beast, "Turtle") };
            sc.OnDeath = (f, mm, own, en) =>
            {
                for (var k = 0; k < 2; k++)
                    f.SummonAtDr(own, f.NewToken(ratSc, ra, rh, false, false, Tribe.Beast, "Sewer Rat", 2), en);
            };
        });

        H("BG25_008,BG25_008_G", @"Has \+(\d+)/\+(\d+) for each friendly Eternal Knight that died", (m, sc, _) =>
        {
            sc.KnightA = I(m, 1);
            sc.KnightH = I(m, 2);
            sc.FDeath = KnightWatch;
        });
        H("BG25_009", @"Deathrattle: Summon (?:1|a|an) (Golden )?Eternal Knight", (m, sc, b) =>
        {
            var kc = b.Cards.Get(m.Groups[1].Success ? "BG25_008_G" : "BG25_008");
            if (kc is null) return;
            var km = Regex.Match(kc.TextEn, @"Has \+(\d+)/\+(\d+) for each friendly Eternal Knight");
            if (!km.Success) return;
            int ka = I(km, 1), kh = I(km, 2), ba = kc.Attack ?? 0, bh = kc.Health ?? 1, tier = kc.Tier ?? 2;
            var races = Tribes.Parse(kc.Tribes);
            var ks = new CardScript { KnightA = ka, KnightH = kh, FDeath = KnightWatch };
            sc.OnDeath = (f, mm, own, en) =>
                f.SummonAtDr(own, f.NewToken(ks, ba + ka * own.KnightDeaths, bh + kh * own.KnightDeaths, false, false, races, "Eternal Knight", tier), en);
        });

        H("BG23_318", @"Deathrattle: Destroy the minion that killed this",
            (_, sc, _) => sc.OnDeath = (f, mm, own, en) => { if (mm.Killer is { } k) k.Hp = 0; });

        H("BG31_809", @"Deathrattle: Your Beetles have \+(\d+)/\+(\d+) this game\. Summon (\w+) (\d+)/(\d+) Beetles?", (m, sc, _) =>
        {
            int a = I(m, 1), h = I(m, 2), n = Words(m.Groups[3].Value), ta = I(m, 4), th = I(m, 5);
            sc.Summon = null;
            sc.OnDeath = (f, mm, own, en) =>
            {
                BeetleBuff(own, a, h);
                for (var k = 0; k < n; k++)
                    f.SummonAtDr(own, f.NewToken(CardScript.Plain, ta, th, false, false, Tribe.Beast, "Beetle", mm.Tier), en);
            };
        });
        H("BG31_801", @"Battlecry: Your Beetles have \+(\d+)/\+(\d+) this game", (m, sc, _) =>
        {
            int a = I(m, 1), h = I(m, 2);
            sc.Soc = (f, mm, own, en) => { own.BeetleA += a; own.BeetleH += h; };
        });
        H("BG36_209", @"After a friendly minion attacks, your Beetles have \+(\d+)/\+(\d+) this game", (m, sc, _) =>
        {
            int a = I(m, 1), h = I(m, 2);
            sc.FAtk = (f, w, att, own, en) => BeetleBuff(own, a, h);
        });

        H("BG25_806", @"Deathrattle: Summon a random Beast\. Set its stats to (\d+)/(\d+)",
            (m, sc, _) => sc.Summon = new SummonSpec(1, I(m, 1), I(m, 2), false, false, Tribe.Beast, "Beast"));
        H("BG32_172", @"Summon an? (Golden )?Ancestral Automaton", (m, sc, b) =>
        {
            var ac = b.Cards.Get(m.Groups[1].Success ? "BG_TTN_401_G" : "BG_TTN_401");
            if (ac is not null)
                sc.Summon = new SummonSpec(1, ac.Attack ?? 3, ac.Health ?? 4, false, false, Tribe.Mechanical, "Ancestral Automaton");
        });

        H("BG29_888", @"Rally: Gain \+(\d+) Attack", (m, sc, _) =>
        {
            var a = I(m, 1);
            sc.OnAtk = (f, mm, tg, own, en) => Fighter.Buff(mm, a, 0);
        });
        H("BG36_207", @"Rally: Give your other minions \+(\d+)/\+(\d+)", (m, sc, _) =>
        {
            int a = I(m, 1), h = I(m, 2);
            sc.OnAtk = (f, mm, tg, own, en) =>
            {
                for (var k = 0; k < own.N; k++)
                    if (!ReferenceEquals(own.Ms[k], mm) && own.Ms[k].Hp > 0) Fighter.Buff(own.Ms[k], a, h);
            };
        });
        H("BG36_200", @"Rally: Summon (\w+) (\d+)/(\d+) Beasts?", (m, sc, _) =>
        {
            int n = Words(m.Groups[1].Value), a = I(m, 2), h = I(m, 3);
            sc.OnAtk = (f, mm, tg, own, en) =>
            {
                var idx = own.IndexOf(mm) + 1;
                for (var k = 0; k < n; k++)
                    idx = f.Summon(own, idx, f.NewToken(CardScript.Plain, a, h, false, false, Tribe.Beast, "Beast", mm.Tier), en);
            };
        });
        H("BG27_017,BG27_017_G", @"Rally: Deal damage equal to this minion.s Attack to the target and (an adjacent minion|its neighbors)", (m, sc, _) =>
        {
            var both = m.Groups[1].Value == "its neighbors";
            sc.OnAtk = (f, mm, tg, own, en) =>
            {
                var ti = en.IndexOf(tg);
                var dmg = mm.Atk;
                var hasL = ti > 0;
                var hasR = ti >= 0 && ti + 1 < en.N;
                var l = hasL ? en.Ms[ti - 1] : null;
                var r = hasR ? en.Ms[ti + 1] : null;
                Fighter.Damage(tg, dmg);
                if (both) { if (l is not null) Fighter.Damage(l, dmg); if (r is not null) Fighter.Damage(r, dmg); }
                else if (l is not null && r is not null) Fighter.Damage(f.Rand(2) == 0 ? l : r, dmg);
                else if (l is not null || r is not null) Fighter.Damage((l ?? r)!, dmg);
            };
        });
        H("BG25_016", @"Rally: Remove Reborn and Taunt from the target",
            (_, sc, _) => sc.OnAtk = (f, mm, tg, own, en) => { tg.Reborn = false; tg.Taunt = false; });
        H("BG34_604", @"Rally: Gain (double )?the target.s Attack", (m, sc, _) =>
        {
            var mult = m.Groups[1].Success ? 2 : 1;
            sc.OnAtk = (f, mm, tg, own, en) => Fighter.Buff(mm, tg.Atk * mult, 0);
        });
        H("BG33_318", @"Rally: Give (?:another|(\d+) other) friendly Murlocs? Venomous", (m, sc, _) =>
        {
            var n = m.Groups[1].Success ? I(m, 1) : 1;
            sc.OnAtk = (f, mm, tg, own, en) =>
            {
                var k = f.Pick(own, mm, Tribe.Murloc, n, false);
                for (var j = 0; j < k; j++) f.Cand[j].Venom = true;
            };
        });

        H("BG36_514", @"After a friendly minion is Reborn, gain Divine Shield and \+(\d+)/\+(\d+)", (m, sc, _) =>
        {
            int a = I(m, 1), h = I(m, 2);
            sc.OnReborn = (f, w, rb, own, en) => { w.Ds = 1; Fighter.Buff(w, a, h); };
        });
        H("BG36_515", @"give stats equal to (double )?its Attack to your right-most Undead", (m, sc, _) =>
        {
            var mult = m.Groups[1].Success ? 2 : 1;
            sc.OnReborn = (f, w, rb, own, en) =>
            {
                for (var k = own.N - 1; k >= 0; k--)
                {
                    var u = own.Ms[k];
                    if (u.Hp > 0 && u.Has(Tribe.Undead)) { Fighter.Buff(u, w.Atk * mult, w.Atk * mult); return; }
                }
            };
        });
        H("BGS_071,TB_BaconUps_123", @"Whenever you summon a Mech during combat, gain \+(\d+) Attack and Divine Shield", (m, sc, _) =>
        {
            var a = I(m, 1);
            sc.OnSummon = (f, w, tok, own, en) => { if (tok.Has(Tribe.Mechanical)) { w.Atk += a; w.Ds = 1; } };
        });
        H("BGS_012,TB_BaconUps_087", @"Summon plain copies of your first (\d+) Mechs that died", (m, sc, _) =>
        {
            var cap = Math.Min(I(m, 1), 4);
            sc.FDeath = (f, w, subject, own, en) =>
            {
                if (!subject.Has(Tribe.Mechanical) || own.DeadN >= own.DeadMechs.Length) return;
                for (var k = 0; k < own.DeadN; k++) if (ReferenceEquals(own.DeadMechs[k], subject)) return;
                own.DeadMechs[own.DeadN++] = subject;
            };
            sc.OnDeath = (f, mm, own, en) =>
            {
                for (var k = 0; k < own.DeadN && k < cap; k++)
                {
                    var c = f.Clone(own.DeadMechs[k]!);
                    c.Atk = c.BaseAtk; c.Hp = c.MaxHp = c.BaseHp;
                    c.Gained = null; c.Killer = null; c.Ds = 0; c.Reborn = false; c.Stealth = false;
                    f.SummonAtDr(own, c, en);
                }
            };
        });
        H("BG27_080", @"Give a friendly minion of each type \+(\d+)/\+(\d+)", (m, sc, _) =>
        {
            int a = I(m, 1), h = I(m, 2);
            sc.OnDeath = (f, mm, own, en) =>
            {
                foreach (var t in Tribes.Concrete)
                    if (f.Pick(own, mm, t, 1, false) > 0) Fighter.Buff(f.Cand[0], a, h);
            };
        });
        H("BG25_354", @"Your Deathrattles trigger (?:an extra time|(\d+) extra times)",
            (m, sc, _) => sc.DrRep = m.Groups[1].Success ? I(m, 1) : 1);
        H("BG36_208", @"After a friendly Rally minion attacks, trigger your left-most Deathrattle( twice)?", (m, sc, _) =>
        {
            var times = m.Groups[1].Success ? 2 : 1;
            sc.FAtk = (f, w, att, own, en) =>
            {
                if (!att.Sc.Rally) return;
                for (var k = 0; k < own.N; k++)
                {
                    var t = own.Ms[k];
                    if (t.Hp <= 0 || !(t.Sc.HasDeathrattle || t.Gained is not null)) continue;
                    var idx = k + 1;
                    for (var r = 0; r < times; r++) idx = f.Deathrattle(t, own, en, idx);
                    return;
                }
            };
        });
        H("BGS_126,TB_BaconUps_166", @"deal excess damage to (an adjacent enemy|both adjacent enemies)",
            (m, sc, _) => sc.Overkill = m.Groups[1].Value.StartsWith("both", StringComparison.Ordinal) ? 2 : 1);
        H("TB_BaconShop_HP_105t,TB_BaconUps_307", @"After a different friendly Deathrattle minion dies in combat, gain its Deathrattle( twice)?", (m, sc, _) =>
        {
            var times = m.Groups[1].Success ? 2 : 1;
            sc.FDeath = (f, w, subject, own, en) =>
            {
                var sub = subject.Sc;
                var has = sub.HasDeathrattle;
                if (!has && subject.Gained is null) return;
                var list = new List<DrRec>(w.Gained ?? []);
                for (var t = 0; t < times; t++)
                {
                    if (has) list.Add(new DrRec(sub.Summon, sub.DrBuff, sub.OnDeath, subject.Tier));
                    if (subject.Gained is { } g) list.AddRange(g);
                }
                w.Gained = list.ToArray();
            };
        });

        H("BG33_828,BG33_828_G", @"Deathrattle: Give your minions \+(\d+)/\+(\d+)( twice)?", (m, sc, _) =>
        {
            int a = I(m, 1), h = I(m, 2), times = m.Groups[3].Success ? 2 : 1;
            sc.OnDeath = (f, mm, own, en) =>
            {
                for (var k = 0; k < own.N; k++)
                    if (own.Ms[k].Hp > 0)
                        for (var t = 0; t < times; t++) Fighter.Buff(own.Ms[k], a, h);
            };
        });
        // The Tentacles come from the derived Summon, which runs first, so they get the Attack too.
        H("BG36_116,BG36_116_G", @"Deathrattle: Summon [^.]*\. Give your minions \+(\d+) Attack", (m, sc, _) =>
        {
            var a = I(m, 1);
            sc.OnDeath = (f, mm, own, en) =>
            {
                for (var k = 0; k < own.N; k++)
                    if (own.Ms[k].Hp > 0) Fighter.Buff(own.Ms[k], a, 0);
            };
        });
        H("BG29_816,BG29_816_G", @"Whenever another friendly Dragon attacks, give it \+(\d+)/\+(\d+)", (m, sc, _) =>
        {
            int a = I(m, 1), h = I(m, 2);
            sc.FAtk = (f, w, att, own, en) => { if (!ReferenceEquals(w, att) && att.Has(Tribe.Dragon)) Fighter.Buff(att, a, h); };
        });
        H("BG36_211,BG36_211_G", @"Whenever a friendly Beast attacks, give your Beasts \+(\d+)/\+(\d+)", (m, sc, _) =>
        {
            int a = I(m, 1), h = I(m, 2);
            sc.FAtk = (f, w, att, own, en) =>
            {
                if (!att.Has(Tribe.Beast)) return;
                for (var k = 0; k < own.N; k++)
                    if (own.Ms[k].Hp > 0 && own.Ms[k].Has(Tribe.Beast)) Fighter.Buff(own.Ms[k], a, h);
            };
        });
        H("BG26_802,BG26_802_G", @"After you summon a Beast in combat, (double|triple) its Attack", (m, sc, _) =>
        {
            var mult = m.Groups[1].Value == "double" ? 2 : 3;
            sc.OnSummon = (f, w, tok, own, en) => { if (tok.Has(Tribe.Beast)) tok.Atk *= mult; };
        });
        H("BG25_013,BG25_013_G", @"Has \+(\d+) Attack for each friendly minion that died this combat",
            (m, sc, _) => sc.DeathA = I(m, 1));
        return d;
    }

    static void UndeadAttack(Side own, int a)
    {
        own.UndeadAtk += a;
        for (var k = 0; k < own.N; k++)
            if (own.Ms[k].Hp > 0 && own.Ms[k].Has(Tribe.Undead)) own.Ms[k].Atk += a;
    }

    static void BeetleBuff(Side own, int a, int h)
    {
        own.BeetleA += a;
        own.BeetleH += h;
        for (var k = 0; k < own.N; k++)
            if (own.Ms[k].Beetle) Fighter.Buff(own.Ms[k], a, h);
    }

    static void KnightWatch(Fighter f, Minion w, Minion subject, Side own, Side enemy)
    {
        if (subject.Sc.KnightA > 0) Fighter.Buff(w, w.Sc.KnightA, w.Sc.KnightH);
    }
}
