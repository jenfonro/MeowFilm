using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MeowFilm.Desktop;

namespace MeowFilm.Windows.Tests;

public static partial class DesktopTests
{
    private static IEnumerable<TestCase> ContextMenuCases()
    {
        foreach (bool ownedDialog in new[] { false, true })
        yield return Case(ownedDialog
            ? "owned window automatic text editor menu keeps WPF actions targets and disabled states with shared chrome"
            : "automatic text editor menu keeps WPF actions targets and disabled states with shared chrome", async () =>
        {
            await using var f = new Fixture([new ProbeModule("editor")]);
            f.Shell.Left = -32000; f.Shell.Top = -32000; f.Shell.ShowActivated = false; f.Shell.ShowInTaskbar = false;
            f.Shell.Show(); f.Shell.UpdateLayout(); await DrainAsync();
            var editor = ownedDialog ? new TextBox() : f.Search;
            var dialog = ownedDialog ? new Window { Owner = f.Shell, Content = editor,
                Width = 400, Height = 120, Left = -32000, Top = -32000,
                ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None } : null;
            editor.Text = "fixture text"; editor.SelectAll();
            ContextMenu? menu = null;
            try
            {
                if (dialog is not null)
                {
                    dialog.Resources.MergedDictionaries.Add(new ResourceDictionary
                    { Source = new Uri("/MeowFilm.Capabilities;component/Presentation/CapabilityTheme.xaml", UriKind.Relative) });
                    dialog.Show(); dialog.UpdateLayout(); await DrainAsync();
                    True(editor.IsLoaded && ReferenceEquals(Window.GetWindow(editor), dialog),
                        "the real editor belongs to the ordinary owned window, not directly to Shell");
                }
                var request = (ContextMenuEventArgs)typeof(ContextMenuEventArgs)
                    .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
                        [typeof(object), typeof(bool), typeof(double), typeof(double)], null)!
                    .Invoke([editor, true, -1d, -1d]);
                editor.RaiseEvent(request); await DrainAsync();
                menu = PresentationSource.CurrentSources.Cast<PresentationSource>()
                    .Where(source => source.RootVisual is not null)
                    .SelectMany(source => ChromeVisuals(source.RootVisual))
                    .OfType<ContextMenu>().Single(value => value.IsOpen && ReferenceEquals(value.PlacementTarget, editor));
                True(editor.ContextMenu is null && request.Handled, "WPF generated its own temporary menu");
                True(ReferenceEquals(menu.Style, f.Shell.FindResource("MeowFilm.ContextMenu")),
                    "actual private editor menu receives common surface");
                True(ChromeVisuals(menu).OfType<Border>().Any(border =>
                    ReferenceEquals(border.Style, f.Shell.FindResource("MeowFilm.FloatingSurface"))),
                    "the actual automatic menu renders the shared surface template");
                var actions = menu.Items.OfType<MenuItem>().ToArray();
                foreach (var command in new[] { ApplicationCommands.Cut, ApplicationCommands.Copy, ApplicationCommands.Paste })
                {
                    var item = actions.Single(value => ReferenceEquals(value.Command, command));
                    True(ReferenceEquals(item.CommandTarget, editor), command.Name + " retains the real editor command target");
                    True(ReferenceEquals(item.Style, f.Shell.FindResource("MeowFilm.MenuItem")) &&
                        item.Template.FindName("Header", item) is ContentPresenter,
                        command.Name + " uses the actual shared template");
                }
                var copy = actions.Single(value => ReferenceEquals(value.Command, ApplicationCommands.Copy));
                var cut = actions.Single(value => ReferenceEquals(value.Command, ApplicationCommands.Cut));
                CommandManager.InvalidateRequerySuggested(); await DrainAsync();
                True(copy.IsEnabled && cut.IsEnabled, "selection enables original editing commands");
                editor.IsReadOnly = true; CommandManager.InvalidateRequerySuggested(); await DrainAsync();
                True(copy.IsEnabled && !cut.IsEnabled, "read-only editor preserves copy and disables cut");
                menu.IsOpen = false; editor.IsReadOnly = false;
                ApplicationCommands.SelectAll.Execute(null, editor);
                Equal(editor.Text.Length, editor.SelectionLength, "select all still targets the original editor");
                editor.Select(editor.Text.Length, 0); editor.SelectedText = " edit";
                True(ApplicationCommands.Undo.CanExecute(null, editor), "undo remains available through WPF command routing");
                ApplicationCommands.Undo.Execute(null, editor);
                Equal("fixture text", editor.Text, "undo changes the editor rather than menu state");
            }
            finally
            {
                if (menu is not null) menu.IsOpen = false;
                if (dialog is not null) { dialog.Content = null; dialog.Close(); }
            }
        });
        yield return Case("surface gesture requires matching blank down and up and consumes dismissal", () =>
        {
            var gesture = new SurfaceGesture();
            True(!gesture.Up(true), "an orphan up never opens details");
            foreach (string layer in new[] { "quality", "spatial", "playlist", "volume", "more", "rate", "source", "queue" })
            {
                gesture.Down(true, true); gesture.Arm(true);
                True(!gesture.Up(true), layer + " closes without granting the same gesture");
                gesture.Down(true, false); gesture.Arm(true);
                True(gesture.Up(true), layer + " does not suppress a later gesture");
            }
            gesture.Down(true, false); gesture.Arm(false); True(!gesture.Up(true), "control press cannot become blank release");
            gesture.Down(true, false); gesture.Arm(true); True(!gesture.Up(false), "blank press ending on control is rejected");
            gesture.Down(true, false); gesture.Arm(true); gesture.Cancel(); True(!gesture.Up(true), "deactivation or unload cancels");
            gesture.Down(true, false); gesture.Arm(true); gesture.Block(); True(!gesture.Up(true), "closing a layer cannot rearm");
            return Task.CompletedTask;
        });
        yield return Case("synthetic native messages preserve mouse activation through WPF activation", () =>
        {
            // Calls the very adapter used by the HWND hook, not physical input or desktop activation.
            var gesture = new SurfaceGesture();
            gesture.Message(0x21, 0, (nint)(0x201 << 16), false, false); // WM_MOUSEACTIVATE while inactive
            gesture.Message(0x6, 1, 0, true, false); // WM_ACTIVATE before WPF down
            gesture.Message(0x201, 1, 0, true, false); gesture.Arm(true);
            True(!gesture.CanDismiss, "activation click cannot dismiss an open queue either");
            True(!gesture.Up(true), "activation click cannot open details even though down sees active");
            gesture.Message(0x201, 1, 0, true, false); gesture.Arm(true);
            True(gesture.Up(true), "next click works");
            foreach (int trigger in new[] { 0x204, 0x207 })
            {
                gesture.Message(0x21, 0, (nint)(trigger << 16), false, false);
                gesture.Message(0x6, 1, 0, true, false);
                gesture.Message(0x201, 1, 0, true, false); gesture.Arm(true);
                True(gesture.Up(true), "right/middle activation does not consume a later left gesture");
            }
            gesture.Message(0x21, 0, (nint)(0x201 << 16), false, false);
            gesture.Message(0x100, 0, 0, true, false); // keyboard recovery, no stale mouse activation
            gesture.Down(true, false); gesture.Arm(true); True(gesture.Up(true), "keyboard recovery permits a normal click");
            gesture.Down(true, false); gesture.Arm(true); gesture.Message(0x6, 0, 0, false, false);
            True(!gesture.Up(true), "mid-gesture deactivation cancels");
            gesture.Message(0x201, 1, 0, true, true); gesture.Arm(true);
            True(!gesture.Up(true), "popup-captured native down preserves pre-close suppression");
            gesture.Down(true, false); gesture.Arm(true); gesture.Message(0x1f, 0, 0, true, false);
            True(!gesture.Up(true), "WM_CANCELMODE cancels admission");
            return Task.CompletedTask;
        });
        yield return Case("shared menu template preserves content checks commands and dynamic subitems", async () =>
        {
            var menu = new ContextMenu();
            var resources = new ResourceDictionary { Source = new Uri("/MeowFilm.Desktop;component/Theme.xaml", UriKind.Relative) };
            menu.Resources = resources; menu.Style = (Style)resources["MeowFilm.ContextMenu"];
            var editor = new TextBox { Text = "unchanged editor" };
            var window = new Window { Content = editor, Width = 320, Height = 100,
                Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false,
                WindowStyle = WindowStyle.None };
            try
            {
                window.Show(); window.UpdateLayout(); await DrainAsync();
                True(editor.IsLoaded && ApplicationCommands.SelectAll.CanExecute(null, editor),
                    "the loaded editor is an executable target for the original SelectAll command");
                var content = new TextBlock { Text = "Rich header" };
                var item = new MenuItem { Header = content, IsCheckable = true, IsChecked = true,
                    Command = ApplicationCommands.SelectAll, CommandTarget = editor, InputGestureText = "Ctrl+A" };
                var parent = new MenuItem { Header = "Dynamic" };
                menu.Items.Add(item); menu.Items.Add(parent);
                Layout(menu);
                var later = new MenuItem { Header = "Later" };
                parent.Items.Add(later); later.ApplyTemplate();
                True(item.Template == ((ControlTemplate)((Style)resources["MeowFilm.MenuItem"]).Setters.OfType<Setter>()
                    .Single(setter => setter.Property == Control.TemplateProperty).Value), "dynamic root uses the shared template");
                True(later.Template == item.Template, "a dynamically added submenu item inherits the same template");
                True(ReferenceEquals(((ContentPresenter)item.Template.FindName("Header", item)).Content, content), "header content is not flattened");
                Equal(Visibility.Visible, ((TextBlock)item.Template.FindName("Check", item)).Visibility, "ordinary checks remain visible");
                True(ReferenceEquals(item.CommandTarget, editor) && ReferenceEquals(item.Command, ApplicationCommands.SelectAll), "style leaves command routing intact");
                True(editor.ContextMenu is null, "the automatic editor menu is not replaced with a partial action list");
                CommandManager.InvalidateRequerySuggested(); await DrainAsync();
                True(item.IsEnabled, "the command item is enabled before hover and high contrast assertions");
                var key = (DependencyPropertyKey)typeof(MenuItem).GetField("IsHighlightedPropertyKey", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
                item.SetValue(key, true);
                True(ReferenceEquals(item.Background, resources["MeowFilm.Brush.ItemHover"]), "keyboard highlight uses visible hover fill");
                DesktopTheme.SetIsHighContrast(menu, true);
                Equal(SystemColors.HighlightTextBrush, item.Foreground, "high contrast highlight text");
                item.IsEnabled = false;
                Equal(SystemColors.GrayTextBrush, item.Foreground, "high contrast disabled text");
            }
            finally { menu.IsOpen = false; window.Content = null; window.Close(); await DrainAsync(); }
        });
        yield return Case("theme palette reaches repeated local dictionaries and detached menu consumers", async () =>
        {
            var popupPanel = new Border();
            var first = new ResourceDictionary { Source = new Uri("/MeowFilm.Desktop;component/Theme.xaml", UriKind.Relative) };
            var second = new ResourceDictionary { Source = new Uri("/MeowFilm.Desktop;component/Theme.xaml", UriKind.Relative) };
            popupPanel.Resources = second; popupPanel.Style = (Style)second["MeowFilm.FloatingSurface"];
            var appearance = new MenuAppearance();
            var shell = new ShellWindow([new ProbeModule("theme")], [], () => Task.CompletedTask,
                () => Task.CompletedTask, appearance: appearance);
            var search = (TextBox)shell.FindName("SearchBox");
            var menu = new ContextMenu { Resources = second, Style = (Style)second["MeowFilm.ContextMenu"], PlacementTarget = search,
                Placement = PlacementMode.AbsolutePoint, HorizontalOffset = -32000, VerticalOffset = -32000 };
            menu.Items.Add(new MenuItem { Header = "Theme consumer" });
            Layout(menu); Layout(popupPanel);
            var before = ((SolidColorBrush)popupPanel.Background).Color;
            try
            {
                menu.IsOpen = true;
                await DrainAsync();
                Layout(menu);
                await appearance.SaveAsync("#FF2800");
                await DrainAsync();
                var background = (Border)shell.FindName("ApplicationBackground");
                Same(shell.Background, background.Background, "the background visual follows the saved theme brush");
                Same(background, ((SidebarBackdrop)shell.FindName("SidebarMaterial")).Source,
                    "sidebar theme changes use the application background visual");
                True(((LinearGradientBrush)background.Background).GradientStops.Select(stop => stop.Color)
                    .SequenceEqual(((LinearGradientBrush)AppearanceColors.Background("#FF2800"))
                        .GradientStops.Select(stop => stop.Color)), "the saved custom color drives every background stop");
                foreach (string token in new[] { "FloatingSurface", "ItemHover", "ItemSelected", "InputSurface" })
                {
                    var brush = (SolidColorBrush)first["MeowFilm.Brush." + token];
                    True(ReferenceEquals(brush, second["MeowFilm.Brush." + token]) && !brush.IsFrozen,
                        token + " is shared across view dictionaries and remains updateable");
                }
                True(((SolidColorBrush)popupPanel.Background).Color != before, "detached panel changes without a window ancestor lookup");
                var menuSurface = ChromeVisuals(menu).OfType<Border>().Single(border =>
                    ReferenceEquals(border.Style, menu.FindResource("MeowFilm.FloatingSurface")));
                Equal(AppearanceColors.FloatingSurface.Color, ((SolidColorBrush)menuSurface.Background).Color,
                    "opened actual menu template updates after sealing");
                Equal(AppearanceColors.InputSurface.Color, ((SolidColorBrush)search.Background).Color, "actual search background follows the palette");
                True(AppearanceColors.ItemHover.Color != AppearanceColors.ItemSelected.Color, "hover and current remain distinct");
                DesktopTheme.SetIsHighContrast(popupPanel, true);
                Equal(SystemColors.WindowBrush, popupPanel.Background, "system background replaces tint in high contrast");
                True(popupPanel.Effect is null, "high contrast has no shadow");
            }
            finally { menu.IsOpen = false; shell.Close(); AppearanceColors.ApplyFloatingPalette(AppearanceColors.DefaultColor); }
        });
        yield return Case("queue shell clips all four content corners without clipping its shadow", async () =>
        {
            var module = new ProbeModule("music");
            await using var f = new Fixture([module]); await f.LoadAsync();
            var surface = f.Element<Border>("QueueSurface");
            var host = f.Element<ContentControl>("QueueHost");
            surface.Visibility = Visibility.Visible; host.Content = new Border { Background = Brushes.Black };
            surface.Measure(new(386, 400)); surface.Arrange(new Rect(0, 0, 386, 400)); surface.UpdateLayout();
            Equal(new CornerRadius(12), surface.CornerRadius, "four rounded corners");
            True(!surface.ClipToBounds && surface.Clip is null && surface.Effect is not null, "outer shadow retains its overflow");
            var clip = (RectangleGeometry)host.Clip;
            True(clip.RadiusX == 11 && clip.RadiusY == 11 && !clip.FillContains(new Point(0, 0)), "content cannot paint a black corner");
            True(surface.Margin.Right >= 8, "right shadow has space inside the window clip");
            DesktopTheme.SetIsHighContrast(surface, true);
            True(surface.Effect is null && ReferenceEquals(surface.Background, SystemColors.WindowBrush), "queue respects high contrast");
            DesktopTheme.SetIsHighContrast(surface, false);
            ((Panel)surface.Parent).Children.Remove(surface);
            surface.Margin = new Thickness(24); surface.Height = 320;
            surface.HorizontalAlignment = HorizontalAlignment.Center;
            surface.VerticalAlignment = VerticalAlignment.Center;
            var canvas = new Grid(); canvas.Children.Add(surface);
            var background = new Border { Child = canvas };
            foreach (var (name, color) in new[] { ("light", Colors.White), ("dark", Color.FromRgb(64, 64, 64)) })
            {
                background.Background = new SolidColorBrush(color);
                background.Measure(new(460, 380)); background.Arrange(new Rect(0, 0, 460, 380)); background.UpdateLayout();
                var origin = surface.TranslatePoint(new Point(), background);
                foreach (int dpi in new[] { 96, 144, 192 })
                {
                    int width = 460 * dpi / 96, height = 380 * dpi / 96;
                    var bitmap = new RenderTargetBitmap(width, height, dpi, dpi, PixelFormats.Pbgra32);
                    bitmap.Render(background);
                    var pixels = new byte[width * height * 4]; bitmap.CopyPixels(pixels, width * 4, 0);
                    Color Pixel(double x, double y)
                    {
                        int offset = ((int)(y * dpi / 96) * width + (int)(x * dpi / 96)) * 4;
                        return Color.FromArgb(pixels[offset + 3], pixels[offset + 2], pixels[offset + 1], pixels[offset]);
                    }
                    var center = Pixel(origin.X + surface.ActualWidth / 2, origin.Y + surface.ActualHeight / 2);
                    True(center.A == 255 && center.R == 0 && center.G == 0 && center.B == 0,
                        name + "/" + dpi + " fixture has opaque black content, making corner checks meaningful");
                    foreach (double x in new[] { origin.X + 1, origin.X + surface.ActualWidth - 2 })
                    foreach (double y in new[] { origin.Y + 1, origin.Y + surface.ActualHeight - 2 })
                    {
                        var corner = Pixel(x, y);
                        True(corner.A == 255 && corner.R + corner.G + corner.B > 30,
                            name + "/" + dpi + " actual corner must not expose the black rectangular child");
                    }
                    double middle = origin.Y + surface.ActualHeight / 2;
                    var near = Pixel(origin.X - 2, middle);
                    var far = Pixel(origin.X - 12, middle);
                    True(near.R <= far.R && far.R <= color.R && far.R - near.R >= 1,
                        name + "/" + dpi + " shadow fades outward instead of ending as an opaque rectangle");
                }
                GeneralAppearanceTests.SavePreview(background, "context-queue-corners-" + name, 460, 380);
            }
        });
    }
    private static void Layout(FrameworkElement element)
    { element.Measure(new Size(600, 600)); element.Arrange(new Rect(element.DesiredSize)); element.UpdateLayout(); }

    private sealed class MenuAppearance : IDesktopAppearance
    {
        public string ThemeColor { get; private set; } = AppearanceColors.DefaultColor;
        public bool CanEdit => true;
        public string Status => "";
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        public Task LoadAsync() => Task.CompletedTask;
        public Task SaveAsync(string color)
        {
            ThemeColor = color;
            PropertyChanged?.Invoke(this, new(nameof(ThemeColor)));
            return Task.CompletedTask;
        }
    }
}
