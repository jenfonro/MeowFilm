using Microsoft.UI.Xaml;

namespace MeowFilm.Windows.Tests;

public sealed partial class TestApplication : Application
{
    private readonly Func<Task<int>>? body;
    private readonly Action<int>? complete;
    private readonly Action<Application>? launch;

    internal TestApplication(Func<Task<int>> body, Action<int> complete)
    {
        this.body = body;
        this.complete = complete;
        InitializeComponent();
    }

    internal TestApplication(Action<Application> launch)
    {
        this.launch = launch;
        InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (launch is not null) { launch(this); return; }
        MeowFilm.Desktop.DesktopTypography.Install(this);
        Window? lifetime = null;
        try
        {
            // Keep the application alive between case-owned windows. This
            // unshown root is not evidence for desktop rendering.
            lifetime = new Window();
            complete!(await body!());
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            complete!(1);
        }
        finally { lifetime?.Close(); Exit(); }
    }
}
