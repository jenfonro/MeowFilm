// SPDX-License-Identifier: GPL-3.0-only
namespace MeowFilm.Windows.Tests;
public static class MusicWyAccountClientTests
{
    public static IEnumerable<TestCase> Cases() => MusicAccountRuntimeFixture.QrCases("wy", "music.native.accounts.wy.client.");
}
