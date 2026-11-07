namespace BgCompass.Core.Logs;

/// <summary>
/// Parses Power.log lines (<c>D hh:mm:ss.fffffff Source.Method() - body</c>) without regular expressions.
/// Lines the tracker does not use return <see cref="PowerEvent.None"/> after a few cheap checks. Never throws.
/// </summary>
public static class PowerParser
{
    private const string MethodEnd = "() - ";

    public static PowerEvent Parse(string? line)
    {
        if (string.IsNullOrEmpty(line)) return PowerEvent.None;
        try { return ParseCore(line); }
        catch (Exception) { return PowerEvent.None; }
    }

    /// <summary>Reads only the time of day, for callers that need a line's clock without a full parse.</summary>
    public static bool TryParseTime(string line, out TimeSpan time)
    {
        time = default;
        if (line.Length < 19 || line[4] != ':' || line[7] != ':' || line[10] != '.') return false;
        if (!Two(line, 2, out var h) || !Two(line, 5, out var m) || !Two(line, 8, out var s)) return false;
        int i = 11;
        long frac = 0;
        int digits = 0;
        while (i < line.Length && line[i] is >= '0' and <= '9' && digits < 7) { frac = frac * 10 + (line[i] - '0'); i++; digits++; }
        if (digits == 0) return false;
        for (; digits < 7; digits++) frac *= 10;
        time = new TimeSpan(h * TimeSpan.TicksPerHour + m * TimeSpan.TicksPerMinute + s * TimeSpan.TicksPerSecond + frac);
        return true;
    }

    private static bool Two(string s, int i, out int v)
    {
        v = 0;
        char a = s[i], b = s[i + 1];
        if (a is < '0' or > '9' || b is < '0' or > '9') return false;
        v = (a - '0') * 10 + (b - '0');
        return true;
    }

    private static PowerEvent ParseCore(string line)
    {
        if (line.Length < 30 || line[1] != ' ' || !TryParseTime(line, out var time)) return PowerEvent.None;
        int sp = line.IndexOf(' ', 11);
        if (sp < 0) return PowerEvent.None;
        int methodEnd = line.IndexOf(MethodEnd, sp + 1, StringComparison.Ordinal);
        if (methodEnd < 0) return PowerEvent.None;
        var source = line.AsSpan(sp + 1, methodEnd - sp - 1);
        var body = line.AsSpan(methodEnd + MethodEnd.Length);

        int indent = 0;
        while (indent < body.Length && body[indent] == ' ') indent++;
        var text = body[indent..].TrimEnd();
        if (text.IsEmpty) return PowerEvent.None;

        return source switch
        {
            "GameState.DebugPrintPower" => ParsePower(text, PowerSource.GameState, time, indent),
            "PowerTaskList.DebugPrintPower" => ParsePower(text, PowerSource.PowerTaskList, time, indent),
            "GameState.DebugPrintGame" => ParseGameInfo(text, time),
            "GameState.DebugPrintEntityChoices" => ParseChoices(text, time, chosen: false),
            "GameState.DebugPrintEntitiesChosen" => ParseChoices(text, time, chosen: true),
            _ => PowerEvent.None,
        };
    }

