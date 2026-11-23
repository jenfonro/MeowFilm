using System.Text.Json;

namespace MeowFilm.Core.Debug;

public static class DebugParams
{
    public static string? GetString(JsonElement? parameters, string name) =>
        parameters is { ValueKind: JsonValueKind.Object } obj &&
        obj.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString() : null;

    public static int? GetInt(JsonElement? parameters, string name) =>
        parameters is { ValueKind: JsonValueKind.Object } obj &&
        obj.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var val)
            ? val : null;

    public static double? GetDouble(JsonElement? parameters, string name) =>
        parameters is { ValueKind: JsonValueKind.Object } obj &&
        obj.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out var val)
            ? val : null;

    public static bool? GetBool(JsonElement? parameters, string name) =>
        parameters is { ValueKind: JsonValueKind.Object } obj &&
        obj.TryGetProperty(name, out var prop) && prop.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? prop.GetBoolean() : null;
}
