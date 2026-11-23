using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace MeowFilm.Desktop;

// A scope action opens one native menu below the search field; it is not an editable value field.
public sealed class ShellSearchScope : Button
{
    public static readonly DependencyProperty SelectedValueProperty = DependencyProperty.Register(nameof(SelectedValue),
        typeof(string), typeof(ShellSearchScope), new PropertyMetadata(null, (owner, _) => ((ShellSearchScope)owner).RefreshSelection()));
    private readonly MenuFlyout menu = new();
    private IReadOnlyList<DesktopSearchTarget> items = [];
    private bool isOpen;

    public ShellSearchScope()
    {
        menu.ShouldConstrainToRootBounds = true;
        menu.Opened += (_, _) =>
        {
            isOpen = true;
            DropdownPlacement.NotifySearchScopeOpened(this);
        };
        menu.Closed += (_, _) =>
        {
            isOpen = false;
            DropdownPlacement.NotifySearchScopeClosed(this);
        };
        Click += (_, _) => { if (IsDropDownOpen) Close(); else Open(); };
        IsEnabledChanged += (_, _) => { if (!IsEnabled) Close(); };
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => { if (Visibility != Visibility.Visible) Close(); });
        Unloaded += (_, _) => Close();
    }

    internal FrameworkElement? SearchField { get; set; }
    internal Control? SurfaceSource { get; set; }

    public IReadOnlyList<DesktopSearchTarget> ItemsSource
    {
        get => items;
        set
        {
            items = value;
            menu.Items.Clear();
            foreach (var target in items)
            {
                var item = new MenuFlyoutItem
                {
                    Text = target.Label,
                    Tag = target,
                    FontSize = 12,
                    FontWeight = Microsoft.UI.Text.FontWeights.Normal,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(6, 7, 6, 7),
                    Margin = new Thickness(4, 1, 4, 1),
                    CornerRadius = new CornerRadius(6)
                };
                if (Application.Current?.Resources.TryGetValue("MeowFilm.MenuItem", out var menuItemStyle) == true &&
                    menuItemStyle is Style itemStyle)
                    item.Style = itemStyle;
                item.Click += (_, _) => SelectedValue = target.Id;
                menu.Items.Add(item);
            }
            RefreshSelection();
        }
    }

    public string? SelectedValue { get => (string?)GetValue(SelectedValueProperty); set => SetValue(SelectedValueProperty, value); }
    public DesktopSearchTarget? SelectedItem => items.FirstOrDefault(item => item.Id == SelectedValue);
    public bool IsDropDownOpen => isOpen || menu.IsOpen;

    private void RefreshSelection()
    {
        Content = SelectedItem?.Label ?? "";
        var selectedBrush = Application.Current?.Resources.TryGetValue("MeowFilm.Brush.ItemSelected", out var sb) == true
            ? sb as Brush : null;
        var textBrush = Application.Current?.Resources.TryGetValue("MeowFilm.Brush.Text", out var tb) == true
            ? tb as Brush : null;
        foreach (var entry in menu.Items.OfType<MenuFlyoutItem>())
        {
            bool isSelected = entry.Tag is DesktopSearchTarget target &&
                string.Equals(target.Id, SelectedValue, StringComparison.Ordinal);
            if (isSelected && selectedBrush is not null)
                entry.Background = selectedBrush;
            else
                entry.ClearValue(Control.BackgroundProperty);
            if (textBrush is not null) entry.Foreground = textBrush;
        }
    }

    internal bool TrySelect(string? id, string? label, int? index)
    {
        DesktopSearchTarget? match = null;
        if (!string.IsNullOrWhiteSpace(id))
            match = items.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
        if (match is null && !string.IsNullOrWhiteSpace(label))
            match = items.FirstOrDefault(item => string.Equals(item.Label, label, StringComparison.OrdinalIgnoreCase));
        if (match is null && index is { } i && i >= 0 && i < items.Count)
            match = items[i];
        if (match is null) return false;
        SelectedValue = match.Id;
        Close();
        return true;
    }

    internal void Open()
    {
        if (!IsEnabled || !IsLoaded) return;
        RefreshSelection();
        double width = Math.Max(92, ActualWidth);
        var presenterStyle = new Style(typeof(MenuFlyoutPresenter))
        {
            Setters =
            {
                new Setter(FrameworkElement.RequestedThemeProperty, ElementTheme.Light),
                new Setter(MinWidthProperty, width),
                new Setter(WidthProperty, width),
                new Setter(MaxWidthProperty, width),
                new Setter(PaddingProperty, new Thickness(0, 4, 0, 4)),
                new Setter(Control.CornerRadiusProperty, new CornerRadius(10)),
                new Setter(Control.BorderThicknessProperty, new Thickness(1))
            }
        };
        if (Application.Current?.Resources.TryGetValue("MeowFilm.ContextMenu", out var baseStyle) == true &&
            baseStyle is Style contextMenuStyle)
            presenterStyle.BasedOn = contextMenuStyle;
        var background = SurfaceSource?.Background ?? (SearchField as Border)?.Background;
        var borderBrush = SurfaceSource?.BorderBrush ?? (SearchField as Border)?.BorderBrush;
        if (background is not null)
        {
            presenterStyle.Setters.Add(new Setter(Control.BackgroundProperty, background));
            if (borderBrush is not null)
                presenterStyle.Setters.Add(new Setter(Control.BorderBrushProperty, borderBrush));
        }
        else if (Application.Current?.Resources.TryGetValue("MeowFilm.Brush.FloatingSurface", out var bg) == true)
        {
            presenterStyle.Setters.Add(new Setter(Control.BackgroundProperty, bg));
            if (Application.Current.Resources.TryGetValue("MeowFilm.Brush.Border", out var bd))
                presenterStyle.Setters.Add(new Setter(Control.BorderBrushProperty, bd));
        }
        foreach (var entry in menu.Items.OfType<MenuFlyoutItem>())
            entry.HorizontalAlignment = HorizontalAlignment.Stretch;
        menu.MenuFlyoutPresenterStyle = presenterStyle;
        menu.ShowAt(SearchField ?? this, new FlyoutShowOptions { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft });
    }

    internal void Close()
    {
        if (menu.IsOpen) menu.Hide();
        if (isOpen)
        {
            isOpen = false;
            DropdownPlacement.NotifySearchScopeClosed(this);
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new ScopePeer(this);

    private sealed class ScopePeer(ShellSearchScope owner) : ButtonAutomationPeer(owner), IExpandCollapseProvider
    {
        protected override object GetPatternCore(PatternInterface patternInterface) => patternInterface == PatternInterface.ExpandCollapse
            ? this : base.GetPatternCore(patternInterface);
        ExpandCollapseState IExpandCollapseProvider.ExpandCollapseState => owner.IsDropDownOpen ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;
        void IExpandCollapseProvider.Expand() => owner.Open();
        void IExpandCollapseProvider.Collapse() => owner.Close();
    }
}
