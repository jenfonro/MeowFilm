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
    public CardPlayGlyph()
    {
        Width = 24; Height = 28;
        var grid = new Grid(); grid.Children.Add(play); grid.Children.Add(busy); Content = grid;
        Loaded += (_, _) => Refresh(); Unloaded += (_, _) => busy.IsActive = false;
        RegisterPropertyChangedCallback(ForegroundProperty, (_, _) => { busy.Foreground = play.Foreground = Foreground; });
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => Refresh());
    }
    private void Refresh()
    {
        busy.IsActive = IsBusy && IsLoaded && Visibility == Visibility.Visible;
        busy.Visibility = IsBusy ? Visibility.Visible : Visibility.Collapsed;
        play.Visibility = IsBusy ? Visibility.Collapsed : Visibility.Visible;
    }
}
