using System.IO;
using MeowFilm.Desktop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace MeowFilm.Capabilities;

public sealed partial class NovelWebInteractionView : UserControl
{
    private readonly Func<CancellationToken, Task<bool>> onSubmit;
    private readonly CancellationTokenSource pending;
    private readonly bool autoSubmit;
    private Task? activeSubmit;
    private bool busy, accepted, closed;
    private Exception? autoFailure;

    internal WebView2 WebView => Web;
    internal string CodeText => CodeBox.Text;

    internal NovelWebInteractionView(
        string title,
        string submitLabel,
        bool showWeb,
        bool showCode,
        bool autoSubmit,
        Func<CancellationToken, Task<bool>> onSubmit,
        CancellationToken token)
    {
        this.onSubmit = onSubmit;
        this.autoSubmit = autoSubmit;
        pending = CancellationTokenSource.CreateLinkedTokenSource(token);
        InitializeComponent();

        TitleText.Text = title;
        AutomationProperties.SetName(this, title);
        SubmitButton.Content = submitLabel;
        AutomationProperties.SetName(SubmitButton, submitLabel);
        Web.Visibility = showWeb ? Visibility.Visible : Visibility.Collapsed;
        CodeSection.Visibility = showCode ? Visibility.Visible : Visibility.Collapsed;
        if (autoSubmit) SubmitButton.Visibility = Visibility.Collapsed;
    }

    internal async Task<bool> PresentAsync(FrameworkElement owner, CancellationToken token)
    {
        try
        {
            await DesktopModalHost.ShowSurfaceAsync(
                owner,
                this,
                dismissOnOutside: false,
                onOpened: OnOpened,
                cancellationToken: pending.Token);
        }
        finally
        {
            closed = true;
            pending.Cancel();
            if (activeSubmit is { } running)
            {
                try { await running; } catch { }
            }
            pending.Dispose();
        }
        if (autoFailure is not null) throw autoFailure;
        return accepted && !token.IsCancellationRequested;
    }

    private void OnOpened()
    {
        if (CodeSection.Visibility == Visibility.Visible) CodeBox.Focus(FocusState.Programmatic);
        else CancelButton.Focus(FocusState.Programmatic);
        if (autoSubmit) activeSubmit = ExecuteSubmitAsync();
    }

    private async Task ExecuteSubmitAsync()
    {
        if (busy || closed) return;
        busy = true;
        SubmitButton.IsEnabled = false;
        try
        {
            if (await onSubmit(pending.Token))
            {
                accepted = true;
                DesktopModalHost.CloseFor(this, this);
            }
        }
        catch (OperationCanceledException)
        {
            DesktopModalHost.CloseFor(this, this);
        }
        catch (Exception error)
        {
            if (autoSubmit && !closed && !pending.IsCancellationRequested)
            {
                autoFailure = error;
                DesktopModalHost.CloseFor(this, this);
            }
            else if (closed || pending.IsCancellationRequested)
            {
                DesktopModalHost.CloseFor(this, this);
            }
            else
            {
                Notices.Show(this, error is InvalidDataException ? error.Message : "操作失败，请检查输入或重试");
            }
        }
        finally
        {
            busy = false;
            SubmitButton.IsEnabled = true;
        }
    }

    private async void Submit_Click(object sender, RoutedEventArgs e)
    {
        if (busy || closed) return;
        var task = ExecuteSubmitAsync();
        activeSubmit = task;
        await task;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (closed) return;
        pending.Cancel();
        DesktopModalHost.CloseFor(this, this);
    }
}