    private static PowerEvent ParsePower(ReadOnlySpan<char> t, PowerSource src, TimeSpan time, int indent)
    {
        switch (t[0])
        {
            case 't' when t.StartsWith("tag="):
            {
                if (!SplitTagValue(t[4..], out var name, out var value)) return PowerEvent.None;
                int tag = GameTag.Resolve(name);
                if (tag == 0) return PowerEvent.None;
                return new PowerEvent
                {
                    Kind = PowerKind.TagValue, Source = src, Time = time, Indent = indent, Tag = tag,
                    Value = TagValues.Convert(tag, value), ValueText = tag == GameTag.CardRace ? value.ToString() : null,
                };
            }
            case 'T' when t.StartsWith("TAG_CHANGE Entity="):
                return ParseTagChange(t["TAG_CHANGE Entity=".Length..], PowerKind.TagChange, src, time, indent);
            case 'H' when t.StartsWith("HIDE_ENTITY - Entity="):
                return ParseTagChange(t["HIDE_ENTITY - Entity=".Length..], PowerKind.HideEntity, src, time, indent);
            case 'B' when t.StartsWith("BLOCK_START"):
                return ParseBlockStart(t, src, time, indent);
            case 'B' when t.StartsWith("BLOCK_END"):
                return new PowerEvent { Kind = PowerKind.BlockEnd, Source = src, Time = time, Indent = indent };
            case 'F' when t.StartsWith("FULL_ENTITY - Creating"):
            {
                var rest = t["FULL_ENTITY - Creating".Length..];
                int idAt = rest.IndexOf("ID=", StringComparison.Ordinal);
                int cardAt = rest.IndexOf("CardID=", StringComparison.Ordinal);
                if (idAt < 0 || cardAt < 0) return PowerEvent.None;
                return new PowerEvent
                {
                    Kind = PowerKind.FullEntityCreate, Source = src, Time = time, Indent = indent,
                    Id = LeadingInt(rest[(idAt + 3)..]), CardId = rest[(cardAt + 7)..].Trim().ToString(),
                };
            }
            case 'F' when t.StartsWith("FULL_ENTITY - Updating "):
                return ParseEntityCard(t["FULL_ENTITY - Updating ".Length..], PowerKind.FullEntityUpdate, src, time, indent);
            case 'S' when t.StartsWith("SHOW_ENTITY - Updating Entity="):
                return ParseEntityCard(t["SHOW_ENTITY - Updating Entity=".Length..], PowerKind.ShowEntity, src, time, indent);
            case 'C' when t.StartsWith("CHANGE_ENTITY - Updating Entity="):
                return ParseEntityCard(t["CHANGE_ENTITY - Updating Entity=".Length..], PowerKind.ChangeEntity, src, time, indent);
            case 'C' when t.SequenceEqual("CREATE_GAME"):
                return new PowerEvent { Kind = PowerKind.CreateGame, Source = src, Time = time, Indent = indent };
            case 'G' when t.StartsWith("GameEntity EntityID="):
                return new PowerEvent
                {
                    Kind = PowerKind.GameEntity, Source = src, Time = time, Indent = indent,
                    Id = LeadingInt(t["GameEntity EntityID=".Length..]),
                };
            case 'P' when t.StartsWith("Player EntityID="):
                return ParsePlayer(t, src, time, indent);
            case 'M' when t.StartsWith("META_DATA - Meta="):
            {
                var rest = t["META_DATA - Meta=".Length..];
                int sp = rest.IndexOf(' ');
                if (sp < 0) return PowerEvent.None;
                int dataAt = rest.IndexOf("Data=", StringComparison.Ordinal);
                return new PowerEvent
                {
                    Kind = PowerKind.MetaData, Source = src, Time = time, Indent = indent,
                    Text = rest[..sp].ToString(), Value = dataAt < 0 ? 0 : LeadingInt(rest[(dataAt + 5)..]),
                };
            }
            default:
                return PowerEvent.None;
        }
    }

    private static PowerEvent ParseTagChange(ReadOnlySpan<char> rest, PowerKind kind, PowerSource src, TimeSpan time, int indent)
    {
        if (!SplitRef(rest, "] tag=", " tag=", out var entity, out var after)) return PowerEvent.None;
        if (!SplitTagValue(after, out var name, out var value)) return PowerEvent.None;
        int tag = GameTag.Resolve(name);
        if (tag == 0) return PowerEvent.None;
        return new PowerEvent
        {
            Kind = kind, Source = src, Time = time, Indent = indent, Entity = entity, Tag = tag,
            Value = TagValues.Convert(tag, value), ValueText = tag == GameTag.CardRace ? value.ToString() : null,
        };
    }

    private static PowerEvent ParseEntityCard(ReadOnlySpan<char> rest, PowerKind kind, PowerSource src, TimeSpan time, int indent)
    {
        if (!SplitRef(rest, "] CardID=", " CardID=", out var entity, out var after)) return PowerEvent.None;
        return new PowerEvent
        {
            Kind = kind, Source = src, Time = time, Indent = indent, Entity = entity, CardId = after.Trim().ToString(),
        };
    }

    private static PowerEvent ParseBlockStart(ReadOnlySpan<char> t, PowerSource src, TimeSpan time, int indent)
    {
        const string typeKey = "BLOCK_START BlockType=";
        if (!t.StartsWith(typeKey)) return PowerEvent.None;
        var rest = t[typeKey.Length..];
        int sp = rest.IndexOf(' ');
        string blockType = (sp < 0 ? rest : rest[..sp]).ToString();
        var entity = EntityRef.None;
        int entAt = rest.IndexOf("Entity=", StringComparison.Ordinal);
        if (entAt >= 0)
            SplitRef(rest[(entAt + 7)..], "] EffectCardId=", " EffectCardId=", out entity, out _);
        return new PowerEvent
        {
            Kind = PowerKind.BlockStart, Source = src, Time = time, Indent = indent, Text = blockType, Entity = entity,
        };
    }

