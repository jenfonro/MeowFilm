using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

/// <summary>Observes the actual ancestor visibility, without coupling it to navigation or media lifetime.</summary>
internal sealed class FilmPresentationVisibility
{
    private readonly FrameworkElement view;
    private readonly Action<bool> changed;
    private readonly List<(UIElement Element, long Token)> ancestors = [];
    private XamlRoot? root;
    private bool? visible;

    internal FilmPresentationVisibility(FrameworkElement view, Action<bool> changed)
    {
        this.view = view; this.changed = changed;
        view.Loaded += (_, _) => Attach();
        view.Unloaded += (_, _) => { Detach(); Publish(false); };
    }
    private void Attach()
    {
        Detach();
        for (DependencyObject? node = view; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is UIElement element)
                ancestors.Add((element, element.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => Update())));
        root = view.XamlRoot;
        if (root is not null) root.Changed += RootChanged;
        Update();
    }
    private void Detach()
    {
        foreach (var (element, token) in ancestors) element.UnregisterPropertyChangedCallback(UIElement.VisibilityProperty, token);
        ancestors.Clear();
        if (root is not null) root.Changed -= RootChanged;
        root = null;
    }
    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => Update();
    private void Update() => Publish(DesktopVisual.IsVisible(view));
    private void Publish(bool value)
    {
        if (visible == value) return;
        visible = value; changed(value);
    }
}
