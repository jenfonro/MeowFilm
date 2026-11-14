using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MeowFilm.Capabilities;
using MeowFilm.Desktop;

namespace MeowFilm.Windows.Tests;

public static partial class NovelTests
{
    private static async Task DialogHostIsolation()
    {
        foreach (string outcome in new[] { "success", "cancelled", "failure" })
        {
            var root = new Grid();
            var owner = new StackPanel { Focusable = true };
            var dialogHost = new PlayerDialogHost();
            root.Children.Add(owner);
            root.Children.Add(dialogHost);
            var window = GeneralAppearanceTests.Window(root);
            NovelUi.Theme(window);
            dialogHost.Attach(window);
            window.Show();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<bool>? retired = null;
            try
            {
                retired = NovelUi.DialogAsync(owner, "先前编辑", new TextBox(), "保存", async _ =>
                {
                    started.TrySetResult();
                    await release.Task;
                    if (outcome == "failure") throw new System.IO.IOException("held write failed");
                    if (outcome == "cancelled") throw new OperationCanceledException();
                    return true;
                }, applicationOverlay: true);
                await Until(() => dialogHost.IsOpen);
                Find<Button>(dialogHost).Single(button => Equals(button.Content, "保存"))
                    .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Find<Button>(dialogHost).Single(button => Equals(button.Content, "取消"))
                    .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check(!dialogHost.IsOpen && !retired.IsCompleted,
                    "Closing the application editor did not wait for the outstanding write.");

                var input = new TextBox { Text = "后续输入" };
                var current = NovelUi.DialogAsync(owner, "后续编辑", input, "保存",
                    _ => Task.FromResult(true), applicationOverlay: true);
                await Until(() => dialogHost.IsOpen && ReferenceEquals(Keyboard.FocusedElement, input));
                var surface = Find<Border>(dialogHost).Single(item =>
                    AutomationProperties.GetName(item) == "后续编辑");
                release.TrySetResult();
                if (outcome == "failure")
                {
                    try { await retired; throw new InvalidOperationException("The closed editor hid a write failure."); }
                    catch (System.IO.IOException) { }
                }
                else Check(!await retired, "A retired application editor reported a successful save.");
                Check(dialogHost.IsOpen && ReferenceEquals(surface.Parent, dialogHost) &&
                    !current.IsCompleted && ReferenceEquals(Keyboard.FocusedElement, input),
                    "A retired editor closed or stole focus from its successor.");
                Find<Button>(dialogHost).Single(button => Equals(button.Content, "保存"))
                    .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check(await current && !dialogHost.IsOpen, "The successor editor did not retain its save action.");
            }
            finally
            {
                release.TrySetResult();
                dialogHost.Close();
                window.Close();
                if (retired is not null)
                {
                    try { await retired.WaitAsync(TimeSpan.FromSeconds(5)); }
                    catch (System.IO.IOException) { }
                }
            }
        }
    }

    private static async Task DialogHostPresentation()
    {
        var root = new Grid();
        var owner = new StackPanel { Focusable = true };
        root.Children.Add(owner);
        var dialogHost = new PlayerDialogHost();
        root.Children.Add(dialogHost);
        var window = GeneralAppearanceTests.Window(root);
        NovelUi.Theme(window);
        dialogHost.Attach(window);
        window.Show();
        try
        {
            var input = NovelUi.Input("桌面输入", "保留输入", 420);
            var body = new StackPanel();
            body.Children.Add(NovelUi.Field("桌面输入", input));
            var form = new ManagementForm(true);
            form.Field("规则名称", "保留", id: "novel.rule.field.name");
            form.Field("匹配内容", "广告", id: "novel.rule.field.pattern");
            var formCard = form.Card();
            body.Children.Add(formCard);
            owner.Focus();
            var dialog = NovelUi.DialogAsync(owner, "桌面编辑", body, "保存", _ => Task.FromResult(true),
                applicationOverlay: true);
            await Until(() => dialogHost.IsOpen);
            await Dispatcher.Yield(DispatcherPriority.Render);
            await Until(() => ReferenceEquals(Keyboard.FocusedElement, input));

            var surface = Find<Border>(dialogHost).Single(item =>
                AutomationProperties.GetName(item) == "桌面编辑");
            Check(surface.Width <= 520 && surface.ActualWidth <= 520, "Novel dialog kept the mobile-sized width.");
            Check(surface.BorderThickness.Bottom == 1 && surface.BorderBrush is not null,
                "Novel dialog lost its themed border.");
            Check(surface.Background == surface.FindResource("MeowFilm.Brush.FloatingSurface"),
                "Novel dialog does not use the application floating surface.");
            Check(Find<Border>(body).Any(item => item.BorderThickness.Bottom == 1),
                "Novel input rows lost their desktop divider.");
            Check(Find<Border>(formCard).Count(item => Math.Abs(item.Height - .5) < .01) == 2,
                "Novel multi-field editor lost its row separators.");
            Check(formCard.Background == formCard.FindResource("MeowFilm.Brush.FloatingSurface") &&
                  formCard.BorderBrush == formCard.FindResource("MeowFilm.Brush.Border"),
                "Novel form card is not using the application theme surface and border.");
            Check(ReferenceEquals(Keyboard.FocusedElement, input),
                "Opening the Novel editor did not move focus into its first input.");

            var cancel = Find<Button>(dialogHost).Single(button => Equals(button.Content, "取消"));
            cancel.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(!await dialog, "Closing the application-hosted dialog was reported as a save.");
            Check(ReferenceEquals(Keyboard.FocusedElement, owner),
                "Closing the Novel editor did not return focus to its owner.");

            var saved = NovelUi.DialogAsync(owner, "应用内保存", new TextBlock { Text = "保存" }, "保存", _ => Task.FromResult(true),
                applicationOverlay: true);
            await Until(() => dialogHost.IsOpen);
            Find<Button>(dialogHost).Single(button => Equals(button.Content, "保存"))
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(await saved && !dialogHost.IsOpen, "Application-hosted dialog did not complete its save action.");

            window.Width = 360; window.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.Render);
            var narrowEditor = new NovelAppearanceEditor(new());
            var narrow = NovelUi.DialogAsync(owner, "窄窗编辑", narrowEditor, "保存",
                _ => Task.FromResult(true), applicationOverlay: true);
            await Until(() => dialogHost.IsOpen);
            await Dispatcher.Yield(DispatcherPriority.Render);
            var narrowSurface = Find<Border>(dialogHost).Single(item =>
                AutomationProperties.GetName(item) == "窄窗编辑");
            Check(narrowSurface.ActualWidth <= Math.Max(0, dialogHost.ActualWidth - 32) + 1,
                "The application dialog overflowed its narrow host.");
            foreach (var field in Find<TextBox>(narrowEditor))
            {
                var bounds = field.TransformToAncestor(narrowSurface)
                    .TransformBounds(new Rect(field.RenderSize));
                Check(bounds.Left >= 0 && bounds.Right <= narrowSurface.ActualWidth + 1,
                    "A reading appearance input overflowed its narrow dialog.");
            }
            var closeNarrow = Find<Button>(dialogHost).Single(button => Equals(button.Content, "取消"));
            closeNarrow.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(!await narrow && !dialogHost.IsOpen, "The narrow application dialog did not cancel cleanly.");
        }
        finally { dialogHost.Close(); window.Close(); }
    }
}