    private static PowerEvent ParsePlayer(ReadOnlySpan<char> t, PowerSource src, TimeSpan time, int indent)
    {
        // Player EntityID=14 PlayerID=5 GameAccountId=[hi=144115198130930503 lo=38593682]
        int pidAt = t.IndexOf("PlayerID=", StringComparison.Ordinal);
        int hiAt = t.IndexOf("hi=", StringComparison.Ordinal);
        int loAt = t.IndexOf("lo=", StringComparison.Ordinal);
        return new PowerEvent
        {
            Kind = PowerKind.Player, Source = src, Time = time, Indent = indent,
            Id = LeadingInt(t["Player EntityID=".Length..]),
            PlayerId = pidAt < 0 ? 0 : LeadingInt(t[(pidAt + 9)..]),
            AccountHi = hiAt < 0 ? 0 : LeadingLong(t[(hiAt + 3)..]),
            AccountLo = loAt < 0 ? 0 : LeadingLong(t[(loAt + 3)..]),
        };
    }

    private static PowerEvent ParseGameInfo(ReadOnlySpan<char> t, TimeSpan time)
    {
        PowerEvent Make(string key, int id = 0, int playerId = 0, string? text = null) => new()
        {
            Kind = PowerKind.GameInfo, Source = PowerSource.GameState, Time = time, Text = key, Id = id,
            PlayerId = playerId, Text2 = text,
        };

        if (t.StartsWith("BuildNumber=")) return Make("BuildNumber", LeadingInt(t[12..]));
        if (t.StartsWith("GameType=")) return Make("GameType", text: t[9..].Trim().ToString());
        if (t.StartsWith("PlayerID="))
        {
            int nameAt = t.IndexOf("PlayerName=", StringComparison.Ordinal);
            if (nameAt < 0) return PowerEvent.None;
            return Make("Player", playerId: LeadingInt(t[9..]), text: t[(nameAt + 11)..].Trim().ToString());
        }
        return PowerEvent.None;
    }

    private static PowerEvent ParseChoices(ReadOnlySpan<char> t, TimeSpan time, bool chosen)
    {
        if (t.StartsWith("id="))
        {
            // id=1 Player=Player#12345 TaskList=7 ChoiceType=MULLIGAN CountMin=1 CountMax=1
            int playerAt = t.IndexOf(" Player=", StringComparison.Ordinal);
            if (playerAt < 0) return PowerEvent.None;
            var afterPlayer = t[(playerAt + 8)..];
            int end = afterPlayer.IndexOf(" TaskList=", StringComparison.Ordinal);
            if (end < 0) end = afterPlayer.IndexOf(" ChoiceType=", StringComparison.Ordinal);
            if (end < 0) end = afterPlayer.IndexOf(" EntitiesCount=", StringComparison.Ordinal);
            string player = (end < 0 ? afterPlayer : afterPlayer[..end]).Trim().ToString();
            string? choiceType = null;
            int typeAt = t.IndexOf("ChoiceType=", StringComparison.Ordinal);
            if (typeAt >= 0)
            {
                var r = t[(typeAt + 11)..];
                int sp = r.IndexOf(' ');
                choiceType = (sp < 0 ? r : r[..sp]).ToString();
            }
            return new PowerEvent
            {
                Kind = chosen ? PowerKind.ChosenHeader : PowerKind.ChoicesHeader, Source = PowerSource.GameState,
                Time = time, Id = LeadingInt(t[3..]), Text = choiceType, Text2 = player,
            };
        }
        if (t.StartsWith("Entities["))
        {
            int close = t.IndexOf(']');
            int eq = t.IndexOf("]=", StringComparison.Ordinal);
            if (close < 0 || eq < 0) return PowerEvent.None;
            var r = t[(eq + 2)..];
            var entity = r.Length > 0 && r[0] == '[' ? ParseBracket(r) : ParseLoose(r);
            return new PowerEvent
            {
                Kind = chosen ? PowerKind.ChosenEntity : PowerKind.ChoicesEntity, Source = PowerSource.GameState,
                Time = time, Id = LeadingInt(t["Entities[".Length..]), Entity = entity,
            };
        }
        return PowerEvent.None;
    }

    // ---- entity references --------------------------------------------------------------

