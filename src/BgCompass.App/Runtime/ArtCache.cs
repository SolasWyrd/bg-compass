using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Avalonia.Media.Imaging;

namespace BgCompass.App.Runtime;

/// <summary>
/// Card art tiles from art.hearthstonejson.com, kept on disk. A failed download simply means no picture; it is retried
/// after ten minutes. Nothing about the user is sent besides the card id in the URL.
/// </summary>
public sealed partial class ArtCache(string dir, HttpClient? http)
{
    const int MemoryLimit = 160;
    readonly SemaphoreSlim _slots = new(4);
    readonly ConcurrentDictionary<string, DateTime> _failed = new();
    readonly object _lru = new();
    readonly LinkedList<(string Id, Bitmap Bmp)> _order = new();
    readonly Dictionary<string, LinkedListNode<(string Id, Bitmap Bmp)>> _map = new();

    public static ArtCache? Instance { get; set; }

    [GeneratedRegex("^[A-Za-z0-9_\\-]{1,80}$")] private static partial Regex SafeId();

    public async Task<Bitmap?> GetAsync(string cardId)
    {
        if (!SafeId().IsMatch(cardId)) return null;
        lock (_lru)
            if (_map.TryGetValue(cardId, out var node)) { _order.Remove(node); _order.AddFirst(node); return node.Value.Bmp; }
        if (_failed.TryGetValue(cardId, out var when) && DateTime.UtcNow - when < TimeSpan.FromMinutes(10)) return null;

        await _slots.WaitAsync();
        try
        {
            var path = Path.Combine(dir, cardId + ".png");
            if (!File.Exists(path))
            {
                if (http is null) return null;
                using var resp = await http.GetAsync($"https://art.hearthstonejson.com/v1/tiles/{cardId}.png");
                if (!resp.IsSuccessStatusCode) { _failed[cardId] = DateTime.UtcNow; return null; }
                var bytes = await resp.Content.ReadAsByteArrayAsync();
                Directory.CreateDirectory(dir);
                var tmp = path + ".tmp";
                await File.WriteAllBytesAsync(tmp, bytes);
                File.Move(tmp, path, true);
            }
            using var fs = File.OpenRead(path);
            var bmp = new Bitmap(fs);
            lock (_lru)
            {
                if (_map.TryGetValue(cardId, out var existing)) return existing.Value.Bmp;
                _map[cardId] = _order.AddFirst((cardId, bmp));
                while (_order.Count > MemoryLimit)
                {
                    var last = _order.Last!;
                    _map.Remove(last.Value.Id);
                    _order.RemoveLast();
                }
            }
            return bmp;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            _failed[cardId] = DateTime.UtcNow;
            return null;
        }
        finally { _slots.Release(); }
    }
}
