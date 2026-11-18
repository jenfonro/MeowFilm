using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MeowFilm.Capabilities;

public sealed class MusicLyricRowView : UserControl
{
    private readonly StackPanel stack = new();
    private readonly TextBlock plain = new() { FontSize = 20, TextWrapping = TextWrapping.Wrap };
    private readonly MusicWrapPanel words = new();
    private readonly TextBlock translation = new() { Name = "LyricTranslation", FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0) };
    private readonly TextBlock romanization = new() { FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0) };
    private MusicCapability? module;
    private MusicLyricRow? row;
    public MusicLyricRowView()
    {
        IsTabStop = false; IsHitTestVisible = false;
        stack.Children.Add(plain); stack.Children.Add(words); stack.Children.Add(translation); stack.Children.Add(romanization);
        Content = new Border { Padding = new Thickness(76, 11, 16, 11), MinHeight = 47, Child = stack };
        DataContextChanged += (_, _) => Reload();
        Loaded += (_, _) =>
        {
            for (DependencyObject? parent = VisualTreeHelper.GetParent(this); parent is not null; parent = VisualTreeHelper.GetParent(parent))
                if (parent is MusicPlayerDetailView { DataContext: MusicCapability capability }) { module = capability; break; }
            if (module is not null) module.PropertyChanged += Changed;
            Reload();
        };
        Unloaded += (_, _) => { if (module is not null) module.PropertyChanged -= Changed; module = null; };
    }
    private void Reload()
    {
        if (!ReferenceEquals(row, DataContext))
        {
            row = DataContext as MusicLyricRow; words.Children.Clear();
            if (row?.Words is { } timed)
                foreach (var word in timed) words.Children.Add(new MusicLyricWordView { Word = word, FontSize = 20 });
        }
        plain.Text = row?.Text ?? ""; plain.Visibility = words.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        translation.Text = row?.Translation ?? ""; romanization.Text = row?.Romanization ?? "";
        Refresh();
    }
    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MusicCapability.LyricDisplayIndex) or nameof(MusicCapability.ShowLyricTranslation) or nameof(MusicCapability.HasLyricPreview)) Refresh();
    }
    private void Refresh()
    {
        bool selected = module is { } owner && owner.LyricDisplayIndex >= 0 && owner.LyricDisplayIndex < owner.Lyrics.Count && ReferenceEquals(owner.Lyrics[owner.LyricDisplayIndex], row);
        var normal = MusicVisuals.Brush(selected ? "#EBEAED" : "#8B898C");
        plain.Foreground = translation.Foreground = romanization.Foreground = normal;
        foreach (var word in words.Children.OfType<MusicLyricWordView>())
        {
            word.Foreground = MusicVisuals.Brush(selected ? "#8B898C" : "#68666A");
            word.Highlight = MusicVisuals.Brush(selected ? "#F4F3F6" : "#928F90");
        }
        translation.Visibility = module?.ShowLyricTranslation != false && translation.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        romanization.Visibility = romanization.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
