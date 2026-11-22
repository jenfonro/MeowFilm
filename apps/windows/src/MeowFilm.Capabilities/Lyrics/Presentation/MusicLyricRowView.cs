using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

public sealed class MusicLyricRowView : UserControl
{
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
    private MusicLyricsState? lyricsState;
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
                if (parent is FrameworkElement { DataContext: MusicLyricsState state }) { lyricsState = state; break; }
            if (lyricsState is not null) lyricsState.PropertyChanged += Changed;
            Reload();
        };
        Unloaded += (_, _) => { if (lyricsState is not null) lyricsState.PropertyChanged -= Changed; lyricsState = null; };
    }
    private void Reload()
    {
        if (!ReferenceEquals(row, DataContext))
        {
            row = DataContext as MusicLyricRow; words.Children.Clear();
            if (row?.Words is { } timed)
                foreach (var word in timed) words.Children.Add(new MusicLyricWordView { Word = word, FontSize = 20 });
            lastSelected = null;
            lastShowTranslation = null;
        }
        plain.Text = row?.Text ?? ""; plain.Visibility = words.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        translation.Text = row?.Translation ?? ""; romanization.Text = row?.Romanization ?? "";
        Refresh(force: true);
    }
    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MusicLyricsState.LyricDisplayIndex) or nameof(MusicLyricsState.ShowLyricTranslation) or nameof(MusicLyricsState.HasLyricPreview)) Refresh();
    }
    private void Refresh(bool force = false)
    {
        bool selected = lyricsState is { } owner && owner.LyricDisplayIndex >= 0 && owner.LyricDisplayIndex < owner.Lyrics.Count && ReferenceEquals(owner.Lyrics[owner.LyricDisplayIndex], row);
        bool showTranslation = lyricsState?.ShowLyricTranslation != false && translation.Text.Length > 0;
        if (force || lastSelected != selected)
        {
            lastSelected = selected;
            var normal = selected ? SelectedTextBrush : UnselectedTextBrush;
            plain.Foreground = translation.Foreground = romanization.Foreground = normal;
            var wordNormal = selected ? SelectedWordNormalBrush : UnselectedWordNormalBrush;
            var wordHighlight = selected ? SelectedWordHighlightBrush : UnselectedWordHighlightBrush;
            foreach (var word in words.Children.OfType<MusicLyricWordView>())
            {
                word.Foreground = wordNormal;
                word.Highlight = wordHighlight;
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