    /// <summary>
    /// Splits "&lt;ref&gt; MARKER rest". A bracket ref ends at <paramref name="bracketMarker"/> (so names holding
    /// brackets or spaces survive); anything else ends at <paramref name="plainMarker"/>.
    /// </summary>
    private static bool SplitRef(ReadOnlySpan<char> s, string bracketMarker, string plainMarker,
        out EntityRef entity, out ReadOnlySpan<char> after)
    {
        entity = EntityRef.None;
        after = default;
        s = s.TrimStart();
        if (s.IsEmpty) return false;
        if (s[0] == '[')
        {
            int end = s.IndexOf(bracketMarker, StringComparison.Ordinal);
            if (end < 0) return false;
            entity = ParseBracket(s[..(end + 1)]);
            after = s[(end + bracketMarker.Length)..];
            return true;
        }
        int at = s.IndexOf(plainMarker, StringComparison.Ordinal);
        if (at < 0) return false;
        entity = ParseLoose(s[..at]);
        after = s[(at + plainMarker.Length)..];
        return true;
    }

    private static EntityRef ParseLoose(ReadOnlySpan<char> s)
    {
        s = s.Trim();
        if (s.IsEmpty) return EntityRef.None;
        if (s.SequenceEqual("GameEntity")) return new EntityRef(EntityRefKind.Game, 0, null, null, 0, 0, 0);
        if (int.TryParse(s, out var id)) return new EntityRef(EntityRefKind.Id, id, null, null, 0, 0, 0);
        return new EntityRef(EntityRefKind.Name, 0, s.ToString(), null, 0, 0, 0);
    }

    /// <summary>[entityName=NAME id=N zone=Z zonePos=P cardId=C player=Q]</summary>
    private static EntityRef ParseBracket(ReadOnlySpan<char> s)
    {
        if (s.Length < 2 || s[0] != '[') return ParseLoose(s);
        s = s[1..];
        if (s.EndsWith("]")) s = s[..^1];
        const string nameKey = "entityName=";
        int idAt = s.IndexOf(" id=", StringComparison.Ordinal);
        if (!s.StartsWith(nameKey) || idAt < 0) return EntityRef.None;
        string name = s[nameKey.Length..idAt].ToString();
        var rest = s[(idAt + 4)..];
        int id = LeadingInt(rest);
        int zone = 0, zonePos = 0, player = 0;
        string? cardId = null;
        int zAt = rest.IndexOf(" zone=", StringComparison.Ordinal);
        if (zAt >= 0)
        {
            var z = rest[(zAt + 6)..];
            int sp = z.IndexOf(' ');
            zone = TagValues.Zone(sp < 0 ? z : z[..sp]);
        }
        int zpAt = rest.IndexOf(" zonePos=", StringComparison.Ordinal);
        if (zpAt >= 0) zonePos = LeadingInt(rest[(zpAt + 9)..]);
        int cAt = rest.IndexOf(" cardId=", StringComparison.Ordinal);
        if (cAt >= 0)
        {
            var c = rest[(cAt + 8)..];
            int sp = c.IndexOf(' ');
            cardId = (sp < 0 ? c : c[..sp]).ToString();
        }
        int pAt = rest.LastIndexOf(" player=", StringComparison.Ordinal);
        if (pAt >= 0) player = LeadingInt(rest[(pAt + 8)..]);
        return new EntityRef(EntityRefKind.Id, id, name, string.IsNullOrEmpty(cardId) ? null : cardId, zone, zonePos, player);
    }

    private static bool SplitTagValue(ReadOnlySpan<char> s, out ReadOnlySpan<char> name, out ReadOnlySpan<char> value)
    {
        name = value = default;
        int at = s.IndexOf(" value=", StringComparison.Ordinal);
        if (at < 0) return false;
        name = s[..at].Trim();
        value = s[(at + 7)..].Trim();
        return !name.IsEmpty;
    }

    private static int LeadingInt(ReadOnlySpan<char> s)
    {
        s = s.TrimStart();
        int i = 0;
        if (i < s.Length && s[i] == '-') i++;
        while (i < s.Length && s[i] is >= '0' and <= '9') i++;
        return int.TryParse(s[..i], out var v) ? v : 0;
    }

    private static long LeadingLong(ReadOnlySpan<char> s)
    {
        s = s.TrimStart();
        int i = 0;
        while (i < s.Length && s[i] is >= '0' and <= '9') i++;
        return long.TryParse(s[..i], out var v) ? v : 0;
    }
}
