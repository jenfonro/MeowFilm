using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MeowFilm.Capabilities;

namespace MeowFilm.Windows.Tests;

public static class CardMotionTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("hover interpolates reverses and stays local to its card", Hover);
        yield return Case("disable hide rebind and unload retire the previous clock", Lifecycle);
        yield return Case("focus stays active between descendants and retires after leaving", Focus);
    }

    private static TestCase Case(string name, Func<Task> body) =>
        new("card-motion: " + name, () => TestUiHost.InvokeAsync(body));

    private static async Task Hover()
    {
        var card = new Button { IsHitTestVisible = false };
        using var scene = new Scene(card);
        await scene.Load();
        var other = new Border(); CardMotion.SetEnabled(other, true);
        scene.Root.Children.Add(other); await TestUiHost.LoadedAsync(other);
        CardMotion.SetPointerActive(card, true);
        if (MusicVisuals.CanAnimate(card))
        {
            Check(CardMotion.HasActiveAnimation(card), "hover starts a native presentation clock");
            await Until(() => CardMotion.GetProgress(card) is > 0 and < .95, "hover publishes a real intermediate frame");
            double partial = CardMotion.GetProgress(card);
            CardMotion.SetPointerActive(card, false);
            Check(Math.Abs(CardMotion.GetProgress(card) - partial) < .001,
                "reversal preserves the current presentation value before the next native tick");
            await Until(() => CardMotion.GetProgress(card) > 0 && CardMotion.GetProgress(card) < partial,
                "the replacement clock moves back from the captured frame");
            CardMotion.SetPointerActive(card, true);
        }
        await Settled(card, 1);
        Check(CardMotion.GetProgress(other) == 0 && !CardMotion.HasActiveAnimation(other),
            "a second card has its own progress and clock");
        CardMotion.SetPointerActive(card, false); await Settled(card, 0);
        CardMotion.SetProgress(card, .4);
        Check(CardMotion.GetProgress(card) == .4, "the attached progress setter remains the immediate presentation value");
        CardMotion.SetProgress(card, 0);
    }

    private static async Task Lifecycle()
    {
        var card = new Button { IsHitTestVisible = false };
        using var scene = new Scene(card);
        await scene.Load();
        await Start();
        card.IsEnabled = false; Rest("disabled controls stop immediately");
        card.IsEnabled = true; await Settled(card, 1);
        CardMotion.SetEnabled(card, false); Rest("the attached switch stops immediately");
        CardMotion.SetEnabled(card, true); await Settled(card, 1);

        card.Visibility = Visibility.Collapsed;
        Check(card.IsLoaded, "hiding preserves the loaded element");
        Rest("hiding stops motion without waiting for unload");
        card.Visibility = Visibility.Visible; scene.Root.UpdateLayout(); await DesktopTestUi.DrainAsync();
        Rest("a visible return does not retain hidden pointer intent");
        await Start();
        card.DataContext = new object();
        await Settled(card, 1);
        Check(CardMotion.IsPointerActive(card), "rebinding retains current pointer intent while replacing the clock");
        CardMotion.SetPointerActive(card, false); await Settled(card, 0);
        await Start();
        scene.Root.Children.Remove(card); await DesktopTestUi.DrainAsync();
        Check(!card.IsLoaded, "the card actually left the native tree");
        Rest("unloading retires motion and pointer intent");
        scene.Root.Children.Add(card); await TestUiHost.LoadedAsync(card); await DesktopTestUi.DrainAsync();
        Rest("the same remounted element starts at rest");
        CardMotion.SetPointerActive(card, true); await Settled(card, 1);
        CardMotion.SetPointerActive(card, false); await Settled(card, 0);

        async Task Start()
        {
            CardMotion.SetPointerActive(card, true);
            if (MusicVisuals.CanAnimate(card))
                await Until(() => CardMotion.GetProgress(card) is > 0 and < .95,
                    "lifecycle change interrupts a running native clock");
            else await Settled(card, 1);
        }
        void Rest(string message) => Check(CardMotion.GetProgress(card) == 0 && !CardMotion.HasActiveAnimation(card), message);
    }

    private static async Task Focus()
    {
        var first = new Button { Content = "first" };
        var second = new Button { Content = "second" };
        var card = new StackPanel { IsHitTestVisible = false };
        card.Children.Add(first); card.Children.Add(second);
        using var scene = new Scene(card);
        await scene.Load();
        Check(first.Focus(FocusState.Programmatic), "the first action receives real native focus");
        await Settled(card, 1);
        Check(second.Focus(FocusState.Programmatic), "focus moves to another descendant");
        await DesktopTestUi.DrainAsync(); await Settled(card, 1);
        Check(scene.Root.Focus(FocusState.Programmatic), "focus leaves the card");
        await Settled(card, 0);
    }

    private static Task Settled(FrameworkElement card, double value) => Until(
        () => !CardMotion.HasActiveAnimation(card) && Math.Abs(CardMotion.GetProgress(card) - value) < .001,
        $"the native card clock completes at {value}");
    private static async Task Until(Func<bool> done, string message)
    {
        var elapsed = Stopwatch.StartNew();
        while (!done())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException(message);
            await DesktopTestUi.DrainAsync();
            if (!done()) await Task.Delay(1);
        }
        await DesktopTestUi.DrainAsync();
    }
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }

    private sealed class Scene : IDisposable
    {
        internal readonly Grid Root = new() { IsTabStop = true };
        private readonly DesktopTestUi.Host host;
        internal Scene(FrameworkElement card)
        {
            CardMotion.SetEnabled(card, true);
            Root.Children.Add(card); host = new(Root, 360, 260);
        }
        internal async Task Load()
        {
            await TestUiHost.LoadedAsync(Root);
            Check(Root.Focus(FocusState.Programmatic), "the fixture starts with focus outside the card");
            await DesktopTestUi.DrainAsync(); Root.UpdateLayout();
        }
        public void Dispose() => host.Dispose();
    }
}
