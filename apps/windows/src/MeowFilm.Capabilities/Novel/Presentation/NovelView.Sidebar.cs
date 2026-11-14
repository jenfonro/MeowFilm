using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace MeowFilm.Capabilities;

public sealed partial class NovelView
{
    private readonly StackPanel sidebar = new() { Margin = new(15, 27, 15, 14) };
    private object? sidebarStamp;
    internal FrameworkElement SidebarView => sidebar;

    private void PresentSidebar()
    {
        object stamp = (module.Sources.Sources, module.ExploreSource);
        if (Equals(sidebarStamp, stamp)) return;
        sidebarStamp = stamp;
        if (sidebar.Resources.MergedDictionaries.Count == 0)
        {
            sidebar.Resources.MergedDictionaries.Add(new() { Source = new Uri(DiscoveryThemeUri, UriKind.Relative) });
            AutomationProperties.SetName(sidebar, "小说书源");
        }
        sidebar.Children.Clear();
        var divider = new Border { Height = 1, Margin = new(0, 0, 0, 14) };
        divider.SetResourceReference(Border.BackgroundProperty, "MeowFilm.Brush.SidebarBorder"); sidebar.Children.Add(divider);
        var heading = new Grid { Height = 28 };
        var label = DiscoveryText("已启用书源", 12, true); label.VerticalAlignment = VerticalAlignment.Center;
        heading.Children.Add(label);
        var manage = DiscoveryButton("书源管理", module.RequestSettings);
        manage.Content = DiscoveryIcon("M9,3 L8,6 5,7 3,11 5,13 6,17 9,18 10,21 14,21 15,18 18,17 19,13 21,11 19,7 16,6 15,3 Z M15,12 A3,3 0 1 1 9,12 A3,3 0 1 1 15,12");
        manage.Width = 26; manage.Height = 26; manage.HorizontalAlignment = HorizontalAlignment.Right; manage.ToolTip = "书源管理";
        heading.Children.Add(manage); sidebar.Children.Add(heading);
        foreach (var source in module.Sources.Sources.Where(s => s.Enabled))
        {
            bool selected = source.InstallationId == module.ExploreSource?.InstallationId;
            var row = DiscoveryButton(source.Name, () => module.RunUi(ct => module.LoadDiscoveryAsync(source, null, true, ct)));
            row.Height = 34; row.IsEnabled = NovelCapability.CanExplore(source);
            row.HorizontalContentAlignment = HorizontalAlignment.Stretch; row.ToolTip = source.Name;
            var content = new Grid();
            content.ColumnDefinitions.Add(new() { Width = new(16) }); content.ColumnDefinitions.Add(new());
            content.ColumnDefinitions.Add(new() { Width = new(20) });
            var dot = new System.Windows.Shapes.Ellipse { Width = 4, Height = 4, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
            dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "MeowFilm.Brush.AccentText"); content.Children.Add(dot);
            var name = DiscoveryText(source.Name, 12, true); name.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(name, 1); content.Children.Add(name);
            if (selected) { var check = DiscoveryIcon("M5,12 L9,16 19,6", 12); Grid.SetColumn(check, 2); content.Children.Add(check); }
            row.Content = content; sidebar.Children.Add(row);
        }
        var import = DiscoveryButton("导入书源", () => module.RunUi(ct => module.ImportSourcesAsync(sidebar, ct)));
        import.Content = "＋  导入书源"; import.Height = 32; import.Margin = new(0, 12, 0, 0);
        import.BorderThickness = new(1); import.SetResourceReference(Control.BorderBrushProperty, "MeowFilm.Brush.Border");
        sidebar.Children.Add(import);
    }
}
