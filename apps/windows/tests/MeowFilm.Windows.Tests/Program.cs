using System.Reflection;

namespace MeowFilm.Windows.Tests;

public sealed record TestCase(string Name, Func<Task> Run);

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        TestWorkspace.Initialize();
        var groups = Assembly.GetExecutingAssembly().GetTypes()
            .Where(type => type.Name.EndsWith("Tests", StringComparison.Ordinal))
            .Select(type => type.GetMethod("Cases", BindingFlags.Public | BindingFlags.Static))
            .Where(method => method is not null)
            .ToDictionary(method => method!.DeclaringType!.Name,
                method => ((IEnumerable<TestCase>)method!.Invoke(null, null)!).ToArray(), StringComparer.Ordinal);
        groups[nameof(MusicNativeSyncTests)] = MusicNativeSyncTests.Cases()
            .Concat(MusicNativeSyncTests.RecoveryCases()).Concat(MusicNativeSyncTests.CloudCases()).ToArray();
        groups[nameof(MusicFeedbackProtocolTests)] = MusicFeedbackProtocolTests.Cases()
            .Concat(MusicFeedbackProtocolTests.QqCases()).ToArray();
        groups[nameof(MusicKwMgCatalogTests)] = MusicKwMgCatalogTests.Cases()
            .Concat(MusicKwMgCatalogTests.DrainCases()).ToArray();
        var allTests = groups.Values.SelectMany(cases => cases).OrderBy(test => test.Name, StringComparer.Ordinal).ToArray();
        if (allTests.Length == 0 || allTests.Select(test => test.Name).Distinct(StringComparer.Ordinal).Count() != allTests.Length)
            throw new InvalidOperationException("Empty or duplicate test inventory");
        string[]? selectedGroups = null;
        string? selectedCase = null;
        if (args.Length != 0)
        {
            if (args[0] == "--group" && args.Length > 1 && args.Skip(1).All(groups.ContainsKey))
                selectedGroups = args.Skip(1).Distinct(StringComparer.Ordinal).ToArray();
            else if (args.Length == 2 && args[0] == "--case" && allTests.Any(test => test.Name == args[1]))
                selectedCase = args[1];
            else throw new ArgumentException("Usage: [--group <groups...> | --case <exact case name>]");
        }
        Console.WriteLine(selectedGroups is null && selectedCase is null ? "SCOPE full-native-matrix" :
            "SCOPE partial-code-tests:" + (selectedGroups is not null ? string.Join(',', selectedGroups) : "case:" + selectedCase));
        foreach (var group in groups.Where(pair => selectedGroups is null || selectedGroups.Contains(pair.Key))
                     .OrderBy(pair => pair.Key, StringComparer.Ordinal))
            Console.WriteLine($"INVENTORY {group.Key}; CASES {group.Value.Length}");
        var tests = (selectedGroups is not null ? selectedGroups.SelectMany(group => groups[group]) :
                selectedCase is not null ? allTests.Where(test => test.Name == selectedCase) : allTests)
            .OrderBy(test => test.Name, StringComparer.Ordinal).ToArray();
        if (tests.Length == 0 || tests.Select(test => test.Name).Distinct().Count() != tests.Length)
            throw new InvalidOperationException("Empty or duplicate test inventory");
        var failed = 0;
        foreach (var test in tests)
        {
            try { await test.Run(); Console.WriteLine($"PASS {test.Name}"); }
            catch (Exception error)
            {
                failed++;
                Console.WriteLine($"FAIL {test.Name}: {error.GetType().Name}: {error.Message}");
            }
        }
        Console.WriteLine($"TOTAL {tests.Length}; PASSED {tests.Length - failed}; FAILED {failed}; SKIPPED 0");
        return failed == 0 ? 0 : 1;
    }
}
