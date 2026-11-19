using Microsoft.UI.Xaml;

namespace MeowFilm.App;

public sealed partial class NativeApplication : Application
{
    private readonly Action<Application> launch;

    internal NativeApplication(Action<Application> launch)
    {
        this.launch = launch;
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args) => launch(this);
}
