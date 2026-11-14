using System.Windows;
using System.Windows.Controls;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

// Only prepares placeholders for the visible card layout; never creates artwork or reads data.
public sealed class FilmLoadingCards : AdaptiveCardPanel
{
    public bool IsGrid { get; set; }

    public FilmLoadingCards()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
    }

    protected override Size MeasureOverride(Size constraint)
    {
        double width = double.IsInfinity(constraint.Width) ? MaximumCardWidth : constraint.Width;
        int columns = Shelf.MeasureCards(width, MinimumCardWidth, CardGap).Count;
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
        return base.MeasureOverride(constraint);
    }
}
