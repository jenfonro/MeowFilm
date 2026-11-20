using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace MeowFilm.Desktop;

// A scope action opens one native menu below the search field; it is not an editable value field.
public sealed class ShellSearchScope : Button
{
    public static readonly DependencyProperty SelectedValueProperty = DependencyProperty.Register(nameof(SelectedValue),
        typeof(string), typeof(ShellSearchScope), new PropertyMetadata(null, (owner, _) => ((ShellSearchScope)owner).RefreshSelection()));
    private readonly MenuFlyout menu = new();
    private IReadOnlyList<DesktopSearchTarget> items = [];
    public ShellSearchScope()
    {
        Click += (_, _) => { if (menu.IsOpen) menu.Hide(); else Open(); };
        Unloaded += (_, _) => menu.Hide();
    }
    internal FrameworkElement? SearchField { get; set; }
    public IReadOnlyList<DesktopSearchTarget> ItemsSource
    {
        get => items;
        set
        {
            items = value; menu.Items.Clear();
            foreach (var target in items)
            {
                var item = new MenuFlyoutItem { Text = target.Label, Tag = target, FontSize = 12,
                    FontWeight = Microsoft.UI.Text.FontWeights.Normal };
                item.Click += (_, _) => SelectedValue = target.Id;
                menu.Items.Add(item);
            }
            RefreshSelection();
        }
    }
    public string? SelectedValue { get => (string?)GetValue(SelectedValueProperty); set => SetValue(SelectedValueProperty, value); }
    public DesktopSearchTarget? SelectedItem => items.FirstOrDefault(item => item.Id == SelectedValue);
    public bool IsDropDownOpen => menu.IsOpen;
    private void RefreshSelection() => Content = SelectedItem?.Label ?? "";
    internal void Open()
    {
        if (!IsEnabled || !IsLoaded) return;
        menu.MenuFlyoutPresenterStyle = new Style(typeof(MenuFlyoutPresenter))
        {
            Setters = { new Setter(MinWidthProperty, 0d), new Setter(WidthProperty, ActualWidth),
                new Setter(MaxWidthProperty, ActualWidth), new Setter(PaddingProperty, new Thickness(0)) }
        };
        menu.ShowAt(SearchField ?? this, new FlyoutShowOptions { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft });
    }
    internal void Close() => menu.Hide();
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
