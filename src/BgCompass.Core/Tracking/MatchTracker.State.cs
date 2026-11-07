using BgCompass.Core.Logs;

namespace BgCompass.Core.Tracking;

public sealed partial class MatchTracker
{
    private sealed record HeroOffer(int Id, string CardId, string Name);

    private sealed class InfoStash(TimeSpan time)
    {
        public readonly TimeSpan Time = time;
        public readonly List<PowerEvent> Events = [];
    }

    private sealed class HeroAttack(bool mine, int depth)
    {
        public readonly bool Mine = mine;
        public readonly int Depth = depth;
        public int? Damage;
    }

    private sealed class CombatCtx(int round, int oppAtStart)
    {
        public readonly int Round = round;
        public readonly int OppAtStart = oppAtStart;
        public CombatSnapshot? Snap;
        public int Attacks;
        public HeroAttack? PendingHeroAttack;
        public HeroAttack? FinalHeroAttack;
    }

    /// <summary>All state of one game. A new CREATE_GAME replaces the whole object.</summary>
    private sealed class Game
    {
        public bool HasStart, Started, Ignored, Ended, AwaitPtlCreate;
        public int SeatTicks;
        public TimeSpan StartTime;
        public int StartDay;
        public int? Build;
        public string? GameType;
        public bool IsDuos;

        public int LocalPid, AiPid, LocalHeroId, NextOpp, Turn, Depth;
        public string? LocalName;
        public bool HeroChosen, ChoiceIsMulligan;

        public readonly Dictionary<int, string> Names = [];
        public readonly Dictionary<int, TrackedEntity> Ents = new(2048);
        public readonly Dictionary<int, TrackedEntity> PlayerEntities = [];
        public TrackedEntity? GameEntity, Pending;
        public readonly List<HeroOffer> Offers = [];
        /// <summary>Cards the game asks the player to pick from right now, other than the hero pick.</summary>
        public readonly List<EntityRef> Choices = [];
        public int? DamageCap;
        public bool DamageCapOn = true;
        public readonly HashSet<string> Confirmed = new(StringComparer.Ordinal);
        public readonly Dictionary<int, LastSeenBoard> LastBoards = [];
        public readonly List<Encounter> Encounters = [];
        public CombatCtx? Combat;

        public bool InCombat => Combat is not null;
    }

    // ---- helpers ----------------------------------------------------------------------------

    private static bool IsPlaceholderHero(string? cardId) =>
        cardId is null || cardId.StartsWith("TB_BaconShopBob", StringComparison.Ordinal)
        || cardId == "TB_BaconShop_HERO_PH";

    private static int AiPid(Game g)
    {
        if (g.AiPid != 0) return g.AiPid;
        foreach (var e in g.Ents.Values)
        {
            if (e.CardId is { } c && c.StartsWith("TB_BaconShopBob", StringComparison.Ordinal) && e.Controller != 0)
                return g.AiPid = e.Controller;
        }
        return 0;
    }

    private static TrackedEntity? PlayerEnt(Game g, int pid)
    {
        if (pid == 0) return null;
        if (g.PlayerEntities.TryGetValue(pid, out var e)) return e;
        // The Player line was not seen (log started mid-stream): keep a stand-in so name-addressed tags still land.
        e = new TrackedEntity(-pid) { IsPlayer = true, CardType = TagValues.TypePlayer, PlayerId = pid };
        g.PlayerEntities[pid] = e;
        return e;
    }

    private TrackedEntity? ResolvePlayerByName(Game g, string? name)
    {
        if (name is null) return null;
        if (g.LocalName is not null && string.Equals(name, g.LocalName, StringComparison.Ordinal))
            return PlayerEnt(g, g.LocalPid);
        if (g.LocalName is null && g.LocalPid == 0) return null;
        return PlayerEnt(g, AiPid(g));
    }

    private void NoteSighting(Game g, TrackedEntity e)
    {
        if (!e.IsMinion || e.Zone != TagValues.ZonePlay || e.Controller == 0) return;
        var r = e.Race;
        if (string.IsNullOrEmpty(r) || r == "ALL" || r == "INVALID") return;
        if (_poolTagSeen && e.IsPool != 1) return;
        g.Confirmed.Add(r);
    }

    private static void SetKeyword(TrackedEntity e, MinionKeywords k, int value) =>
        e.Keywords = value != 0 ? e.Keywords | k : e.Keywords & ~k;

    // ---- tag application --------------------------------------------------------------------

