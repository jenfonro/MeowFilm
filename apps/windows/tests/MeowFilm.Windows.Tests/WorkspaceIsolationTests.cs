using System.IO;
using System.Diagnostics;

namespace MeowFilm.Windows.Tests;

public static class WorkspaceIsolationTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("data root must be explicitly absolute", () =>
        {
            Reject(() => TestWorkspace.ResolveDataRoot(null));
            Reject(() => TestWorkspace.ResolveDataRoot(""));
            Reject(() => TestWorkspace.ResolveDataRoot("relative-data"));
        });
        yield return Case("data root cannot be source checkout or results owner itself", () =>
        {
            Reject(() => TestWorkspace.ResolveDataRoot(TestWorkspace.SourceRoot));
            Reject(() => TestWorkspace.ResolveDataRoot(TestWorkspace.ResultsRoot));
            Reject(() => TestWorkspace.ResolveDataRoot(TestWorkspace.ResultsRoot + "-other"));
        });
        yield return Case("data descendants retain boundaries and reject traversal", () =>
        {
            string child = TestWorkspace.DataPath("isolation", "child");
            if (!child.StartsWith(TestWorkspace.DataRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Missing test owner.");
            Reject(() => TestWorkspace.ValidatePath(TestWorkspace.DataRoot,
                Path.Combine(TestWorkspace.DataRoot, "..", "foreign")));
            Reject(() => TestWorkspace.ValidatePath(TestWorkspace.DataRoot, TestWorkspace.DataRoot + "-foreign"));
            Reject(() => TestWorkspace.DataPath(TestWorkspace.SourceRoot));
        });
        yield return Case("source, build and results paths are bound to the compiled checkout", () =>
        {
            if (!File.Exists(Path.Combine(TestWorkspace.SourceRoot, "apps", "windows", "global.json")) ||
                TestWorkspace.BuildRoot != Path.Combine(TestWorkspace.SourceRoot, "build", "windows") ||
                TestWorkspace.TmpRoot != Path.Combine(TestWorkspace.SourceRoot, "tmp") ||
                TestWorkspace.ResultsRoot != Path.Combine(TestWorkspace.TmpRoot, "test-results"))
                throw new InvalidOperationException("Wrong Windows dependency owner.");
        });
        yield return Case("inherited source and Node overrides cannot redirect the compiled runner", () =>
        {
            foreach (string name in new[] { "MEOW_FILM_TEST_ROOT", "MEOWFILM_TEST_NODE", "MEOW_FILM_TEST_NODE" })
            {
                string? original = Environment.GetEnvironmentVariable(name);
                try
                {
                    Environment.SetEnvironmentVariable(name, TestWorkspace.DataPath("unowned"));
                    Reject(TestWorkspace.Initialize);
                }
                finally { Environment.SetEnvironmentVariable(name, original); }
            }
        });
        yield return Case("junction write aliases are rejected before use", () =>
        {
            string fixture = TestWorkspace.DataPath("isolation-" + Guid.NewGuid().ToString("N"));
            string target = Path.Combine(fixture, "target"), link = Path.Combine(fixture, "link");
            Directory.CreateDirectory(target);
            try
            {
                var start = new ProcessStartInfo(TestWorkspace.NodePath)
                {
                    WorkingDirectory = TestWorkspace.SourceRoot, UseShellExecute = false,
                    CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
                };
                foreach (string argument in new[] { "--input-type=module", "-e",
                    "import {symlinkSync} from 'node:fs'; symlinkSync(process.argv[1],process.argv[2],'junction');", target, link })
                    start.ArgumentList.Add(argument);
                using var process = Process.Start(start)!;
                if (!process.WaitForExit(10_000))
                {
                    process.Kill(entireProcessTree: true);
                    throw new InvalidOperationException("Junction fixture creation timed out.");
                }
                if (process.ExitCode != 0) throw new InvalidOperationException("Junction fixture creation failed: " + process.StandardError.ReadToEnd());
                Reject(() => TestWorkspace.ValidatePath(fixture, Path.Combine(link, "new-data")));
                Directory.Delete(target);
                Reject(() => TestWorkspace.ValidatePath(fixture, Path.Combine(link, "dangling-data")));
            }
            finally
            {
                // Remove only the link itself, never its target through recursion.
                try
                {
                    if ((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0) Directory.Delete(link);
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                TestWorkspace.ValidatePath(TestWorkspace.DataRoot, fixture);
                if (Directory.Exists(target)) Directory.Delete(target);
                Directory.Delete(fixture);
            }
        });
    }

    private static TestCase Case(string name, Action action) =>
        new("Windows workspace isolation " + name, () => { action(); return Task.CompletedTask; });
    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Expected an isolation rejection.");
    }
}
