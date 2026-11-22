using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

public sealed class MusicLyricRowView : UserControl
{
    private const int WordPoolLimit = 24;
    private static readonly SolidColorBrush SelectedTextBrush = MusicVisuals.Brush("#EBEAED");
    private static readonly SolidColorBrush UnselectedTextBrush = MusicVisuals.Brush("#8B898C");
    private static readonly SolidColorBrush SelectedWordNormalBrush = MusicVisuals.Brush("#8B898C");
    private static readonly SolidColorBrush UnselectedWordNormalBrush = MusicVisuals.Brush("#68666A");
    private static readonly SolidColorBrush SelectedWordHighlightBrush = MusicVisuals.Brush("#F4F3F6");
    private static readonly SolidColorBrush UnselectedWordHighlightBrush = MusicVisuals.Brush("#928F90");

    private readonly StackPanel stack = new();
    private readonly TextBlock plain = new() { FontSize = 20, TextWrapping = TextWrapping.Wrap };
    private readonly WrapPanel words = new();
    private readonly TextBlock translation = new() { Name = "LyricTranslation", FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0) };
    private readonly TextBlock romanization = new() { FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0) };
    private readonly List<MusicLyricWordView> wordPool = [];
    private MusicLyricsController? lyrics;
    private MusicLyricRow? row;
    private bool? lastSelected;
    private bool? lastShowTranslation;

    public MusicLyricRowView()
    {
        IsTabStop = false; IsHitTestVisible = false;
        stack.Children.Add(plain); stack.Children.Add(words); stack.Children.Add(translation); stack.Children.Add(romanization);
        Content = new Border { Padding = new Thickness(76, 11, 16, 11), MinHeight = 47, Child = stack };
        DataContextChanged += (_, _) => Reload();
        Loaded += (_, _) =>
        {
            for (DependencyObject? parent = VisualTreeHelper.GetParent(this); parent is not null; parent = VisualTreeHelper.GetParent(parent))
                if (parent is MusicPlayerDetailView detail) { lyrics = detail.Lyrics; break; }
            if (lyrics is not null) lyrics.PropertyChanged += Changed;
            Reload();
        };
        Unloaded += (_, _) =>
        {
            if (lyrics is not null) lyrics.PropertyChanged -= Changed;
            lyrics = null;
        };
    }

    private void Reload()
    {
        if (!ReferenceEquals(row, DataContext))
        {
            row = DataContext as MusicLyricRow;
            SyncWordViews(row?.Words);
            lastSelected = null;
            lastShowTranslation = null;
        }
        plain.Text = row?.Text ?? "";
        plain.Visibility = words.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        translation.Text = row?.Translation ?? "";
        romanization.Text = row?.Romanization ?? "";
        Refresh(force: true);
    }

    private void SyncWordViews(IReadOnlyList<MusicLyricWord>? timed)
    {
        int targetCount = timed?.Count ?? 0;
        int currentCount = words.Children.Count;
        int reuseCount = Math.Min(currentCount, targetCount);

        for (int i = 0; i < reuseCount; i++)
        {
            var view = (MusicLyricWordView)words.Children[i];
            view.Word = timed![i];
        }

        if (targetCount > currentCount)
        {
            for (int i = currentCount; i < targetCount; i++)
            {
                MusicLyricWordView view;
                if (wordPool.Count > 0)
                {
                    int last = wordPool.Count - 1;
                    view = wordPool[last];
                    wordPool.RemoveAt(last);
                    view.Word = timed![i];
                }
                else
                {
                    view = new MusicLyricWordView { Word = timed![i], FontSize = 20 };
                }
                words.Children.Add(view);
            }
        }
        else if (targetCount < currentCount)
        {
            for (int i = currentCount - 1; i >= targetCount; i--)
            {
                var view = (MusicLyricWordView)words.Children[i];
                words.Children.RemoveAt(i);
                view.StopHighlight(0);
                if (wordPool.Count < WordPoolLimit)
                    wordPool.Add(view);
            }
        }
    }

    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MusicLyricsController.LyricDisplayIndex) or nameof(MusicLyricsController.ShowLyricTranslation) or nameof(MusicLyricsController.HasLyricPreview))
            Refresh();
    }

    private void Refresh(bool force = false)
    {
        bool selected = lyrics is { } owner && owner.LyricDisplayIndex >= 0 && owner.LyricDisplayIndex < owner.Lyrics.Count && ReferenceEquals(owner.Lyrics[owner.LyricDisplayIndex], row);
        bool showTranslation = lyrics?.ShowLyricTranslation != false && translation.Text.Length > 0;
        if (force || lastSelected != selected)
        {
            lastSelected = selected;
            var normal = selected ? SelectedTextBrush : UnselectedTextBrush;
            plain.Foreground = translation.Foreground = romanization.Foreground = normal;
            var wordNormal = selected ? SelectedWordNormalBrush : UnselectedWordNormalBrush;
            var wordHighlight = selected ? SelectedWordHighlightBrush : UnselectedWordHighlightBrush;
            for (int i = 0; i < words.Children.Count; i++)
            {
                if (words.Children[i] is MusicLyricWordView word)
                {
                    word.Foreground = wordNormal;
                    word.Highlight = wordHighlight;
                }
            }
        }
        if (force || lastShowTranslation != showTranslation)
        {
            lastShowTranslation = showTranslation;
            translation.Visibility = showTranslation ? Visibility.Visible : Visibility.Collapsed;
        }
        romanization.Visibility = romanization.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
