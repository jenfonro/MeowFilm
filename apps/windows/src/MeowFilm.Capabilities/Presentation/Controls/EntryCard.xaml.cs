using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MeowFilm.Capabilities;

public partial class EntryCard : UserControl
{
    public static readonly RoutedEvent OpenRequestedEvent = EventManager.RegisterRoutedEvent(nameof(OpenRequested), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(EntryCard));
    public static readonly RoutedEvent PlayRequestedEvent = EventManager.RegisterRoutedEvent(nameof(PlayRequested), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(EntryCard));
    public static readonly RoutedEvent PreviewRequestedEvent = EventManager.RegisterRoutedEvent(nameof(PreviewRequested), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(EntryCard));
    public event RoutedEventHandler OpenRequested { add => AddHandler(OpenRequestedEvent, value); remove => RemoveHandler(OpenRequestedEvent, value); }
    public event RoutedEventHandler PlayRequested { add => AddHandler(PlayRequestedEvent, value); remove => RemoveHandler(PlayRequestedEvent, value); }
    public event RoutedEventHandler PreviewRequested { add => AddHandler(PreviewRequestedEvent, value); remove => RemoveHandler(PreviewRequestedEvent, value); }
    public static readonly DependencyProperty PreviewTitlesProperty = DependencyProperty.Register(nameof(PreviewTitles),
        typeof(IReadOnlyList<string>), typeof(EntryCard), new PropertyMetadata(Array.Empty<string>()));
    public IReadOnlyList<string> PreviewTitles { get => (IReadOnlyList<string>)GetValue(PreviewTitlesProperty); set => SetValue(PreviewTitlesProperty, value); }
    private CancellationTokenSource? request;
    internal CancellationToken PreviewCancellation => request?.Token ?? new CancellationToken(true);
    public EntryCard()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => { Cancel(); PreviewTitles = DataContext is MusicCatalogCard c ? c.PreviewSongs.Take(3).Select((p, i) => $"{i + 1} {p.Title}").ToArray() : []; };
        MouseEnter += (_, _) => RequestPreview(); MouseLeave += (_, _) => { if (!IsKeyboardFocusWithin) Cancel(); };
        IsKeyboardFocusWithinChanged += (_, _) => { if (IsKeyboardFocusWithin) RequestPreview(); else if (!IsMouseOver) Cancel(); };
        Unloaded += (_, _) => Cancel();
        IsVisibleChanged += (_, _) => { if (!IsVisible) Cancel(); };
    }
    private void RequestPreview()
    {
        if (request is not null || PreviewTitles.Count > 0 || !IsLoaded || !IsVisible || !IsEnabled ||
            DataContext is not MusicCatalogCard { CanRequestPreview: true }) return;
        request = new(); RaiseEvent(new RoutedEventArgs(PreviewRequestedEvent, this));
    }
    private void Cancel() { request?.Cancel(); request?.Dispose(); request = null; }
    private void Open_Click(object sender, RoutedEventArgs args) { args.Handled = true; RaiseEvent(new RoutedEventArgs(OpenRequestedEvent, this)); }
    private void Play_Click(object sender, RoutedEventArgs args) { args.Handled = true; RaiseEvent(new RoutedEventArgs(PlayRequestedEvent, this)); }
}
