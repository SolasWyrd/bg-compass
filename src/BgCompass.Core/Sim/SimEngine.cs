// Combat rules ported from bgtracker sim/engine.py (MIT License); C# port for BG Compass.
namespace BgCompass.Core.Sim;

[Flags]
internal enum Tribe
{
    None = 0, Beast = 1, Demon = 2, Dragon = 4, Elemental = 8, Mechanical = 16,
    Murloc = 32, Naga = 64, Pirate = 128, Quilboar = 256, Undead = 512, All = 1024,
}

internal static class Tribes
{
    /// <summary>Every concrete tribe, in the alphabetical order engine.py iterates them (Motley Phalanx).</summary>
    public static readonly Tribe[] Concrete =
    [
        Tribe.Beast, Tribe.Demon, Tribe.Dragon, Tribe.Elemental, Tribe.Mechanical,
        Tribe.Murloc, Tribe.Naga, Tribe.Pirate, Tribe.Quilboar, Tribe.Undead,
    ];

    public static Tribe Parse(string? name) => name?.Trim().ToUpperInvariant() switch
    {
        "BEAST" => Tribe.Beast,
        "DEMON" => Tribe.Demon,
        "DRAGON" => Tribe.Dragon,
        "ELEMENTAL" => Tribe.Elemental,
        "MECHANICAL" or "MECH" => Tribe.Mechanical,
        "MURLOC" => Tribe.Murloc,
        "NAGA" => Tribe.Naga,
        "PIRATE" => Tribe.Pirate,
        "QUILBOAR" => Tribe.Quilboar,
        "UNDEAD" => Tribe.Undead,
        "ALL" => Tribe.All,
        _ => Tribe.None,
    };

    public static Tribe Parse(IEnumerable<string>? names)
    {
        var t = Tribe.None;
        if (names is not null) foreach (var n in names) t |= Parse(n);
        return t;
    }
}

internal delegate void DeathHook(Fighter f, Minion m, Side own, Side enemy);
internal delegate void StartHook(Fighter f, Minion m, Side own, Side enemy);
internal delegate void AttackHook(Fighter f, Minion m, Minion target, Side own, Side enemy);
internal delegate void WatchHook(Fighter f, Minion watcher, Minion subject, Side own, Side enemy);

internal sealed record SummonSpec(int Count, int Atk, int Hp, bool Taunt, bool Reborn, Tribe Races, string Name);

/// <summary>Declarative deathrattle buff. Count 0 = every match.</summary>
internal sealed record BuffSpec(int Atk, int Hp, Tribe Race, int Count, bool GrantReborn);

/// <summary>One deathrattle, the minion's own or one it gained (Fish of N'Zoth).</summary>
internal readonly record struct DrRec(SummonSpec? Summon, BuffSpec? Buff, DeathHook? Od, int Tier);

internal sealed class Minion
{
    public CardScript Sc = CardScript.Plain;
    public int Atk, Hp, MaxHp, BaseAtk, BaseHp, Ds, Tier;
    public bool Taunt, Poison, Venom, Wf, Reborn, Stealth, Beetle;
    public Tribe Races;
    public Minion? Killer;
    public DrRec[]? Gained;
    public string Name = "";

    public bool Has(Tribe t) => (Races & (t | Tribe.All)) != 0;

    public void CopyFrom(Minion o)
    {
        Sc = o.Sc; Atk = o.Atk; Hp = o.Hp; MaxHp = o.MaxHp; BaseAtk = o.BaseAtk; BaseHp = o.BaseHp;
        Ds = o.Ds; Tier = o.Tier; Taunt = o.Taunt; Poison = o.Poison; Venom = o.Venom; Wf = o.Wf;
        Reborn = o.Reborn; Stealth = o.Stealth; Beetle = o.Beetle; Races = o.Races;
        Killer = o.Killer; Gained = o.Gained; Name = o.Name;
    }
}

internal sealed class Side
{
    public readonly Minion[] Ms = new Minion[Fighter.MaxBoard + 1];
    public int N, Ptr, BeetleA, BeetleH, UndeadAtk, KnightDeaths, DeadN, Deaths;
    public readonly Minion?[] DeadMechs = new Minion?[4];
    public bool WAtk, WDeath, WReborn, WSummon, WRep, WDeathAtk;

