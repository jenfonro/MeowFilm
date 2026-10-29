using System.Collections.Generic;
namespace MeowFilm.Windows.Tests;
public static class SelectedJsonSnapshotTests
{
    public static IEnumerable<TestCase> Cases() => new SelectedSnapshotCases(script: false).Cases();
}
