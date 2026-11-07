using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using BgCompass.App.Runtime;

namespace BgCompass.App.Views;

/// <summary>Card art tile loaded through <see cref="ArtCache"/>. No picture is shown when the download fails.</summary>
public sealed class ArtImage : Image
{
    public static readonly StyledProperty<string?> CardIdProperty = AvaloniaProperty.Register<ArtImage, string?>(nameof(CardId));
    int _version;

    static ArtImage() => CardIdProperty.Changed.AddClassHandler<ArtImage>((a, _) => a.Load());

    public string? CardId { get => GetValue(CardIdProperty); set => SetValue(CardIdProperty, value); }

    async void Load()
    {
        var v = ++_version;
        Source = null;
        var id = CardId;
        var cache = ArtCache.Instance;
        if (string.IsNullOrEmpty(id) || cache is null) return;
        try
        {
            var bmp = await cache.GetAsync(id);
            if (v == _version && bmp is not null) Dispatcher.UIThread.Post(() => { if (v == _version) Source = bmp; });
        }
        catch { /* no picture */ }
    }
}
