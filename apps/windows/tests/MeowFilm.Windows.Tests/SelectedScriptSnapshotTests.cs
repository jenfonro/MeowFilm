using System.Collections.Generic;
namespace MeowFilm.Windows.Tests;
public static class SelectedScriptSnapshotTests
{
    public static IEnumerable<TestCase> Cases() => new SelectedSnapshotCases(script: true).Cases();
}
