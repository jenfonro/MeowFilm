using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace MeowFilm.Windows.Tests;

internal static class TestDesktopContext
{
    internal static bool Verify(bool requireTarget = false)
    {
        string? expectedUser = Environment.GetEnvironmentVariable("MEOWFILM_TEST_DESKTOP_EXPECTED_USER");
        string? expectedSessionId = Environment.GetEnvironmentVariable("MEOWFILM_TEST_DESKTOP_EXPECTED_SESSION_ID");
        if (!requireTarget && expectedUser is null && expectedSessionId is null) return true;
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("TEST_DESKTOP_CHECK FAIL UNSUPPORTED");
            return false;
        }
        try
        {
            using var process = Process.GetCurrentProcess();
            using var identity = WindowsIdentity.GetCurrent();
            string user = identity.Name;
            int sessionId = process.SessionId;
            bool interactive = Environment.UserInteractive;
            if (!GetTokenInformation(identity.AccessToken, 20, out int elevation, sizeof(int), out _))
                throw new InvalidOperationException("Desktop token information unavailable.");
            bool elevated = elevation != 0;
            Console.WriteLine($"TEST_DESKTOP_CONTEXT PID={process.Id}; Identity={user}; SessionId={sessionId}; UserInteractive={interactive}; Elevated={elevated}");
            bool matches = !string.IsNullOrWhiteSpace(expectedUser) &&
                int.TryParse(expectedSessionId, NumberStyles.Integer, CultureInfo.InvariantCulture, out int expectedSession) &&
                string.Equals(user, expectedUser, StringComparison.OrdinalIgnoreCase) && sessionId == expectedSession &&
                interactive && !elevated;
            Console.WriteLine(matches ? "TEST_DESKTOP_CHECK PASS" : "TEST_DESKTOP_CHECK FAIL MISMATCH");
            return matches;
        }
        catch
        {
            Console.WriteLine("TEST_DESKTOP_CHECK FAIL UNAVAILABLE");
            return false;
        }
    }
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind,
        out int value, int length, out int returned);
}
