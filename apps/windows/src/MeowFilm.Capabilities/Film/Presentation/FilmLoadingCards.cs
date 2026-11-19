using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

// Only prepares placeholders for the visible card layout; never creates artwork or reads data.
public sealed class FilmLoadingCards : AdaptiveCardPanel
{
    public static readonly DependencyProperty IsGridProperty = DependencyProperty.Register(nameof(IsGrid),
        typeof(bool), typeof(FilmLoadingCards), new PropertyMetadata(false, (owner, _) => ((FilmLoadingCards)owner).QueuePlaceholders()));
    public bool IsGrid { get => (bool)GetValue(IsGridProperty); set => SetValue(IsGridProperty, value); }
    private bool placeholdersQueued;

    public FilmLoadingCards()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) => QueuePlaceholders();
        SizeChanged += (_, _) => QueuePlaceholders();
        RegisterPropertyChangedCallback(MinimumCardWidthProperty, (_, _) => QueuePlaceholders());
        RegisterPropertyChangedCallback(CardGapProperty, (_, _) => QueuePlaceholders());
        RegisterPropertyChangedCallback(MaximumColumnsProperty, (_, _) => QueuePlaceholders());
    }

    private void QueuePlaceholders()
    {
        if (placeholdersQueued) return;
        placeholdersQueued = DispatcherQueue.TryEnqueue(() =>
        {
            placeholdersQueued = false;
            UpdatePlaceholders();
        });
    }

    private void UpdatePlaceholders()
    {
        if (!IsLoaded || ActualWidth <= 0) return;
        int columns = Math.Min(MaximumColumns, Shelf.MeasureCards(ActualWidth, MinimumCardWidth, CardGap).Count);
        int count = columns * (IsGrid ? 2 : 1);
        while (Children.Count > count) Children.RemoveAt(Children.Count - 1);
        while (Children.Count < count)
        {
            var card = new StackPanel();
            card.Children.Add(new AspectRatioDecorator
            {
                AspectRatio = new(176, 264),
                Child = new LoadingSkeleton { CornerRadius = new(14) }
            });
            card.Children.Add(new LoadingSkeleton
            {
                Height = 15, Width = 132, CornerRadius = new(5), Margin = new(0, 9, 0, 6)
            });
            Children.Add(card);
        }
    }
}
