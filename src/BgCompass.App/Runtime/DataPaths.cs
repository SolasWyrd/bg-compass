namespace BgCompass.App.Runtime;

/// <summary>
/// Where the app keeps its files: %LOCALAPPDATA%\BgCompass on Windows, the platform equivalent elsewhere.
/// The BGC_DATA_DIR environment variable overrides it (tests, automated runs).
/// </summary>
public sealed class DataPaths
{
    public DataPaths(string root)
    {
        Root = root;
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(CardCache);
        Directory.CreateDirectory(ArtCache);
    }

    public string Root { get; }
    public string Settings => Path.Combine(Root, "settings.json");
    public string AppLog => Path.Combine(Root, "app.log");
    public string CardCache => Path.Combine(Root, "cards");
    public string ArtCache => Path.Combine(Root, "art");

    /// <param name="replay">A log replay keeps its history apart from real games unless BGC_DATA_DIR is set.</param>
    public static DataPaths Resolve(bool replay = false)
    {
        var env = Environment.GetEnvironmentVariable("BGC_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(env)) return new DataPaths(Path.GetFullPath(env));
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BgCompass");
        return new DataPaths(replay ? Path.Combine(root, "replay") : root);
    }
}
