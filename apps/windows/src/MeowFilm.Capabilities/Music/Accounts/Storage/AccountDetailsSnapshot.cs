namespace MeowFilm.Capabilities.Accounts;

/// <summary>Native display snapshot lifetime. Platform membership interpretation belongs to Node.</summary>
internal static class AccountDetailsSnapshot
{
    internal const long ProfileTtl = 7 * 86400000L;
    internal static MusicAccountEntitlements Freeze(MusicAccountEntitlements value) =>
        value with { AudioLevels = Array.AsReadOnly(value.AudioLevels.ToArray()) };
}
