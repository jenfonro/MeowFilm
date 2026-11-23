using MeowFilm.Desktop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeowFilm.Capabilities;

public sealed partial class CardPlayGlyph : UserControl
{
    public static readonly DependencyProperty IsBusyProperty = DependencyProperty.Register(nameof(IsBusy), typeof(bool), typeof(CardPlayGlyph),
        new PropertyMetadata(false, (owner, _) => ((CardPlayGlyph)owner).Refresh()));
    public bool IsBusy { get => (bool)GetValue(IsBusyProperty); set => SetValue(IsBusyProperty, value); }

    private IDisposable? visibility;
    private bool visible;

    public CardPlayGlyph()
    {
        InitializeComponent();
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
            BusyRing.IsActive = false;
        };
    }

    private void Refresh()
    {
        BusyRing.IsActive = IsBusy && visible;
        BusyRing.Visibility = IsBusy ? Visibility.Visible : Visibility.Collapsed;
        PlayIcon.Visibility = IsBusy ? Visibility.Collapsed : Visibility.Visible;
    }
}
