using MeowFilm.Windows.Tests;

namespace MeowFilm.FilmKernel.Tests;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args) => TestUiHost.Run(() => RunAsync(args));

    private static async Task<int> RunAsync(string[] args)
    {
        if (!TestDesktopContext.Verify()) return 1;
        TestWorkspace.Initialize();
        var groups = new Dictionary<string, Func<IEnumerable<TestCase>>>(StringComparer.Ordinal)
        {
            [nameof(FilmKernelIntegrationTests)] = FilmKernelIntegrationTests.Cases,
            [nameof(RuntimeMediaTests)] = RuntimeMediaTests.Cases,
            [nameof(NativeVideoInputTests)] = NativeVideoInputTests.Cases,
            [nameof(NativeHttpMediaTransferTests)] = NativeHttpMediaTransferTests.Cases
        };
        TestCase[] cases;
        string scope;
        if (args.Length > 1 && args[0] == "--group" && args.Skip(1).All(groups.ContainsKey))
        {
            var selected = args.Skip(1).Distinct(StringComparer.Ordinal).ToArray();
            cases = selected.SelectMany(name => groups[name]()).ToArray();
            scope = string.Join(',', selected);
        }
        else if (args is ["--case", var name])
        {
            cases = groups.Values.SelectMany(factory => factory()).Where(test => test.Name == name).ToArray();
            scope = "case:" + name;
        }
        else throw new ArgumentException("Choose --group <explicit direct-test groups> or --case <exact case name>; no full App target.");
        if (cases.Length == 0 || cases.Select(test => test.Name).Distinct().Count() != cases.Length)
            throw new InvalidOperationException("Empty or duplicate direct-test inventory.");
        Console.WriteLine("SCOPE film-kernel-direct:" + scope);
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