    public void Load(Minion[] templates, Fighter f)
    {
        N = 0; Ptr = 0; BeetleA = 0; BeetleH = 0; UndeadAtk = 0; KnightDeaths = 0; DeadN = 0; Deaths = 0;
        WAtk = WDeath = WReborn = WSummon = WRep = WDeathAtk = false;
        foreach (var t in templates)
        {
            var m = f.Clone(t);
            Ms[N++] = m;
            var sc = m.Sc;
            if (sc.FAtk is not null) WAtk = true;
            if (sc.FDeath is not null) WDeath = true;
            if (sc.OnReborn is not null) WReborn = true;
            if (sc.OnSummon is not null) WSummon = true;
            if (sc.DrRep != 0) WRep = true;
            if (sc.DeathA != 0) WDeathAtk = true;
        }
        Array.Clear(Ms, N, Ms.Length - N);
        Array.Clear(DeadMechs);
    }

    public int IndexOf(Minion m)
    {
        for (var i = 0; i < N; i++) if (ReferenceEquals(Ms[i], m)) return i;
        return -1;
    }

    /// <summary>How many times each of this side's deathrattles fires; an aura stops when its minion leaves.</summary>
    public int DrRepeats()
    {
        if (!WRep) return 1;
        var r = 1;
        for (var i = 0; i < N; i++) r += Ms[i].Sc.DrRep;
        return r;
    }

    public int NextAttackerIdx()
    {
        if (N == 0) return -1;
        var p = Ptr % N;
        for (var k = 0; k < N; k++)
        {
            var i = p + k;
            if (i >= N) i -= N;
            if (Ms[i].Atk > 0) return i;
        }
        return -1;
    }

    public void Remove(int i)
    {
        Array.Copy(Ms, i + 1, Ms, i, N - i - 1);
        N--;
        Ms[N] = null!;
        if (i < Ptr) Ptr--;
    }

    public void Insert(int i, Minion m)
    {
        Array.Copy(Ms, i, Ms, i + 1, N - i);
        Ms[i] = m;
        N++;
        if (m.Sc.DeathA != 0) WDeathAtk = true;
        if (i < Ptr) Ptr++;
    }
}

internal readonly record struct FightResult(int Result, int Damage);

/// <summary>
/// One combat at a time, with every object recycled between fights: after warm-up a fight allocates
/// nothing (except Fish of N'Zoth's gained-deathrattle arrays). Owned by exactly one thread.
/// </summary>
internal sealed class Fighter
{
    public const int MaxBoard = 7;
    public const int MaxAttacks = 1000;

    public readonly Side A = new();
    public readonly Side B = new();

    ulong _state;
    Minion?[] _pool = new Minion?[96];
    int _used;
    readonly Minion[] _cand = new Minion[MaxBoard + 1];
    Minion[][] _snap = new Minion[8][];
    int[] _snapN = new int[8];
    int _depth;

    public Fighter(ulong seed) => _state = seed;

    // ---- RNG (SplitMix64; unbiased enough for n <= 16) ----
    public int Rand(int n)
    {
        _state += 0x9E3779B97F4A7C15UL;
        var z = _state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        return (int)(((z >> 32) * (ulong)n) >> 32);
    }

    // ---- minion recycling ----
    Minion New()
    {
        if (_used == _pool.Length) Array.Resize(ref _pool, _used * 2);
        var m = _pool[_used] ??= new Minion();
        _used++;
        return m;
    }

    public Minion Clone(Minion src)
    {
        var m = New();
        m.CopyFrom(src);
        return m;
    }

    /// <summary>A fresh token. Base stats equal the printed ones, as in engine.py's Minion().</summary>
    public Minion NewToken(CardScript sc, int atk, int hp, bool taunt, bool reborn, Tribe races, string name, int tier)
    {
        var m = New();
        m.Sc = sc; m.Atk = atk; m.Hp = hp; m.MaxHp = hp; m.BaseAtk = atk; m.BaseHp = hp;
        m.Ds = 0; m.Tier = tier; m.Taunt = taunt; m.Poison = false; m.Venom = false; m.Wf = false;
        m.Reborn = reborn; m.Stealth = false; m.Beetle = name == "Beetle"; m.Races = races;
        m.Killer = null; m.Gained = null; m.Name = name;
        return m;
    }