    private void ApplyTag(Game g, TrackedEntity e, int tag, int value, string? text, bool immediate)
    {
        switch (tag)
        {
            case GameTag.Zone:
                e.Zone = value;
                if (value == TagValues.ZonePlay)
                    foreach (var o in g.Offers)
                        if (o.Id == e.Id) g.HeroChosen = true;
                break;
            case GameTag.Controller: e.Controller = value; break;
            case GameTag.ZonePosition: e.ZonePos = value; break;
            case GameTag.CardType: e.CardType = value; break;
            case GameTag.CardRace: e.Race = text is null ? null : TagValues.Race(text); break;
            case GameTag.Atk: e.Atk = value; break;
            case GameTag.Health: e.Health = value; break;
            case GameTag.Damage: e.Damage = value; break;
            case GameTag.Armor: e.Armor = value; break;
            case GameTag.Premium: e.Premium = value; break;
            case GameTag.TechLevel: e.TechLevel = value; break;
            case GameTag.Cost: e.Cost = value; break;
            case GameTag.MaxPlayerTech: e.MaxTech = value; break;
            case GameTag.DamageCap: if (e.IsGame) g.DamageCap = value; break;
            case GameTag.DamageCapEnabled: if (e.IsGame) g.DamageCapOn = value != 0; break;
            case GameTag.Taunt: SetKeyword(e, MinionKeywords.Taunt, value); break;
            case GameTag.DivineShield: SetKeyword(e, MinionKeywords.DivineShield, value); break;
            case GameTag.Poisonous: SetKeyword(e, MinionKeywords.Poisonous, value); break;
            case GameTag.Venomous: SetKeyword(e, MinionKeywords.Venomous, value); break;
            case GameTag.Reborn: SetKeyword(e, MinionKeywords.Reborn, value); break;
            case GameTag.Windfury: SetKeyword(e, MinionKeywords.Windfury, value); break;
            case GameTag.Stealth: SetKeyword(e, MinionKeywords.Stealth, value); break;
            case GameTag.Deathrattle: SetKeyword(e, MinionKeywords.Deathrattle, value); break;
            case GameTag.Resources: e.Resources = value; e.HasResources = true; break;
            case GameTag.ResourcesUsed: e.ResourcesUsed = value; e.HasResources = true; break;
            case GameTag.PlayerId: e.PlayerId = value; break;
            case GameTag.LeaderboardPlace: e.Place = value; e.SeatTick = ++g.SeatTicks; break;
            case GameTag.PlayerTechLevel: e.PlayerTech = value; break;
            case GameTag.PlayerTriples: e.Triples = value; break;
            case GameTag.IsBaconPoolMinion: e.IsPool = value; if (value != 0) _poolTagSeen = true; break;
            case GameTag.Turn:
                if (e.IsGame) OnTurn(g, value);
                break;
            case GameTag.State:
                if (e.IsGame && value == TagValues.StateComplete) EndGame(g);
                break;
            case GameTag.PlayState:
                if (e.IsPlayer && e.PlayerId == g.LocalPid && g.LocalPid != 0 && value is TagValues.PlayWon
                        or TagValues.PlayLost or TagValues.PlayTied or TagValues.PlayConceded or TagValues.PlayDisconnected)
                    EndGame(g);
                break;
            case GameTag.HeroEntity:
                if (e.IsPlayer && e.PlayerId == g.LocalPid && g.LocalPid != 0
                    && g.Ents.TryGetValue(value, out var h) && !IsPlaceholderHero(h.CardId))
                {
                    g.LocalHeroId = value;
                    g.HeroChosen = true;
                }
                break;
            case GameTag.NextOpponentPlayerId:
                if (g.LocalPid != 0 && e.PlayerId == g.LocalPid && (e.IsPlayer || e.IsHero)) g.NextOpp = value;
                break;
        }

        if (immediate && tag is GameTag.Zone or GameTag.CardRace or GameTag.IsBaconPoolMinion or GameTag.Controller)
            NoteSighting(g, e);
    }

    // ---- turns and combat -------------------------------------------------------------------

    private void OnTurn(Game g, int turn)
    {
        g.Turn = turn;
        g.Choices.Clear();
        if (turn >= 1) g.HeroChosen = true;
        if (turn <= 0) return;
        if (turn % 2 == 0)
        {
            if (g.Combat is not null) EndCombat(g);
            g.Combat = new CombatCtx(turn / 2, g.NextOpp);
        }
        else
        {
            EndCombat(g);
        }
    }

