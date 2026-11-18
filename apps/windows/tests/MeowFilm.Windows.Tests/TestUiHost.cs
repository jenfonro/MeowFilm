using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace MeowFilm.Windows.Tests;

// WinUI owns one application/UI thread per process. Cases own their windows
// and resources, rather than starting unrelated WPF dispatchers.
internal static class TestUiHost
{
    private static DispatcherQueue? dispatcher;

    internal static int Run(Func<Task<int>> body)
    {
        int exitCode = 1;
        WinRT.ComWrappersSupport.InitializeComWrappers();
        try
        {
            Application.Start(args =>
            {
                dispatcher = DispatcherQueue.GetForCurrentThread();
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(dispatcher));
                var application = new TestApplication(body, value => exitCode = value);
                application.UnhandledException += (_, error) => Console.Error.WriteLine(error.Exception);
            });
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            exitCode = 1;
        }
        dispatcher = null;
        return exitCode;
    }

    internal static Task InvokeAsync(Func<Task> body)
    {
        var queue = dispatcher ?? throw new InvalidOperationException("TEST_WINUI_HOST_UNAVAILABLE");
        if (queue.HasThreadAccess) return body();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!queue.TryEnqueue(async () =>
        {
            try { await body(); completion.TrySetResult(); }
            catch (Exception error) { completion.TrySetException(error); }
        })) completion.TrySetException(new InvalidOperationException("TEST_WINUI_HOST_CLOSED"));
        return completion.Task;
    }

    internal static async Task LoadedAsync(FrameworkElement target)
    {
        if (!target.IsLoaded)
        {
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            RoutedEventHandler handler = (_, _) => loaded.TrySetResult();
            target.Loaded += handler;
            try { await loaded.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            finally { target.Loaded -= handler; }
        }
        target.UpdateLayout();
    }

}
