using MeowFilm.Windows.Tests;

namespace MeowFilm.Windows.Tests;

public sealed record TestCase(string Name, Func<Task> Run);

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        TestWorkspace.Initialize();
        var groups = new Dictionary<string, Func<IEnumerable<TestCase>>>(StringComparer.Ordinal)
        {
            [nameof(FFmpegVideoKernelTests)] = FFmpegVideoKernelTests.Cases,
            [nameof(NativeHandoffAdmissionTests)] = NativeHandoffAdmissionTests.Cases,
            [nameof(RuntimeMediaTests)] = RuntimeMediaTests.Cases,
            [nameof(MediaOutputTests)] = MediaOutputTests.Cases
        };
        if (args.Length < 2 || args[0] != "--group" || args.Skip(1).Any(name => !groups.ContainsKey(name)))
            throw new ArgumentException("Choose --group <explicit direct-test groups>; no full App target.");
        var selected = args.Skip(1).Distinct(StringComparer.Ordinal).ToArray();
        var cases = selected.SelectMany(name => groups[name]()).ToArray();
        if (cases.Length == 0 || cases.Select(test => test.Name).Distinct().Count() != cases.Length)
            throw new InvalidOperationException("Empty or duplicate direct-test inventory.");
        Console.WriteLine("SCOPE video-kernel-direct:" + string.Join(',', selected));
        int failed = 0;
        foreach (var test in cases)
        {
            try { await test.Run(); Console.WriteLine("PASS " + test.Name); }
            catch (Exception error)
            {
                failed++;
                Console.WriteLine($"FAIL {test.Name}: {error.GetType().Name}: {error.Message}");
            }
        }
        Console.WriteLine($"TOTAL {cases.Length}; PASSED {cases.Length - failed}; FAILED {failed}; SKIPPED 0");
        return failed == 0 ? 0 : 1;
    }
}
