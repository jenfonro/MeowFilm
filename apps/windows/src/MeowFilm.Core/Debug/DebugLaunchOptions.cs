using System.Globalization;
using System.Net;

namespace MeowFilm.Core.Debug;

public sealed record DebugLaunchOptions(
    bool Enabled,
    int Port,
    string Bind,
    string? DebugDir,
    IReadOnlyList<string> Modules,
    int InlineLimit,
    int EventBytesLimit,
    int KeepSessions,
    bool FirstChanceExceptions,
    string? DataRoot)
{
    public const int DefaultInlineLimit = 2048;
    public const int DefaultEventBytesLimit = 16384;
    public const int DefaultKeepSessions = 10;
    public const string DefaultBind = "127.0.0.1";

    public static DebugLaunchOptions Disabled { get; } = new(
        Enabled: false,
        Port: 0,
        Bind: DefaultBind,
        DebugDir: null,
        Modules: ["all"],
        InlineLimit: DefaultInlineLimit,
        EventBytesLimit: DefaultEventBytesLimit,
        KeepSessions: DefaultKeepSessions,
        FirstChanceExceptions: false,
        DataRoot: null);

    public static (DebugLaunchOptions Options, string? Error) Parse(string[]? args)
    {
        if (args is null || args.Length == 0) return (Disabled, null);
        bool enabled = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--debug", StringComparison.Ordinal))
            {
                enabled = true;
                break;
            }
        }
        // Non-debug launch ignores all --debug-* and --data-root options with zero side effects.
        if (!enabled) return (Disabled, null);

        int port = 0;
        string bind = DefaultBind;
        string? debugDir = null;
        IReadOnlyList<string> modules = ["all"];
        int inlineLimit = DefaultInlineLimit;
        int eventLimit = DefaultEventBytesLimit;
        int keep = DefaultKeepSessions;
        bool firstChance = false;
        string? dataRoot = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--debug":
                    break;
                case "--debug-first-chance":
                    firstChance = true;
                    break;
                case "--debug-port":
                {
                    if (!TryNextValue(args, ref i, arg, out var raw, out var error)) return (Disabled, error);
                    if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 0 or > 65535)
                        return (Disabled, $"Invalid value for --debug-port: '{raw}' (expected 0..65535).");
                    break;
                }
                case "--debug-bind":
                {
                    if (!TryNextValue(args, ref i, arg, out var raw, out var error)) return (Disabled, error);
                    if (!IPAddress.TryParse(raw, out _))
                        return (Disabled, $"Invalid IP address for --debug-bind: '{raw}'.");
                    bind = raw;
                    break;
                }
                case "--debug-dir":
                {
                    if (!TryNextValue(args, ref i, arg, out var raw, out var error)) return (Disabled, error);
                    if (!TryNormalizeDirectoryPath(raw, "--debug-dir", out debugDir, out var pathErr))
                        return (Disabled, pathErr);
                    break;
                }
                case "--debug-modules":
                {
                    if (!TryNextValue(args, ref i, arg, out var raw, out var error)) return (Disabled, error);
                    var parsed = DebugCategories.ParseModules(raw);
                    if (parsed.Error is not null) return (Disabled, parsed.Error);
                    modules = parsed.Modules;
                    break;
                }
                case "--debug-inline-limit":
                {
                    if (!TryNextValue(args, ref i, arg, out var raw, out var error)) return (Disabled, error);
                    if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out inlineLimit) || inlineLimit < 0)
                        return (Disabled, $"Invalid value for --debug-inline-limit: '{raw}' (expected >= 0).");
                    break;
                }
                case "--debug-event-limit":
                {
                    if (!TryNextValue(args, ref i, arg, out var raw, out var error)) return (Disabled, error);
                    if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out eventLimit) || eventLimit < 0)
                        return (Disabled, $"Invalid value for --debug-event-limit: '{raw}' (expected >= 0).");
                    break;
                }
                case "--debug-keep":
                {
                    if (!TryNextValue(args, ref i, arg, out var raw, out var error)) return (Disabled, error);
                    if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out keep) || keep < 1)
                        return (Disabled, $"Invalid value for --debug-keep: '{raw}' (expected >= 1).");
                    break;
                }
                case "--data-root":
                {
                    if (!TryNextValue(args, ref i, arg, out var raw, out var error)) return (Disabled, error);
                    if (!TryNormalizeDirectoryPath(raw, "--data-root", out dataRoot, out var pathErr))
                        return (Disabled, pathErr);
                    break;
                }
                default:
                    if (arg.StartsWith("--debug-", StringComparison.Ordinal))
                        return (Disabled, $"Unknown debug option: '{arg}'.");
                    break;
            }
        }

        return (new DebugLaunchOptions(
            Enabled: true,
            Port: port,
            Bind: bind,
            DebugDir: debugDir,
            Modules: modules,
            InlineLimit: inlineLimit,
            EventBytesLimit: eventLimit,
            KeepSessions: keep,
            FirstChanceExceptions: firstChance,
            DataRoot: dataRoot), null);
    }

    private static readonly char[] DisallowedPathChars = ['<', '>', '"', '|', '?', '*'];

    private static bool TryNormalizeDirectoryPath(string raw, string option, out string fullPath, out string? error)
    {
        fullPath = "";
        if (string.IsNullOrWhiteSpace(raw) || raw.Any(char.IsControl) ||
            raw.IndexOfAny(Path.GetInvalidPathChars()) >= 0 ||
            raw.IndexOfAny(DisallowedPathChars) >= 0)
        {
            error = $"Invalid path for {option}: '{raw}'.";
            return false;
        }
        // Only a single Windows drive letter colon at index 1 (e.g. C:\...) is valid.
        int firstColon = raw.IndexOf(':');
        if (firstColon >= 0)
        {
            bool validDrivePrefix = firstColon == 1 && char.IsAsciiLetter(raw[0]) && raw.IndexOf(':', 2) < 0;
            if (!validDrivePrefix)
            {
                error = $"Invalid path for {option}: '{raw}'.";
                return false;
            }
        }
        try
        {
            fullPath = Path.GetFullPath(raw);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = $"Invalid path for {option}: {ex.Message}";
            return false;
        }
    }

    private static bool TryNextValue(string[] args, ref int index, string option, out string value, out string? error)
    {
        if (index + 1 >= args.Length)
        {
            value = "";
            error = $"Missing value for {option}.";
            return false;
        }
        value = args[++index];
        error = null;
        return true;
    }
}
