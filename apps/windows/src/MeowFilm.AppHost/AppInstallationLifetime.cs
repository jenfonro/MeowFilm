using System.Security.Cryptography;
using System.Text;

namespace MeowFilm.AppHost;

/// <summary>Coordinates only installer admission and the lifetime of this user's App processes.</summary>
public static class AppInstallationLifetime
{
    // Keep this handle until process termination, including real startup/close cleanup.
    private static Mutex? running;

    public static bool TryEnter()
    {
        string user = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string prefix = @"Global\meowfilm-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.Unicode.GetBytes(user)));
        using var admission = new Mutex(false, prefix + "-Maintenance");
        try
        {
            if (!admission.WaitOne(0)) return false;
        }
        catch (AbandonedMutexException) { } // The abandoned mutex has been acquired.
        try
        {
            running ??= new Mutex(false, prefix + "-Running");
            return true;
        }
        finally { admission.ReleaseMutex(); }
    }
}