    int SnapBegin(Side s)
    {
        if (_depth == _snap.Length)
        {
            Array.Resize(ref _snap, _depth * 2);
            Array.Resize(ref _snapN, _depth * 2);
        }
        var b = _snap[_depth] ??= new Minion[MaxBoard + 1];
        Array.Copy(s.Ms, b, s.N);
        _snapN[_depth] = s.N;
        return _depth++;
    }

    void SnapEnd() => _depth--;

    // ---- engine.py combat primitives ----

    /// <summary>Plain damage from no attacker. Returns the overkill excess.</summary>
    public static int Damage(Minion dst, int amount)
    {
        if (amount <= 0) return 0;
        if (dst.Ds > 0) { dst.Ds--; return 0; }
        var before = dst.Hp;
        dst.Hp -= amount;
        return before < amount ? amount - before : 0;
    }

    static int Hit(Minion src, Minion dst)
    {
        var a = src.Atk;
        if (a <= 0) return 0;
        if (dst.Ds > 0) { dst.Ds--; return 0; }
        var before = dst.Hp;
        dst.Hp -= a;
        if ((src.Poison || src.Venom) && dst.Hp > 0) dst.Hp = 0;
        if (src.Venom && dst.Hp <= 0) src.Venom = false;
        if (dst.Hp <= 0) dst.Killer = src;
        return before < a ? a - before : 0;
    }

    public Minion PickDefender(Side d)
    {
        int n = 0;
        for (var i = 0; i < d.N; i++) if (d.Ms[i].Taunt && !d.Ms[i].Stealth) n++;
        if (n > 0) return Nth(d, n, static m => m.Taunt && !m.Stealth);
        for (var i = 0; i < d.N; i++) if (!d.Ms[i].Stealth) n++;
        if (n > 0) return Nth(d, n, static m => !m.Stealth);
        return d.Ms[d.N == 1 ? 0 : Rand(d.N)];
    }

    Minion Nth(Side d, int n, Func<Minion, bool> pred)
    {
        var k = n == 1 ? 0 : Rand(n);
        for (var i = 0; i < d.N; i++)
            if (pred(d.Ms[i]) && k-- == 0) return d.Ms[i];
        throw new InvalidOperationException("unreachable");
    }

    public static void Buff(Minion m, int atk, int hp)
    {
        m.Atk += atk;
        m.Hp += hp;
        m.MaxHp += hp;
    }

    /// <summary>Insert a summoned token, respecting the cap, and fire summon watchers.</summary>
    public int Summon(Side side, int i, Minion tok, Side other)
    {
        if (side.N >= MaxBoard) return i;
        if (tok.Beetle) Buff(tok, side.BeetleA, side.BeetleH);
        if (tok.Has(Tribe.Undead)) tok.Atk += side.UndeadAtk;
        tok.Atk += tok.Sc.DeathA * side.Deaths;
        side.Insert(i, tok);
        FireSummon(side, tok, other);
        return i + 1;
    }

    void FireSummon(Side side, Minion tok, Side other)
    {
        if (!side.WSummon) return;
        var d = SnapBegin(side);
        for (var k = 0; k < _snapN[d]; k++)
        {
            var w = _snap[d][k];
            if (w.Sc.OnSummon is { } h && w.Hp > 0 && !ReferenceEquals(w, tok)) h(this, w, tok, side, other);
        }
        SnapEnd();
    }

    /// <summary>Scratch list filled by <see cref="Pick"/>.</summary>
    public Minion[] Cand => _cand;

    /// <summary>
    /// Living friendly candidates other than <paramref name="src"/> (optionally of one tribe, optionally not already
    /// Reborn), at most <paramref name="count"/> of them chosen at random (0 = all). Result is in <see cref="Cand"/>.
    /// </summary>
    public int Pick(Side side, Minion src, Tribe race, int count, bool skipReborn)
    {
        var n = 0;
        for (var k = 0; k < side.N; k++)
        {
            var m = side.Ms[k];
            if (m.Hp <= 0 || ReferenceEquals(m, src)) continue;
            if (race != Tribe.None && !m.Has(race)) continue;
            if (skipReborn && m.Reborn) continue;
            _cand[n++] = m;
        }
        if (count > 0 && n > count)
        {
            for (var j = 0; j < count; j++)
            {
                var r = j + Rand(n - j);
                (_cand[j], _cand[r]) = (_cand[r], _cand[j]);
            }
            n = count;
        }
        return n;
    }

