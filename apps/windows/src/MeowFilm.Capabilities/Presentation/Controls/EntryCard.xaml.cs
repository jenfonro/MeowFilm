using System.Windows;
using System.Windows.Controls;

namespace MeowFilm.Capabilities;

public partial class EntryCard : UserControl
{
    public static readonly RoutedEvent OpenRequestedEvent = EventManager.RegisterRoutedEvent(nameof(OpenRequested), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(EntryCard));
    public static readonly RoutedEvent PlayRequestedEvent = EventManager.RegisterRoutedEvent(nameof(PlayRequested), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(EntryCard));
    public event RoutedEventHandler OpenRequested { add => AddHandler(OpenRequestedEvent, value); remove => RemoveHandler(OpenRequestedEvent, value); }
    public event RoutedEventHandler PlayRequested { add => AddHandler(PlayRequestedEvent, value); remove => RemoveHandler(PlayRequestedEvent, value); }
    public EntryCard() => InitializeComponent();
    private void Open_Click(object sender, RoutedEventArgs args) { args.Handled = true; RaiseEvent(new RoutedEventArgs(OpenRequestedEvent, this)); }
    private void Play_Click(object sender, RoutedEventArgs args) { args.Handled = true; RaiseEvent(new RoutedEventArgs(PlayRequestedEvent, this)); }
}
