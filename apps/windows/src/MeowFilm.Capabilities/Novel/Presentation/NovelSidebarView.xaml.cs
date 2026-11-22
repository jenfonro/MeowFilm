using MeowFilm.Desktop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace MeowFilm.Capabilities;

public sealed partial class NovelSidebarView : UserControl
{
    private readonly NovelCapability module;
    private IReadOnlyList<NovelSource>? displayedSources;
    private string? displayedSelection;
    private readonly Dictionary<string, (Button Row, TextBlock Name, FrameworkElement Check)> rows = [];

    internal NovelSidebarView(NovelCapability module)
    {
        this.module = module;
        InitializeComponent();
    }

    private void Manage_Click(object sender, RoutedEventArgs e) => module.RequestSettings();
    private void Import_Click(object sender, RoutedEventArgs e) => module.RunUi(ct => module.ImportSourcesAsync(this, ct));

    internal void Present()
    {
        bool changedSources = !ReferenceEquals(displayedSources, module.Sources.Sources);
        string? selection = module.ExploreSource?.InstallationId;
        if (!changedSources && displayedSelection == selection) return;

        if (changedSources)
        {
            displayedSources = module.Sources.Sources;
            var desired = module.Sources.Sources.Where(s => s.Enabled).ToArray();
            var identities = desired.Select(s => s.InstallationId).ToHashSet(StringComparer.Ordinal);
            foreach (var id in rows.Keys.Where(id => !identities.Contains(id)).ToArray())
            {
                SourceRows.Children.Remove(rows[id].Row);
                rows.Remove(id);
            }
            for (int index = 0; index < desired.Length; index++)
            {
                var source = desired[index];
                if (!rows.TryGetValue(source.InstallationId, out var item))
                {
                    string id = source.InstallationId;
                    var row = NovelUi.Button(source.Name, () =>
                    {
                        var current = module.Sources.Sources.FirstOrDefault(s => s.InstallationId == id);
                        if (current is not null) module.RunUi(ct => module.LoadDiscoveryAsync(current, null, true, ct));
                    }, style: "Novel.Discovery.Action");
                    row.ClearValue(MarginProperty);
                    row.ClearValue(Control.PaddingProperty);
                    row.Height = 34;
                    row.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                    var content = new Grid();
                    content.ColumnDefinitions.Add(new() { Width = new(16) });
                    content.ColumnDefinitions.Add(new());
                    content.ColumnDefinitions.Add(new() { Width = new(20) });
                    var dot = new Ellipse { Width = 4, Height = 4, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
                    dot.UseResource(Shape.FillProperty, "MeowFilm.Brush.AccentText");
                    content.Children.Add(dot);
                    var name = NovelUi.Text(source.Name, 12, true);
                    name.Margin = new(0);
                    name.TextWrapping = TextWrapping.NoWrap;
                    name.TextTrimming = TextTrimming.CharacterEllipsis;
                    name.VerticalAlignment = VerticalAlignment.Center;
                    Grid.SetColumn(name, 1);
                    content.Children.Add(name);
                    var checkPath = new Microsoft.UI.Xaml.Shapes.Path
                    {
                        Data = NovelPresentation.Geometry("M5,12 L9,16 19,6"),
                        StrokeThickness = 1.4,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round,
                        StrokeLineJoin = PenLineJoin.Round
                    };
                    checkPath.UseResource(StyleProperty, "Novel.Discovery.Icon");
                    var checkCanvas = new Canvas { Width = 24, Height = 24 };
                    checkCanvas.Children.Add(checkPath);
                    var check = new Viewbox { Width = 12, Height = 12, Child = checkCanvas, VerticalAlignment = VerticalAlignment.Center };
                    Grid.SetColumn(check, 2);
                    content.Children.Add(check);
                    row.Content = content;
                    item = (row, name, check);
                    rows.Add(id, item);
                }
                item.Row.IsEnabled = NovelCapability.CanExplore(source);
                item.Name.Text = source.Name;
                AutomationProperties.SetName(item.Row, source.Name);
                ToolTipService.SetToolTip(item.Row, source.Name);
                item.Check.Visibility = source.InstallationId == selection ? Visibility.Visible : Visibility.Collapsed;
                NovelView.PlaceChild(SourceRows, item.Row, index);
            }
        }
        else
        {
            if (displayedSelection is not null && rows.TryGetValue(displayedSelection, out var previous))
                previous.Check.Visibility = Visibility.Collapsed;
            if (selection is not null && rows.TryGetValue(selection, out var current))
                current.Check.Visibility = Visibility.Visible;
        }
        displayedSelection = selection;
    }
}