    void ApplyBuff(BuffSpec spec, Side side, Minion src)
    {
        var n = Pick(side, src, spec.Race, spec.Count, spec.GrantReborn);
        for (var j = 0; j < n; j++)
        {
            var m = _cand[j];
            if (spec.Atk != 0 || spec.Hp != 0) Buff(m, spec.Atk, spec.Hp);
            if (spec.GrantReborn) m.Reborn = true;
        }
    }

    /// <summary>Insertion index for tokens a deathrattle hook summons; hooks advance it via <see cref="SummonAtDr"/>.</summary>
    public int DrIdx;

    public void SummonAtDr(Side side, Minion tok, Side other) => DrIdx = Summon(side, DrIdx, tok, other);

    int Replay(in DrRec rec, Minion m, Side side, Side other, int i)
    {
        if (rec.Summon is { } sp)
        {
            for (var c = 0; c < sp.Count; c++)
            {
                if (side.N >= MaxBoard) break;
                i = Summon(side, i, NewToken(CardScript.Plain, sp.Atk, sp.Hp, sp.Taunt, sp.Reborn, sp.Races, sp.Name, rec.Tier), other);
            }
        }
        if (rec.Buff is { } b) ApplyBuff(b, side, m);
        if (rec.Od is { } od)
        {
            DrIdx = i;
            od(this, m, side, other);
            i = DrIdx;
        }
        return i;
    }

    /// <summary>Everything one death (or one Deathstrider trigger) does: printed deathrattle, then gained ones.</summary>
    public int Deathrattle(Minion m, Side side, Side other, int i)
    {
        var sc = m.Sc;
        i = Replay(new DrRec(sc.Summon, sc.DrBuff, sc.OnDeath, m.Tier), m, side, other, i);
        if (m.Gained is { } g) foreach (var rec in g) i = Replay(rec, m, side, other, i);
        return i;
    }

    /// <summary>Remove the dead, run deathrattles (attacking side first, board order), then reborn - until stable.</summary>
    public void ResolveDeaths(Side first, Side second)
    {
        var changed = true;
        while (changed)
        {
            changed = false;
            for (var pass = 0; pass < 2; pass++)
            {
                var side = pass == 0 ? first : second;
                var other = pass == 0 ? second : first;
                var i = 0;
                while (i < side.N)
                {
                    var m = side.Ms[i];
                    if (m.Hp > 0) { i++; continue; }
                    changed = true;
                    side.Remove(i);
                    if (m.Sc.KnightA > 0) side.KnightDeaths++;
                    side.Deaths++;
                    if (side.WDeathAtk)
                        for (var k = 0; k < side.N; k++)
                            if (side.Ms[k].Sc.DeathA != 0 && side.Ms[k].Hp > 0) side.Ms[k].Atk += side.Ms[k].Sc.DeathA;
                    for (int r = 0, reps = side.DrRepeats(); r < reps; r++) i = Deathrattle(m, side, other, i);
                    if (m.Reborn && side.N < MaxBoard)
                    {
                        // A Reborn copy is a fresh card: printed Attack, 1 Health (engine.py measured this on 314 real events).
                        var rb = Clone(m);
                        rb.Gained = null;
                        rb.Hp = 1;
                        rb.Atk = m.BaseAtk;
                        if (side.UndeadAtk != 0 && rb.Has(Tribe.Undead)) rb.Atk += side.UndeadAtk;
                        rb.Reborn = false;
                        rb.Atk += rb.Sc.DeathA * side.Deaths;
                        side.Insert(i, rb);
                        i++;
                        // A Reborn copy is summoned: "after you summon" watchers see it as well as the Reborn watchers.
                        FireSummon(side, rb, other);
                        if (side.WReborn)
                        {
                            var d = SnapBegin(side);
                            for (var k = 0; k < _snapN[d]; k++)
                            {
                                var w = _snap[d][k];
                                if (w.Sc.OnReborn is { } h && w.Hp > 0 && !ReferenceEquals(w, rb)) h(this, w, rb, side, other);
                            }
                            SnapEnd();
                        }
                    }
                    if (side.WDeath)
                    {
                        // 'After a DIFFERENT friendly minion dies': m is already off the board.
                        var d = SnapBegin(side);
                        for (var k = 0; k < _snapN[d]; k++)
                        {
                            var w = _snap[d][k];
                            if (w.Sc.FDeath is { } h && w.Hp > 0 && !ReferenceEquals(w, m)) h(this, w, m, side, other);
                        }
                        SnapEnd();
                    }
                }
            }
        }
    }

