namespace BgCompass.App.Runtime;

/// <summary>Tribes the player marked as absent from this lobby. The log can only prove presence, so this is the player's own note; it resets per game.</summary>
public sealed class TribeMarks
{
    readonly object _gate = new();
    HashSet<string> _marked = [];

    public IReadOnlySet<string> Current { get { lock (_gate) return new HashSet<string>(_marked); } }

    /// <summary>Toggles the mark. A tribe the log has confirmed cannot be marked absent. Returns whether anything changed.</summary>
    public bool Toggle(string tribe, IReadOnlySet<string> confirmed)
    {
        if (confirmed.Contains(tribe)) return false;
        lock (_gate) return _marked.Add(tribe) || _marked.Remove(tribe);
    }

    public void Clear()
    {
        lock (_gate) _marked = [];
    }
}
