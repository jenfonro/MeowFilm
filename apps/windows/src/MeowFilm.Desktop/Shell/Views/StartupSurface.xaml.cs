using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeowFilm.Desktop;

public sealed partial class StartupSurface : Grid
{
    public event RoutedEventHandler? ActionRequested;

    public StartupSurface() => InitializeComponent();

    public void ShowStarting()
    {
        StatusLabel.Text = "正在启动";
        ActionButton.Visibility = Visibility.Collapsed;
    }

    public void ShowRetryableFailure()
    {
        StatusLabel.Text = "启动失败";
        ActionButton.Content = "重试";
        ActionButton.Visibility = Visibility.Visible;
    }

    public void ShowFatalFailure()
    {
        StatusLabel.Text = "启动失败，请重新打开应用";
        ActionButton.Content = "退出";
        ActionButton.Visibility = Visibility.Visible;
    }

    private void ActionButton_Click(object sender, RoutedEventArgs e) => ActionRequested?.Invoke(this, e);
}
