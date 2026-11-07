#:project ../src/BgCompass.Core/BgCompass.Core.csproj
#:property PublishAot=false
#:property JsonSerializerIsReflectionEnabledByDefault=true
// Rebuilds the embedded offline card snapshot.
// Usage: dotnet run tools/make-card-snapshot.cs <enUS cards.json> <ruRU cards.json> <build> <output .json.gz>
using BgCompass.Core.Cards;

using var en = File.OpenRead(args[0]);
using var ru = File.OpenRead(args[1]);
var catalog = CardCatalog.FromHearthstoneJson(en, ru,
    new CardDataVersion(int.Parse(args[2]), DateTimeOffset.UtcNow, "embedded snapshot"));
using var output = File.Create(args[3]);
catalog.Save(output);
Console.WriteLine($"{catalog.All.Count} cards, {catalog.PoolMinions.Count} pool minions");
