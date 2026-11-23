using MeowFilm.Desktop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeowFilm.Capabilities;

public sealed class CardPlayGlyph : UserControl
{
    public static readonly DependencyProperty IsBusyProperty = DependencyProperty.Register(nameof(IsBusy), typeof(bool), typeof(CardPlayGlyph),
        new PropertyMetadata(false, (owner, _) => ((CardPlayGlyph)owner).Refresh()));
    public bool IsBusy { get => (bool)GetValue(IsBusyProperty); set => SetValue(IsBusyProperty, value); }
    private readonly ProgressRing busy = new() { Width = 24, Height = 24 };
    private readonly FontIcon play = new() { Glyph = "\uE768", FontSize = 24 };
    private IDisposable? visibility;
    private bool visible;
    public CardPlayGlyph()
    {
        Width = 24; Height = 28;
        var grid = new Grid(); grid.Children.Add(play); grid.Children.Add(busy); Content = grid;
        Loaded += (_, _) =>
        {
            visibility?.Dispose();
            visibility = DesktopVisual.ObserveVisibility(this, value => { visible = value; Refresh(); });
        };
        Unloaded += (_, _) =>
        {
            if (IsLoaded) return;
            visibility?.Dispose(); visibility = null;
            visible = false;
            busy.IsActive = false;
        };
        RegisterPropertyChangedCallback(ForegroundProperty, (_, _) => { busy.Foreground = play.Foreground = Foreground; });
    }
    private void Refresh()
    {
        busy.IsActive = IsBusy && visible;
        busy.Visibility = IsBusy ? Visibility.Visible : Visibility.Collapsed;
        play.Visibility = IsBusy ? Visibility.Collapsed : Visibility.Visible;
    }
}
