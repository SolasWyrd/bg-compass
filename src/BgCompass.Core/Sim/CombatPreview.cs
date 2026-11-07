namespace BgCompass.Core.Sim;

/// <summary>
/// "If the fight were now": the player's current board against the next opponent's last seen one. That board is old by
/// definition, so the result is an estimate for planning and is never shown as the coming fight's odds.
/// </summary>
public static class CombatPreview
{
    /// <summary>Null outside the recruit phase, or while either board is unknown.</summary>
    public static CombatSnapshot? For(MatchSnapshot m)
    {
        if (m.Phase != MatchPhase.Recruit || m.Board.Count == 0) return null;
        var enemy = m.Players.FirstOrDefault(p => p.IsNextOpponent && !p.IsDead);
        if (enemy?.LastBoard is not { Minions.Count: > 0 } seen) return null;
        var me = m.Players.FirstOrDefault(p => p.IsLocal);
        return new CombatSnapshot(m.Round, enemy.PlayerId, enemy.HeroCardId, m.Board, seen.Minions,
            m.TavernTier, enemy.TavernTier, me?.Health, enemy.Health, true, null, m.DamageCap);
    }

    /// <summary>How many rounds old the opponent's board is; 0 when it was seen this round.</summary>
    public static int BoardAge(MatchSnapshot m, CombatSnapshot preview)
    {
        var board = m.Players.FirstOrDefault(p => p.PlayerId == preview.OpponentPlayerId)?.LastBoard;
        return board is null ? 0 : Math.Max(0, m.Round - board.Round);
    }
}
