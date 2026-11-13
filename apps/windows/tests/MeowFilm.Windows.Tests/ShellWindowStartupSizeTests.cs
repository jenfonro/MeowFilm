using MeowFilm.Desktop;

namespace MeowFilm.Windows.Tests;

public static class ShellWindowStartupSizeTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("Shell startup size preserves the confirmed physical-screen proportions through DPI conversion", () =>
        {
            var baseline = ShellWindowStartupSize.Calculate(2560, 1440, 96);
            Near(1300, baseline.Width);
            Near(850, baseline.Height);
            Near(860, baseline.MinWidth);
            Near(600, baseline.MinHeight);

            var scaled = ShellWindowStartupSize.Calculate(2560, 1440, 144);
            Near(1300, scaled.Width * 1.5);
            Near(850, scaled.Height * 1.5);
            True(scaled.MinWidth < scaled.Width && scaled.MinHeight < scaled.Height);

            var lowerResolution = ShellWindowStartupSize.Calculate(1920, 1080, 96);
            Near(1920 * (1300d / 2560), lowerResolution.Width);
            Near(1080 * (850d / 1440), lowerResolution.Height);
            Near(1920 * (860d / 2560), lowerResolution.MinWidth);
            Near(1080 * (600d / 1440), lowerResolution.MinHeight);

            var highResolution = ShellWindowStartupSize.Calculate(3840, 2160, 144);
            Near(1300, highResolution.Width);
            Near(850, highResolution.Height);
            True(highResolution.MinWidth < highResolution.Width && highResolution.MinHeight < highResolution.Height);
            return Task.CompletedTask;
        });
    }

    private static void Near(double expected, double actual)
    {
        if (Math.Abs(expected - actual) > .001)
            throw new InvalidOperationException($"Expected {expected}, got {actual}");
    }

    private static void True(bool value)
    {
        if (!value) throw new InvalidOperationException("scaled startup minimum must not clamp the default size");
    }
}
