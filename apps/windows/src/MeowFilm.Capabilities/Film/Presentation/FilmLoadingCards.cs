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

    private void UpdateCards()
    {
        if (!IsVisible || ActualWidth <= 0) return;
        int columns = IsGrid
            ? (int)new FilmGridColumnsConverter().Convert(ActualWidth, typeof(int), null!, System.Globalization.CultureInfo.InvariantCulture)
            : Math.Max(1, (int)Math.Ceiling(ActualWidth / 192));
        Orientation = IsGrid ? Orientation.Horizontal : Orientation.Vertical;
        int count = columns * (IsGrid ? 2 : 1);
        double width = IsGrid ? ActualWidth / columns : 192;
        ItemWidth = width;
        ItemHeight = IsGrid ? 330 : 294;
        while (Children.Count > count) Children.RemoveAt(Children.Count - 1);
        while (Children.Count < count)
        {
            var card = new StackPanel { Width = 176, HorizontalAlignment = IsGrid ? HorizontalAlignment.Center : HorizontalAlignment.Left };
            card.Children.Add(new LoadingSkeleton { Height = 264, CornerRadius = new(14) });
            card.Children.Add(new LoadingSkeleton { Height = 15, Width = 132, CornerRadius = new(5), Margin = new(0, 9, 0, 0) });
            Children.Add(card);
        }
    }
}
