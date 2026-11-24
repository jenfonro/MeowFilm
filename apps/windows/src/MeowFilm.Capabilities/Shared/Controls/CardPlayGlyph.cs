using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MeowFilm.Capabilities;

public sealed class CardPlayGlyph : Grid
{
    public static readonly DependencyProperty IsBusyProperty = DependencyProperty.Register(nameof(IsBusy), typeof(bool), typeof(CardPlayGlyph),
        new PropertyMetadata(false, (owner, _) => ((CardPlayGlyph)owner).Refresh()));
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(nameof(Foreground), typeof(Brush), typeof(CardPlayGlyph),
        new PropertyMetadata(null, (owner, args) => ((CardPlayGlyph)owner).SetForeground((Brush?)args.NewValue)));

    public bool IsBusy { get => (bool)GetValue(IsBusyProperty); set => SetValue(IsBusyProperty, value); }
    public Brush? Foreground { get => (Brush?)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    private readonly FontIcon playIcon = new() { Glyph = "\uE768", FontSize = 24 };
    private ProgressRing? busyRing;

    public CardPlayGlyph()
    {
        Width = 24; Height = 28; Children.Add(playIcon);
        Loaded += (_, _) => Refresh();
        Unloaded += (_, _) => { if (!IsLoaded && busyRing is not null) busyRing.IsActive = false; };
    }

    private void SetForeground(Brush? brush) { playIcon.Foreground = brush; if (busyRing is not null) busyRing.Foreground = brush; }

    private void Refresh()
    {
        if (IsBusy && busyRing is null) Children.Add(busyRing = new ProgressRing { Width = 24, Height = 24, Foreground = Foreground });
        if (busyRing is not null) { busyRing.IsActive = IsBusy && IsLoaded; busyRing.Visibility = IsBusy ? Visibility.Visible : Visibility.Collapsed; }
        playIcon.Visibility = IsBusy ? Visibility.Collapsed : Visibility.Visible;
    }
}
