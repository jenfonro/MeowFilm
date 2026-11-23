namespace MeowFilm.Core.Runtime;

public sealed class BridgeException : Exception
{
    public string Code { get; }
    public BridgeException(string code) : base(string.IsNullOrWhiteSpace(code) ? "INTERNAL" : code) => Code = Message;
}
