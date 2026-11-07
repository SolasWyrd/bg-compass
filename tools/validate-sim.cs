#:project ../src/BgCompass.Core/BgCompass.Core.csproj
#:property PublishAot=false
#:property JsonSerializerIsReflectionEnabledByDefault=true
// Scores the simulator against fights recorded in real Power.log files.
// Usage: dotnet run tools/validate-sim.cs [--gamestate] [--cards <enUS cards.json of that build>] <Power.log>...
// --gamestate: for old logs that hold only the GameState stream.
using BgCompass.Core;
using BgCompass.Core.Cards;
using BgCompass.Core.Logs;
using BgCompass.Core.Sim;
using BgCompass.Core.Tracking;

var useGameState = args.Contains("--gamestate");
var cardsArg = Array.FindIndex(args, a => a == "--cards");
CardCatalog cards;
if (cardsArg >= 0)
{
    using var en = File.OpenRead(args[cardsArg + 1]);
    using var ru = File.OpenRead(args[cardsArg + 1]);
    cards = CardCatalog.FromHearthstoneJson(en, ru, new CardDataVersion(null, DateTimeOffset.UtcNow, "arg"));
}
else cards = CardCatalog.LoadEmbedded();
int fights = 0, scored = 0, correct = 0, unavailable = 0;
double brier = 0;

foreach (var path in args.Where((a, i) => !a.StartsWith("--") && i != cardsArg + 1 || (cardsArg < 0 && !a.StartsWith("--"))))
{
    var tracker = new MatchTracker(new MatchTrackerOptions
    {
        Stream = useGameState ? PowerStream.GameState : PowerStream.PowerTaskList,
    });
    var bySnapshot = new Dictionary<int, SimResult>();
    var game = 0;
    foreach (var line in File.ReadLines(path))
    {
        tracker.Feed(line);
        var snap = tracker.Snapshot;
        if (snap.Phase == MatchPhase.Idle && snap.Round == 0 && bySnapshot.Count > 0 && snap.Encounters.Count == 0) { bySnapshot.Clear(); game++; }
        if (snap.Combat is { } c && !bySnapshot.ContainsKey(c.Round))
            bySnapshot[c.Round] = CombatSimulator.Run(c, cards, 3000, seed: 1);
        foreach (var e in snap.Encounters)
        {
            if (!bySnapshot.TryGetValue(e.Round, out var sim) || sim is null) continue;
            bySnapshot[e.Round] = null!;
            fights++;
            if (!sim.Available) { unavailable++; Console.WriteLine($"round {e.Round,2}: unavailable: {sim.UnavailableReason}"); continue; }
            if (e.Result == EncounterResult.Unknown) continue;
            scored++;
            var actualWin = e.Result == EncounterResult.Win ? 1.0 : 0.0;
            var actualTie = e.Result == EncounterResult.Tie ? 1.0 : 0.0;
            var actualLoss = e.Result == EncounterResult.Loss ? 1.0 : 0.0;
            brier += (Math.Pow(sim.Win - actualWin, 2) + Math.Pow(sim.Tie - actualTie, 2) + Math.Pow(sim.Loss - actualLoss, 2)) / 2;
            var predicted = sim.Win >= sim.Loss && sim.Win >= sim.Tie ? EncounterResult.Win : sim.Loss >= sim.Tie ? EncounterResult.Loss : EncounterResult.Tie;
            if (predicted == e.Result) correct++;
            Console.WriteLine($"round {e.Round,2}: win {sim.Win:P0} tie {sim.Tie:P0} loss {sim.Loss:P0} | actual {e.Result} | unmodelled: {string.Join(", ", sim.Unmodelled)}");
        }
    }
}
Console.WriteLine($"fights {fights}, scored {scored}, unavailable {unavailable}, called correctly {(scored > 0 ? 100.0 * correct / scored : 0):F0}%, Brier {(scored > 0 ? brier / scored : 0):F3}");