    private void OnBlockStart(Game g, PowerEvent ev)
    {
        g.Depth++;
        if (g.Combat is not { } c || ev.Text != "ATTACK") return;
        c.Attacks++;
        if (c.Snap is null) c.Snap = CaptureCombat(g, c);

        var attacker = Resolve(g, ev.Entity, create: false);
        bool hero = attacker is not null
            ? attacker.IsHero || (attacker.CardType == 0 && attacker.CardId?.Contains("HERO", StringComparison.Ordinal) == true)
            : ev.Entity.CardId?.Contains("HERO", StringComparison.Ordinal) == true;
        if (!hero) return;
        int controller = attacker is { Controller: not 0 } ? attacker.Controller : ev.Entity.Player;
        c.PendingHeroAttack = new HeroAttack(controller == g.LocalPid && g.LocalPid != 0, g.Depth);
    }

    private static void OnBlockEnd(Game g)
    {
        if (g.Depth > 0) g.Depth--;
        if (g.Combat is { PendingHeroAttack: { } p } c && g.Depth < p.Depth)
        {
            c.FinalHeroAttack = p;
            c.PendingHeroAttack = null;
        }
    }

    private List<TrackedEntity> MinionsOf(Game g, int controller)
    {
        var list = new List<TrackedEntity>(8);
        if (controller == 0) return list;
        foreach (var e in g.Ents.Values)
            if (e.IsMinion && e.Zone == TagValues.ZonePlay && e.Controller == controller) list.Add(e);
        list.Sort((a, b) => a.ZonePos != b.ZonePos ? a.ZonePos.CompareTo(b.ZonePos) : a.Id.CompareTo(b.Id));
        return list;
    }

    private static MinionView ToView(TrackedEntity e) => new(
        e.Id, e.CardId ?? "", e.Name ?? e.CardId ?? "", e.Atk, Math.Max(0, e.Health - e.Damage), e.ZonePos,
        e.Premium != 0, e.Keywords,
        string.IsNullOrEmpty(e.Race) || e.Race == "INVALID" ? [] : [e.Race], e.TechLevel, e.Cost >= 0 ? e.Cost : 3);

    /// <summary>
    /// Hero entity that represents the seat of <paramref name="pid"/>. The game writes a seat's leaderboard place, tier, armor
    /// and damage to one copy of the hero and resets them on the copies it creates for combat, so the copy that last got a
    /// place wins. Before any place: the local hero for the local seat, else the newest hero carrying that PLAYER_ID.
    /// </summary>
    private static TrackedEntity? SeatHero(Game g, int pid)
    {
        TrackedEntity? best = null, local = null;
        foreach (var e in g.Ents.Values)
        {
            if (!e.IsHero || e.PlayerId != pid || IsPlaceholderHero(e.CardId)) continue;
            if (pid == g.LocalPid && e.Id == g.LocalHeroId) local = e;
            if (best is null || (e.SeatTick, e.Id).CompareTo((best.SeatTick, best.Id)) > 0) best = e;
        }
        return best is { SeatTick: 0 } ? local ?? best : best;
    }

    /// <summary>The seat's tavern tier: combat copies carry 0, which is never a real tier.</summary>
    private static int? SeatTier(Game g, int pid)
    {
        if (SeatHero(g, pid) is { PlayerTech: > 0 } seat) return seat.PlayerTech;
        TrackedEntity? best = null;
        foreach (var e in g.Ents.Values)
            if (e.IsHero && e.PlayerId == pid && e.PlayerTech > 0 && (best is null || e.Id > best.Id)) best = e;
        return best?.PlayerTech;
    }

    /// <summary>Damage the hero can still take: armor counts, and may be all that keeps it above zero.</summary>
    private static int? HeroHealth(TrackedEntity? e) => e is { Health: > 0 } ? e.Health - e.Damage + e.Armor : null;

    private const string ControllersUnknown = "controllers unknown";
    private const string EnemyUnreadable = "enemy side unreadable";
    private const string MissingCardId = "minion without card id";

