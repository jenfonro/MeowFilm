using System.Text.Json;
using System.Text.RegularExpressions;

namespace MeowFilm.Core.Runtime;

/// <summary>The public contracts declared by apps/runtime/contracts.mjs.</summary>
public static partial class RuntimeHttpProtocol
{
    public const string Name = "runtime.http.v1";
    internal sealed record ModuleContract(string Version, string Name, string Prefix);
    internal static readonly IReadOnlyDictionary<string, ModuleContract> Modules =
        new Dictionary<string, ModuleContract>(StringComparer.Ordinal)
    {
        ["music-platform"] = new("0.1.0", "music.platform.v1", "music.platform."),
        ["netease"] = new("0.1.0", "netease.token.v1", "netease.token."),
        ["tmdb"] = new("0.1.0", "tmdb.data.v1", "tmdb.data."),
        ["music"] = new("0.8.0", "music.script.v3", "music.script."),
        ["source"] = new("0.3.0", "source.runtime.v2", "source.runtime."),
        ["cat"] = new("0.2.0", "cat.script.v2", "cat.script."),
        ["pan"] = new("0.1.0", "pan.runtime.v1", "pan."),
        ["douban"] = new("0.1.0", "douban.data.v1", "douban.data."),
        ["film"] = new("0.1.0", "film.playback.v1", "film."),
    };

    [GeneratedRegex(@"\A/v1/tmdb/images/(?:original|[wh][1-9][0-9]*)/[A-Za-z0-9_-]+\.[A-Za-z0-9]+\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex ImagePattern();
    [GeneratedRegex(@"\A/v1/douban/images/(?!\.{1,2}(?:/|\z))[A-Za-z0-9._~-]+(?:/(?!\.{1,2}(?:/|\z))[A-Za-z0-9._~-]+)*\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex DoubanImagePattern();
    [GeneratedRegex(@"\A/v1/source/images/[A-Za-z0-9-]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex SourceImagePattern();

    public static bool ValidImageReference(string? reference) => ImageProvider(reference) is not null;

    internal static string? ImageProvider(string? reference) => reference is null ? null :
        ImagePattern().IsMatch(reference) ? "tmdb" : DoubanImagePattern().IsMatch(reference) ? "douban" :
        SourceImagePattern().IsMatch(reference) ? "source" : null;

    public static bool ValidMethod(string? method) => !string.IsNullOrEmpty(method);

    public static Uri ValidateEndpoint(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(endpoint.Host))
            throw new BridgeException("INVALID_REQUEST");
        return endpoint;
    }

    public static void ValidateDescription(JsonElement description, string product,
        IReadOnlyList<RuntimeModule> expectedModules)
    {
        try
        {
            if (BridgeCodec.RequiredProperty(description, "protocol").GetString() != Name
                || BridgeCodec.RequiredProperty(description, "product").GetString() != product)
                throw new BridgeException("INVALID_RESPONSE");
            var declaredModules = BridgeCodec.RequiredProperty(description, "modules");
            var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (var module in declaredModules.EnumerateArray())
            {
                if (module.ValueKind != JsonValueKind.Object || !module.TryGetProperty("id", out var idProp) ||
                    idProp.ValueKind != JsonValueKind.String) continue;
                var id = idProp.GetString()!;
                var expected = expectedModules.SingleOrDefault(item => item.Id == id);
                if (expected is null) continue;
                if (!identities.Add(id) || !Modules.TryGetValue(id, out var contract) || expected.Version != contract.Version
                    || BridgeCodec.RequiredProperty(module, "version").GetString() != expected.Version
                    || BridgeCodec.RequiredProperty(module, "contract").GetString() != contract.Name)
                    throw new BridgeException("INVALID_RESPONSE");
            }
            if (expectedModules.Any(module => !identities.Contains(module.Id)))
                throw new BridgeException("INVALID_RESPONSE");
        }
        catch { throw new BridgeException("INVALID_RESPONSE"); }
    }

    internal static JsonElement ValidateEnvelope(ReadOnlyMemory<byte> bytes, string contract)
    {
        try
        {
            var value = BridgeCodec.ParseJSON(bytes);
            var ok = BridgeCodec.RequiredProperty(value, "ok").GetBoolean();
            var actualContract = BridgeCodec.RequiredProperty(value, "contract").GetString();
            if (actualContract == Name && contract != Name)
            {
                if (ok) throw new BridgeException("INVALID_RESPONSE");
            }
            else if (actualContract != contract) throw new BridgeException("INVALID_RESPONSE");
            if (ok) _ = BridgeCodec.RequiredProperty(value, "value");
            else if (string.IsNullOrEmpty(BridgeCodec.RequiredProperty(value, "code").GetString()))
                throw new BridgeException("INVALID_RESPONSE");
            return value;
        }
        catch { throw new BridgeException("INVALID_RESPONSE"); }
    }
}
