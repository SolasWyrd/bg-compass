using BgCompass.Core;
using BgCompass.Core.Cards;
using BgCompass.Core.Sim;
using Xunit;

namespace BgCompass.Tests;

/// <summary>One focused fight per hand-scripted card, with seeded RNG. Card numbers come from the embedded snapshot's own text.</summary>
public class SimCardTests
{
    static readonly CardCatalog Cards = CardCatalog.LoadEmbedded();
    static readonly ScriptBook Book = new(Cards);

    const string Skeleton = "BG_ICC_026t"; // 1/1 Undead token with no text
    const string Eudora = "BG33_828", Underrot = "BG36_116", Recruiter = "BG29_816", Gnawer = "BG36_211",
        Slamma = "BG26_802", Gnoll = "BG25_013";

    static MinionView V(string id, int atk, int hp, MinionKeywords kw = MinionKeywords.None, bool golden = false) =>
        new(0, id, id, atk, hp, 0, golden, kw, [], 1);

    static MinionView P(int atk, int hp) => V(Skeleton, atk, hp);

    static string Golden(string baseId) => Cards.GetByDbf(Cards.Get(baseId)!.PremiumDbfId)!.Id;

    static Minion T(MinionView v)
    {
        var m = Book.Template(v, out _, out _, out var err);
        Assert.True(m is not null, err);
        return m!;
    }

    static Minion[] Ts(params MinionView[] v) => v.Select(T).ToArray();

    static Fighter Setup(ulong seed, MinionView[] a, params MinionView[] b)
    {
        var f = new Fighter(seed);
        f.Setup(Ts(a), Ts(b));
        return f;
    }

    static void Kill(Fighter f, int index)
    {
        f.A.Ms[index].Hp = 0;
        f.ResolveDeaths(f.A, f.B);
    }

    static Entry EntryOf(string id)
    {
        Book.Template(V(id, 1, 1), out var entry, out _, out _);
        return entry!;
    }

    // ---- classification ---------------------------------------------------------------------

    [Theory]
    [InlineData("BG27_023")] // Murcules: buffs a minion in hand
    [InlineData("BG29_300")] // Very Hungry Winterfinner: buffs a minion in hand
    [InlineData("BG26_174")] // Soul Rewinder: reacts to hero damage, which is dealt after combat
    [InlineData("BG36_521")] // Locked-up Mutineer: Lockbox is a Tavern item
    [InlineData("BG33_885")] // Sanguine Refiner: only resizes later Blood Gems (every Blood Gem user is hard-listed)
    public void Cards_that_do_nothing_on_the_board_do_not_widen_the_odds(string id)
    {
        Assert.False(EntryOf(id).Unmodelled);
    }

    [Theory]
    [InlineData("BG31_999")] // Stitched Salvager: its target is destroyed before the snapshot is taken
    [InlineData("BG33_886")] // Tusked Camper: gem size depends on state the combat log does not show
    [InlineData("BG25_011")] // Nerubian Deathswarmer: the Undead Attack bonus was earned in the Tavern
    public void Cards_that_cannot_be_modelled_still_widen_the_odds(string id)
    {
        Assert.True(EntryOf(id).Unmodelled);
    }

    [Theory]
    [InlineData(Eudora)]
    [InlineData(Underrot)]
    [InlineData(Recruiter)]
    [InlineData(Gnawer)]
    [InlineData(Slamma)]
    [InlineData(Gnoll)]
    public void Scripted_cards_and_their_goldens_are_covered(string id)
    {
        Assert.False(EntryOf(id).Unmodelled);
        Assert.False(EntryOf(Golden(id)).Unmodelled);
    }

    // ---- Ship Master Eudora -----------------------------------------------------------------

    [Fact]
    public void Eudora_buffs_every_living_friendly_minion_once_and_golden_twice()
    {
        var f = Setup(1, [V(Eudora, 10, 3), P(1, 5), P(2, 2)], P(1, 1));
        var text = Cards.Get(Eudora)!.TextEn;
        var n = int.Parse(System.Text.RegularExpressions.Regex.Match(text, @"\+(\d+)/\+\d+").Groups[1].Value);
        Kill(f, 0);
        Assert.Equal(2, f.A.N);
        Assert.Equal([(1 + n, 5 + n), (2 + n, 2 + n)], f.A.Ms.Take(2).Select(m => (m.Atk, m.Hp)));

        var g = Setup(1, [V(Golden(Eudora), 20, 6, golden: true), P(1, 5)], P(1, 1));
        Kill(g, 0);
        Assert.Equal((1 + 2 * n, 5 + 2 * n), (g.A.Ms[0].Atk, g.A.Ms[0].Hp));
    }