    private CombatSnapshot CaptureCombat(Game g, CombatCtx c)
    {
        string? reason = null;
        int ai = AiPid(g);
        if (g.LocalPid == 0 || ai == 0) reason = ControllersUnknown;

        var mine = MinionsOf(g, g.LocalPid);
        var theirs = MinionsOf(g, ai);

        TrackedEntity? aiHero = null;
        foreach (var e in g.Ents.Values)
            if (e.IsHero && e.Zone == TagValues.ZonePlay && e.Controller == ai && ai != 0 && !IsPlaceholderHero(e.CardId)
                && (aiHero is null || e.Id > aiHero.Id))
                aiHero = e;

        int opp = c.OppAtStart;
        if (aiHero?.CardId is { } hc)
        {
            foreach (var pid in SeatPids(g))
                if (pid != g.LocalPid && SeatHero(g, pid)?.CardId == hc) { opp = pid; break; }
        }
        var seat = opp != 0 ? SeatHero(g, opp) : null;
        var localSeat = SeatHero(g, g.LocalPid) ?? LocalHero(g);

        if (reason is null && theirs.Count == 0 && aiHero is null) reason = EnemyUnreadable;
        if (reason is null && (mine.Any(m => string.IsNullOrEmpty(m.CardId)) || theirs.Any(m => string.IsNullOrEmpty(m.CardId))))
            reason = MissingCardId;

        return new CombatSnapshot(
            c.Round, opp, seat?.CardId ?? aiHero?.CardId ?? "",
            mine.ConvertAll(ToView), theirs.ConvertAll(ToView),
            SeatTier(g, g.LocalPid) ?? PlayerEnt(g, g.LocalPid)?.PlayerTech,
            opp != 0 ? SeatTier(g, opp) : null,
            HeroHealth(localSeat),
            HeroHealth(seat),
            reason is null, reason, g.DamageCapOn ? g.DamageCap : null);
    }

    private void EndCombat(Game g)
    {
        if (g.Combat is not { } c) return;
        g.Combat = null;
        c.Snap ??= CaptureCombat(g, c);
        var snap = c.Snap;

        var ha = c.FinalHeroAttack ?? c.PendingHeroAttack;
        EncounterResult result;
        int? toMe = null, toEnemy = null;
        if (ha is not null)
        {
            result = ha.Mine ? EncounterResult.Win : EncounterResult.Loss;
            if (ha.Mine) { toEnemy = ha.Damage ?? 0; toMe = 0; }
            else { toMe = ha.Damage ?? 0; toEnemy = 0; }
        }
        else if (c.Attacks > 0)
        {
            result = EncounterResult.Tie;
            toMe = toEnemy = 0;
        }
        else
        {
            result = EncounterResult.Unknown;
        }

        var seat = snap.OpponentPlayerId != 0 ? SeatHero(g, snap.OpponentPlayerId) : null;
        g.Encounters.Add(new Encounter(
            c.Round, snap.OpponentPlayerId, snap.OpponentHeroCardId, seat?.Name ?? seat?.CardId ?? snap.OpponentHeroCardId,
            snap.Enemy, snap.Friendly, result, toMe, toEnemy));
        // Remember the board unless it could not be read; minions without a card id are left out of it.
        if (snap.OpponentPlayerId != 0 && snap.IncompleteReason is null or MissingCardId)
            g.LastBoards[snap.OpponentPlayerId] = new LastSeenBoard(c.Round, snap.Enemy.Where(m => m.CardId.Length > 0).ToArray());
        Touch(_burstTime);
    }

    // ---- game end ---------------------------------------------------------------------------

    private void EndGame(Game g)
    {
        if (g.Ended) return;
        var hero = LocalHero(g);
        var finalBoard = g.Combat?.Snap is { } cs ? cs.Friendly : MinionsOf(g, g.LocalPid).ConvertAll(ToView);
        if (g.Combat is not null) EndCombat(g);
        g.Ended = true;
        Touch(_burstTime);
        if (hero?.CardId is null) return;

        var start = _baseDate.AddDays(g.StartDay).ToDateTime(TimeOnly.MinValue) + g.StartTime;
        var end = _baseDate.AddDays(_eventDay).ToDateTime(TimeOnly.MinValue) + _burstTime;
        var tribes = g.Confirmed.Order(StringComparer.Ordinal).ToArray();
        _endedOut.Add(new MatchRecord(
            $"{_sessionId}_{start:yyyyMMdd}T{start:HHmmss.fffffff}",
            new DateTimeOffset(end, TimeZoneInfo.Local.GetUtcOffset(end)),
            g.Build, hero.CardId, hero.Name ?? hero.CardId, hero.Place,
            (g.Turn + 1) / 2, finalBoard.Select(m => m.CardId).ToArray(), tribes, null));
        _hasWork = true;
    }
}
