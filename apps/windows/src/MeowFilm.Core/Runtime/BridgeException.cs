namespace MeowFilm.Core.Runtime;

public sealed class BridgeException : Exception
{
    public static readonly IReadOnlySet<string> Codes = new HashSet<string>(StringComparer.Ordinal)
    {
        "CANCELLED", "TIMEOUT", "CLOSED", "LIMIT", "INVALID_MESSAGE", "METHOD_UNAVAILABLE", "INTERNAL",
        "INVALID_REQUEST", "METHOD_NOT_FOUND", "PERMISSION_DENIED", "INVALID_RESPONSE"
    };

    public string Code { get; }
    public BridgeException(string code) : base(Codes.Contains(code) ? code : "INTERNAL") => Code = Message;
}