    // ---- Underrot Spawn ---------------------------------------------------------------------

    [Fact]
    public void Underrot_spawn_summons_taunt_tentacles_and_gives_all_minions_attack_including_them()
    {
        var f = Setup(2, [V(Underrot, 2, 2), P(1, 3)], P(1, 1));
        Kill(f, 0);
        Assert.Equal(2, f.A.N);
        Assert.Equal((1, 2, true), (f.A.Ms[0].Atk, f.A.Ms[0].Hp, f.A.Ms[0].Taunt)); // 0/2 Tentacle +1 Attack
        Assert.Equal((2, 3), (f.A.Ms[1].Atk, f.A.Ms[1].Hp));

        var g = Setup(2, [V(Golden(Underrot), 4, 4, golden: true), P(1, 3)], P(1, 1));
        Kill(g, 0);
        Assert.Equal(3, g.A.N);
        Assert.All(new[] { g.A.Ms[0], g.A.Ms[1] }, t => Assert.Equal((2, 2, true), (t.Atk, t.Hp, t.Taunt)));
        Assert.Equal(3, g.A.Ms[2].Atk);
    }

    // ---- Roaring Recruiter ------------------------------------------------------------------

    [Fact]
    public void Roaring_recruiter_buffs_another_attacking_dragon_but_not_itself_or_other_tribes()
    {
        var wall = P(0, 100);
        var alone = Setup(3, [V(Recruiter, 2, 8), P(1, 5)], wall);
        alone.Attack(alone.A.Ms[0], alone.A, alone.B);
        Assert.Equal((2, 8), (alone.A.Ms[0].Atk, alone.A.Ms[0].Hp)); // no other Dragon: nothing
        alone.Attack(alone.A.Ms[1], alone.A, alone.B);
        Assert.Equal((1, 5), (alone.A.Ms[1].Atk, alone.A.Ms[1].Hp));  // an Undead is not a Dragon

        var pair = Setup(3, [V(Recruiter, 2, 8), V(Recruiter, 2, 8)], wall);
        var (a, h) = (3, 1);
        var rx = System.Text.RegularExpressions.Regex.Match(Cards.Get(Recruiter)!.TextEn, @"\+(\d+)/\+(\d+)");
        (a, h) = (int.Parse(rx.Groups[1].Value), int.Parse(rx.Groups[2].Value));
        pair.Attack(pair.A.Ms[0], pair.A, pair.B);
        Assert.Equal((2 + a, 8 + h), (pair.A.Ms[0].Atk, pair.A.Ms[0].Hp)); // buffed once, by the other one
        Assert.Equal((2, 8), (pair.A.Ms[1].Atk, pair.A.Ms[1].Hp));
    }

    // ---- Cage Gnawer ------------------------------------------------------------------------

    [Fact]
    public void Cage_gnawer_buffs_all_beasts_when_a_beast_attacks_including_itself()
    {
        var rx = System.Text.RegularExpressions.Regex.Match(Cards.Get(Gnawer)!.TextEn, @"\+(\d+)/\+(\d+)");
        int a = int.Parse(rx.Groups[1].Value), h = int.Parse(rx.Groups[2].Value);
        var wall = P(0, 100);

        var f = Setup(4, [V(Gnawer, 2, 7), V(Slamma, 3, 6), P(1, 5)], wall);
        f.Attack(f.A.Ms[2], f.A, f.B); // an Undead attacks: no Beast attacked
        Assert.Equal([(2, 7), (3, 6), (1, 5)], f.A.Ms.Take(3).Select(m => (m.Atk, m.Hp)));
        f.Attack(f.A.Ms[1], f.A, f.B); // the Slamma (a Beast) attacks
        Assert.Equal([(2 + a, 7 + h), (3 + a, 6 + h), (1, 5)], f.A.Ms.Take(3).Select(m => (m.Atk, m.Hp)));
    }