    public void Attack(Minion att, Side aside, Side dside)
    {
        att.Stealth = false;
        for (int swing = 0, swings = att.Wf ? 2 : 1; swing < swings; swing++)
        {
            if (att.Hp <= 0 || dside.N == 0) return;
            var dfn = PickDefender(dside);
            // Rally and friendly-attack watchers resolve INSIDE the attack, before damage.
            var triggered = false;
            if (att.Sc.OnAtk is { } onAtk)
            {
                onAtk(this, att, dfn, aside, dside);
                triggered = true;
            }
            if (aside.WAtk)
            {
                var d = SnapBegin(aside);
                for (var k = 0; k < _snapN[d]; k++)
                {
                    var w = _snap[d][k];
                    if (w.Sc.FAtk is { } h && w.Hp > 0) h(this, w, att, aside, dside);
                }
                SnapEnd();
                triggered = true;
            }
            if (triggered)
            {
                ResolveDeaths(aside, dside);
                if (att.Hp <= 0 || dside.N == 0) return;
                if (dfn.Hp <= 0 || dside.IndexOf(dfn) < 0) continue; // the rally killed the target: this swing fizzles
            }
            var di = dside.IndexOf(dfn);
            int excess;
            if (att.Sc.Cleave)
            {
                excess = Hit(att, dfn);
                if (di > 0) Hit(att, dside.Ms[di - 1]);
                if (di + 1 < dside.N) Hit(att, dside.Ms[di + 1]);
            }
            else
            {
                excess = Hit(att, dfn);
            }
            var ok = att.Sc.Overkill;
            if (ok != 0 && excess > 0 && dfn.Hp <= 0)
            {
                var hasL = di > 0;
                var hasR = di + 1 < dside.N;
                if (hasL || hasR)
                {
                    if (ok == 2)
                    {
                        if (hasL) Damage(dside.Ms[di - 1], excess);
                        if (hasR) Damage(dside.Ms[di + 1], excess);
                    }
                    else if (hasL && hasR) Damage(dside.Ms[di + (Rand(2) == 0 ? -1 : 1)], excess);
                    else Damage(dside.Ms[hasL ? di - 1 : di + 1], excess);
                }
            }
            Hit(dfn, att);
            ResolveDeaths(aside, dside);
        }
    }

    /// <summary>Loads both sides from templates without fighting (used by tests and by <see cref="Fight"/>).</summary>
    public void Setup(Minion[] ta, Minion[] tb)
    {
        _used = 0;
        _depth = 0;
        A.Load(ta, this);
        B.Load(tb, this);
    }

    /// <summary>One full combat. Result +1 A wins / 0 tie / -1 B wins; Damage = tier-sum of the winner's survivors.</summary>
    public FightResult Fight(Minion[] ta, Minion[] tb)
    {
        Setup(ta, tb);
        int turn = A.N != B.N ? (A.N > B.N ? 0 : 1) : Rand(2);
        var first = turn == 0 ? A : B;
        var second = turn == 0 ? B : A;
        for (var pass = 0; pass < 2; pass++)
        {
            var side = pass == 0 ? first : second;
            var other = pass == 0 ? second : first;
            for (var k = 0; k < side.N; k++)
            {
                var m = side.Ms[k];
                if (m.Sc.Soc is { } soc && m.Hp > 0) soc(this, m, side, other);
            }
        }
        ResolveDeaths(first, second);
        int attacks = 0, skips = 0;
        while (A.N > 0 && B.N > 0 && attacks < MaxAttacks)
        {
            var aside = turn == 0 ? A : B;
            var dside = turn == 0 ? B : A;
            var i = aside.NextAttackerIdx();
            if (i < 0)
            {
                if (++skips >= 2) break; // neither side can attack: stalemate
                turn ^= 1;
                continue;
            }
            skips = 0;
            aside.Ptr = i + 1;
            Attack(aside.Ms[i], aside, dside);
            attacks++;
            turn ^= 1;
        }
        if (A.N > 0 && B.N == 0) return new FightResult(1, TierSum(A));
        if (B.N > 0 && A.N == 0) return new FightResult(-1, TierSum(B));
        return new FightResult(0, 0);
    }

    static int TierSum(Side s)
    {
        var t = 0;
        for (var i = 0; i < s.N; i++) t += s.Ms[i].Tier;
        return t;
    }
}
