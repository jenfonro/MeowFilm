using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace MeowFilm.Desktop;

/// <summary>Shared labelled, in-place settings fields, with line-based editor sizing.</summary>
public sealed class ManagementForm(bool boxedInputs = false) : StackPanel
{
    public TextBox Field(string label, string value, int rows = 1, string? id = null, string? placeholder = null)
    {
        var section = new StackPanel { Margin = boxedInputs ? new(0, 0, 0, 14) : new(14, 11, 14, 10) };
        var heading = new TextBlock { Text = label, FontSize = boxedInputs ? 13 : 12, Margin = new(0, 0, 0, boxedInputs ? 8 : 5), TextWrapping = TextWrapping.Wrap };
        heading.SetResourceReference(FrameworkElement.StyleProperty, "MeowFilm.MutedText");
        section.Children.Add(heading);
        var input = new TextBox { Text = value, AcceptsReturn = rows > 1, AcceptsTab = rows > 1, TextWrapping = TextWrapping.Wrap,
            Height = rows > 1 ? Math.Max(72, rows * 23) : 25, MinHeight = 0,
            VerticalContentAlignment = rows > 1 ? VerticalAlignment.Top : VerticalAlignment.Center,
            VerticalScrollBarVisibility = rows > 1 ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden,
            BorderThickness = new(0), Padding = new(0), FontSize = 17 };
        AutomationProperties.SetName(input, label); if (id is not null) AutomationProperties.SetAutomationId(input, id);
        if (boxedInputs) { input.SetResourceReference(StyleProperty, "MeowFilm.ManagementInput"); input.BorderThickness = new(1); input.Padding = new(12, 9, 12, 9); input.FontSize = 13; input.Height = rows > 1 ? Math.Max(92, rows * 19.5 + 18) : 40; }
        else { input.SetResourceReference(StyleProperty, "MeowFilm.TextBox"); input.Background = Brushes.Transparent; }
        input.Tag = placeholder;
        section.Children.Add(input); Section(section); return input;
    }
    public CheckBox Check(string title, bool value)
    {
        var check = new CheckBox { Content = title, IsChecked = value, Margin = boxedInputs ? new(0, 0, 0, 14) : new(14, 6, 14, 6), MinHeight = 32, VerticalContentAlignment = VerticalAlignment.Center, Tag = boxedInputs ? "round" : null };
        check.SetResourceReference(StyleProperty, "MeowFilm.ManagementCheck");
        AutomationProperties.SetName(check, title); Section(check); return check;
    }
    public void Section(UIElement value)
    {
        Children.Add(value);
        var line = new Border { Height = 0.5, Opacity = .5 };
        line.SetResourceReference(Border.BackgroundProperty, "MeowFilm.Brush.Border"); Children.Add(line);
    }
    public Border Card()
    {
        var card = new Border { CornerRadius = new(boxedInputs ? 22 : 18), BorderThickness = new(1),
            Padding = boxedInputs ? new(16, 16, 16, 2) : new(0), Child = this, ClipToBounds = true };
        card.SetResourceReference(FrameworkElement.StyleProperty, "MeowFilm.FloatingSurface");
        return card;
    }
}

public sealed record ManagementImportItem(string Title, string Detail, string Status);
public sealed class ManagementImportSelection : DockPanel
{
    public IReadOnlySet<int> Selected => selected;
    private readonly HashSet<int> selected;
    private readonly CheckBox all = new();
    private readonly CheckBox[] checks;
    public ManagementImportSelection(IReadOnlyList<ManagementImportItem> items, IEnumerable<int>? initiallySelected = null)
    {
        selected = (initiallySelected ?? Enumerable.Range(0, items.Count)).ToHashSet();
        all.Margin = new(0, 0, 0, 8); all.Tag = "round"; all.SetResourceReference(StyleProperty, "MeowFilm.ManagementCheck"); SetDock(all, Dock.Top); Children.Add(all);
        var rows = new StackPanel(); checks = new CheckBox[items.Count];
        for (int index = 0; index < items.Count; index++)
        {
            int at = index; var item = items[index]; var line = new DockPanel { Margin = new(0, 0, 0, 0), MinHeight = 58 };
            var check = checks[index] = new CheckBox { IsChecked = selected.Contains(index), Tag = "round", VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 12, 0) }; check.SetResourceReference(StyleProperty, "MeowFilm.ManagementCheck");
            AutomationProperties.SetName(check, "选择 " + item.Title); check.Click += (_, _) => { if (check.IsChecked == true) selected.Add(at); else selected.Remove(at); Count(); };
            SetDock(check, Dock.Left); line.Children.Add(check);
            bool existing = item.Status is "已有" or "已存在";
            var status = new TextBlock { Text = item.Status, FontSize = 12 };
            status.SetResourceReference(FrameworkElement.StyleProperty, existing ? "MeowFilm.MutedText" : "MeowFilm.AccentText");
            var badge = new Border { Child = status, Tag = existing ? "existing" : "new", CornerRadius = new(10), Padding = new(8, 3, 8, 3), VerticalAlignment = VerticalAlignment.Center };
            badge.SetResourceReference(FrameworkElement.StyleProperty, "MeowFilm.ImportStatusBadge"); SetDock(badge, Dock.Right); line.Children.Add(badge);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 12, 0) };
            var title = new TextBlock { Text = item.Title, FontSize = 14, FontWeight = FontWeights.Medium, TextWrapping = TextWrapping.Wrap }; title.SetResourceReference(FrameworkElement.StyleProperty, "MeowFilm.Text"); text.Children.Add(title);
            var detail = new TextBlock { Text = item.Detail, FontSize = 12, Margin = new(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap }; detail.SetResourceReference(FrameworkElement.StyleProperty, "MeowFilm.MutedText"); text.Children.Add(detail);
            line.Children.Add(text);
            var divider = new Border { Child = line, BorderThickness = new(0, 0, 0, 1) }; divider.SetResourceReference(Border.BorderBrushProperty, "MeowFilm.Brush.Border"); rows.Children.Add(divider);
        }
        Children.Add(new ScrollViewer { Content = rows, MaxHeight = 390, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        all.Click += (_, _) => { selected.Clear(); if (all.IsChecked == true) selected.UnionWith(Enumerable.Range(0, items.Count)); for (int index = 0; index < checks.Length; index++) checks[index].IsChecked = selected.Contains(index); Count(); }; Count();
    }
    private void Count() { all.Content = $"全选（{selected.Count}/{checks.Length}）"; all.IsChecked = checks.Length > 0 && selected.Count == checks.Length; }
}
