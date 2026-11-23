using Microsoft.UI.Xaml.Controls;

namespace MeowFilm.Desktop;

public sealed partial class ShellNavigationGroupHeader : UserControl
{
    public ShellNavigationGroup? Group => DataContext as ShellNavigationGroup;

    public ShellNavigationGroupHeader()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Bindings?.Update();
    }
}
