using MeowFilm.Desktop;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.System;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MeowFilm.Capabilities;

public sealed partial class NovelView
{
    private readonly StackPanel sidebar = new() { Margin = new(15, 27, 15, 14) };
    private IReadOnlyList<NovelSource>? sidebarSources;
    private string? sidebarSelection;
    private readonly Dictionary<string, (Button Row, TextBlock Name, FrameworkElement Check)> sidebarRows = [];
    private readonly StackPanel sourceRows = new();
    internal FrameworkElement SidebarView => sidebar;

    private void PresentSidebar()
    {
        bool changedSources = !ReferenceEquals(sidebarSources, module.Sources.Sources);
        string? selection = module.ExploreSource?.InstallationId;
        if (!changedSources && sidebarSelection == selection) return;
        if (sidebar.Resources.MergedDictionaries.Count == 0)
        {
            sidebar.Resources.MergedDictionaries.Add(new() { Source = new Uri(DiscoveryThemeUri) });
            AutomationProperties.SetName(sidebar, "小说书源");
        }
        if (sidebar.Children.Count == 0)
        {
            var divider = new Border { Height = 1, Margin = new(0, 0, 0, 14) };
            divider.UseResource(Border.BackgroundProperty, "MeowFilm.Brush.SidebarBorder"); sidebar.Children.Add(divider);
            var heading = new Grid { Height = 28 };
            var label = DiscoveryText("已启用书源", 12, true); label.VerticalAlignment = VerticalAlignment.Center;
            heading.Children.Add(label);
            var manage = DiscoveryButton("书源管理", module.RequestSettings);
            manage.Content = DiscoveryIcon("M9,3 L8,6 5,7 3,11 5,13 6,17 9,18 10,21 14,21 15,18 18,17 19,13 21,11 19,7 16,6 15,3 Z M15,12 A3,3 0 1 1 9,12 A3,3 0 1 1 15,12");
            manage.Width = 26; manage.Height = 26; manage.HorizontalAlignment = HorizontalAlignment.Right; ToolTipService.SetToolTip(manage, "书源管理");
            heading.Children.Add(manage); sidebar.Children.Add(heading);
            sidebar.Children.Add(sourceRows);
            var import = DiscoveryButton("导入书源", () => module.RunUi(ct => module.ImportSourcesAsync(sidebar, ct)));
            import.Content = "＋  导入书源"; import.Height = 32; import.Margin = new(0, 12, 0, 0);
            import.BorderThickness = new(1); import.UseResource(Control.BorderBrushProperty, "MeowFilm.Brush.Border");
            sidebar.Children.Add(import);
        }
        if (changedSources)
        {
            sidebarSources = module.Sources.Sources;
            var desired = module.Sources.Sources.Where(s => s.Enabled).ToArray();
            var identities = desired.Select(s => s.InstallationId).ToHashSet(StringComparer.Ordinal);
            foreach (var id in sidebarRows.Keys.Where(id => !identities.Contains(id)).ToArray())
            { sourceRows.Children.Remove(sidebarRows[id].Row); sidebarRows.Remove(id); }
            for (int index = 0; index < desired.Length; index++)
            {
                var source = desired[index];
                if (!sidebarRows.TryGetValue(source.InstallationId, out var item))
                {
                    string id = source.InstallationId;
                    var row = DiscoveryButton(source.Name, () =>
                    {
                        var current = module.Sources.Sources.FirstOrDefault(s => s.InstallationId == id);
                        if (current is not null) module.RunUi(ct => module.LoadDiscoveryAsync(current, null, true, ct));
                    });
                    row.Height = 34; row.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                    var content = new Grid();
                    content.ColumnDefinitions.Add(new() { Width = new(16) }); content.ColumnDefinitions.Add(new());
                    content.ColumnDefinitions.Add(new() { Width = new(20) });
                    var dot = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 4, Height = 4, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
                    dot.UseResource(Microsoft.UI.Xaml.Shapes.Shape.FillProperty, "MeowFilm.Brush.AccentText"); content.Children.Add(dot);
                    var name = DiscoveryText(source.Name, 12, true); name.VerticalAlignment = VerticalAlignment.Center;
                    Grid.SetColumn(name, 1); content.Children.Add(name);
                    var check = DiscoveryIcon("M5,12 L9,16 19,6", 12); Grid.SetColumn(check, 2); content.Children.Add(check);
                    row.Content = content; item = (row, name, check); sidebarRows.Add(id, item);
                }
                item.Row.IsEnabled = NovelCapability.CanExplore(source); item.Name.Text = source.Name;
                AutomationProperties.SetName(item.Row, source.Name); ToolTipService.SetToolTip(item.Row, source.Name);
                item.Check.Visibility = source.InstallationId == selection ? Visibility.Visible : Visibility.Collapsed;
                PlaceChild(sourceRows, item.Row, index);
            }
        }
        else
        {
            if (sidebarSelection is not null && sidebarRows.TryGetValue(sidebarSelection, out var previous)) previous.Check.Visibility = Visibility.Collapsed;
            if (selection is not null && sidebarRows.TryGetValue(selection, out var current)) current.Check.Visibility = Visibility.Visible;
        }
        sidebarSelection = selection;
    }
}
