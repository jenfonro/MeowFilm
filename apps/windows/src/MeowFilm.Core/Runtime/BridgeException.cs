namespace MeowFilm.Core.Runtime;

public sealed class BridgeException : Exception
{
    public static readonly IReadOnlySet<string> Codes = BuildCodes();

    private static HashSet<string> BuildCodes()
    {
        var codes = new HashSet<string>(StringComparer.Ordinal)
        {
            "CANCELLED", "TIMEOUT", "CLOSED", "LIMIT", "INVALID_MESSAGE", "METHOD_UNAVAILABLE", "INTERNAL"
        };
        codes.UnionWith(RuntimeHttpProtocol.PublicCodes);
        foreach (var module in RuntimeHttpProtocol.Modules.Values) codes.UnionWith(module.Codes);
        return codes;
    }

    public string Code { get; }
    public BridgeException(string code) : base(Codes.Contains(code) ? code : "INTERNAL") => Code = Message;
}
