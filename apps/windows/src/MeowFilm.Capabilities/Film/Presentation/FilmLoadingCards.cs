using System.Windows;
using System.Windows.Controls;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

// Only prepares placeholders for the visible card layout; never creates artwork or reads data.
public sealed class FilmLoadingCards : WrapPanel
{
    public bool IsGrid { get; set; }

    public FilmLoadingCards()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        Loaded += (_, _) => UpdateCards();
        SizeChanged += (_, _) => UpdateCards();
        IsVisibleChanged += (_, _) => { if (IsVisible) UpdateCards(); };
    }

    protected override Size MeasureOverride(Size constraint)
    {
        if (IsGrid || !IsVisible) return base.MeasureOverride(constraint);
        UpdateCards(constraint.Width);
        foreach (UIElement child in Children) child.Measure(new(ItemWidth, ItemHeight));
        // A fixed complete row must not re-wrap when WPF rounds its height.
        return new(constraint.Width, ItemHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (IsGrid) return base.ArrangeOverride(finalSize);
        for (int index = 0; index < Children.Count; index++)
            Children[index].Arrange(new(index * ItemWidth, 0, ItemWidth, ItemHeight));
        return finalSize;
    }

    private void UpdateCards()
    {
        if (!IsVisible || ActualWidth <= 0) return;
        UpdateCards(ActualWidth);
    }

    private void UpdateCards(double availableWidth)
    {
        int columns = IsGrid
            ? (int)new FilmGridColumnsConverter().Convert(availableWidth, typeof(int), null!, System.Globalization.CultureInfo.InvariantCulture)
            : Shelf.MeasureCards(availableWidth, 176, 16).Count;
        Orientation = IsGrid ? Orientation.Horizontal : Orientation.Vertical;
        int count = columns * (IsGrid ? 2 : 1);
        double width = IsGrid ? availableWidth / columns : Shelf.MeasureCards(availableWidth, 176, 16).Width;
        ItemWidth = width;
        ItemHeight = IsGrid ? 330 : 294 * Math.Max(1, width - 16) / 176;
        while (Children.Count > count) Children.RemoveAt(Children.Count - 1);
        while (Children.Count < count)
        {
            var card = new StackPanel { Width = 176, HorizontalAlignment = IsGrid ? HorizontalAlignment.Center : HorizontalAlignment.Left };
            card.Children.Add(new LoadingSkeleton { Height = 264, CornerRadius = new(14) });
            card.Children.Add(new LoadingSkeleton { Height = 15, Width = 132, CornerRadius = new(5), Margin = new(0, 9, 0, 0) });
            Children.Add(IsGrid ? card : new Viewbox { Child = card, Margin = new(0, 0, 16, 0) });
        }
    }
}