    // ---- Banana Slamma ----------------------------------------------------------------------

    [Fact]
    public void Banana_slamma_doubles_a_summoned_beast_triples_when_golden_and_ignores_other_tribes()
    {
        var f = Setup(5, [V(Slamma, 3, 6)], P(1, 1));
        f.Summon(f.A, 1, f.NewToken(CardScript.Plain, 2, 2, false, false, Tribe.Beast, "Beast", 1), f.B);
        f.Summon(f.A, 2, f.NewToken(CardScript.Plain, 2, 2, false, false, Tribe.Undead, "Zombie", 1), f.B);
        Assert.Equal([3, 4, 2], f.A.Ms.Take(3).Select(m => m.Atk));

        var g = Setup(5, [V(Golden(Slamma), 6, 12, golden: true)], P(1, 1));
        g.Summon(g.A, 1, g.NewToken(CardScript.Plain, 2, 2, false, false, Tribe.Beast, "Beast", 1), g.B);
        Assert.Equal(6, g.A.Ms[1].Atk);
    }

    [Fact]
    public void Banana_slamma_also_sees_a_reborn_beast_come_back()
    {
        // The Reborn Slamma returns with its printed 3 Attack; the second Slamma doubles it.
        var f = Setup(6, [V(Slamma, 9, 9, MinionKeywords.Reborn), V(Slamma, 3, 6)], P(1, 1));
        Kill(f, 0);
        Assert.Equal(2, f.A.N);
        Assert.Equal((6, 1), (f.A.Ms[0].Atk, f.A.Ms[0].Hp));
    }

    // ---- Rot Hide Gnoll ---------------------------------------------------------------------

    [Fact]
    public void Rot_hide_gnoll_gains_attack_for_each_friendly_death_and_ignores_enemy_ones()
    {
        var f = Setup(7, [V(Gnoll, 1, 4), P(1, 1), P(1, 1)], P(1, 1), P(1, 1));
        Kill(f, 1);
        Assert.Equal(2, f.A.Ms[0].Atk);
        f.B.Ms[0].Hp = 0;
        f.ResolveDeaths(f.A, f.B);
        Assert.Equal(2, f.A.Ms[0].Atk); // an enemy death changes nothing
        Kill(f, 1);
        Assert.Equal(3, f.A.Ms[0].Atk);

        var g = Setup(7, [V(Golden(Gnoll), 2, 8, golden: true), P(1, 1)], P(1, 1));
        Kill(g, 1);
        Assert.Equal(4, g.A.Ms[0].Atk);
    }

    [Fact]
    public void A_reborn_gnoll_comes_back_with_the_deaths_so_far_counting_its_own()
    {
        var f = Setup(8, [V(Gnoll, 1, 4, MinionKeywords.Reborn), P(1, 1)], P(1, 1));
        Kill(f, 1);                 // 1 death: the living Gnoll goes to 2
        Assert.Equal(2, f.A.Ms[0].Atk);
        Kill(f, 0);                 // the Gnoll dies (2 deaths) and returns at printed 1 + 2
        Assert.Equal(1, f.A.N);
        Assert.Equal((3, 1), (f.A.Ms[0].Atk, f.A.Ms[0].Hp));
    }

    // ---- through the public simulator -------------------------------------------------------

    [Fact]
    public void Gnawer_and_recruiter_fights_run_without_unmodelled_cards()
    {
        var a = new[] { V(Gnawer, 2, 7), V(Slamma, 3, 6) }.Select((m, i) => m with { EntityId = i + 1, Position = i }).ToList();
        var b = new[] { P(4, 4) }.Select((m, i) => m with { EntityId = 10 + i, Position = i }).ToList();
        var snap = new CombatSnapshot(5, 2, "TB_BaconShop_HERO_01", a, b, 3, 3, 30, null, true, null);
        var r = CombatSimulator.Run(snap, Cards, 500, 7);
        Assert.True(r.Available);
        Assert.Empty(r.Unmodelled);
        Assert.Equal(1.0, r.Win + r.Tie + r.Loss, 9);
    }
}
