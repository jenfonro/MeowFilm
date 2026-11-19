using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeowFilm.Platform.Storage;

/// <summary>Typed nodes only for explicitly declared third-party extension values and host KV.</summary>
public static class DatabaseValueTree
{
    public static readonly string[] Columns = ["node_id", "parent_id", "prop_key", "item_index", "kind",
        "bool_value", "int_value", "real_value", "text_value"];
    private static DocumentStoreException Invalid() => new(DocumentStoreError.Corrupt);

    public static IReadOnlyList<object?[]> Rows(JsonNode? value)
    {
        var rows = new List<object?[]>();
        void Visit(JsonNode? node, int? parent, string? property, int? index, int depth)
        {
            if (depth > 64) throw new DocumentStoreException(DocumentStoreError.Limit);
            int id = rows.Count;
            string kind;
            object? flag = null, integer = null, real = null, text = null;
            if (node is null) kind = "null";
            else if (node is JsonObject) kind = "object";
            else if (node is JsonArray) kind = "array";
            else if (node is JsonValue scalar)
            {
                if (scalar.TryGetValue<string>(out var str)) { kind = "text"; text = str; }
                else if (scalar.TryGetValue<bool>(out bool boolean)) { kind = "boolean"; flag = boolean ? 1L : 0L; }
                else if (scalar.TryGetValue<long>(out long number)) { kind = "integer"; integer = number; }
                else
                {
                    var literal = scalar.ToJsonString();
                    using var parsed = JsonDocument.Parse(literal);
                    if (parsed.RootElement.ValueKind != JsonValueKind.Number) throw Invalid();
                    // Retain exact upstream number lexemes, not rounded binary IDs.
                    kind = "number_text"; text = literal;
                }
            }
            else throw Invalid();
            rows.Add([id, parent, property, index, kind, flag, integer, real, text]);
            if (node is JsonObject obj)
                foreach (var pair in obj) Visit(pair.Value, id, pair.Key, null, depth + 1);
            else if (node is JsonArray array)
                for (int i = 0; i < array.Count; i++) Visit(array[i], id, null, i, depth + 1);
        }
        Visit(value, null, null, null, 0);
        return rows;
    }

    public static JsonNode? Value(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        if (rows.Count == 0) throw Invalid();
        var byId = rows.ToDictionary(row => Convert.ToInt32(row["node_id"]));
        var childrenByParent = rows.Where(row => row["parent_id"] is not null)
            .GroupBy(row => Convert.ToInt32(row["parent_id"])).ToDictionary(group => group.Key, group => group.ToArray());
        var consumed = new HashSet<int>();
        JsonNode? Read(int id, int depth)
        {
            if (depth > 64 || !consumed.Add(id) || !byId.TryGetValue(id, out var row)) throw Invalid();
            var children = childrenByParent.GetValueOrDefault(id, []);
            string kind = row["kind"] as string ?? throw Invalid();
            if (kind is not ("array" or "object") && children.Length != 0) throw Invalid();
            switch (kind)
            {
                case "null": return null;
                case "text": return JsonValue.Create(row["text_value"] as string ?? throw Invalid());
                case "boolean":
                    long flag = Convert.ToInt64(row["bool_value"] ?? throw Invalid());
                    if (flag is not (0 or 1)) throw Invalid();
                    return JsonValue.Create(flag == 1);
                case "integer": return JsonValue.Create(Convert.ToInt64(row["int_value"] ?? throw Invalid()));
                case "real": return JsonValue.Create(Convert.ToDouble(row["real_value"] ?? throw Invalid()));
                case "number_text":
                    var value = JsonNode.Parse(row["text_value"] as string ?? throw Invalid());
                    if (value is not JsonValue scalar || scalar.GetValueKind() != JsonValueKind.Number) throw Invalid();
                    return value;
                case "array":
                    var array = new JsonArray();
                    foreach (var child in children.OrderBy(child => Convert.ToInt32(child["item_index"])))
                    {
                        if (child["prop_key"] is not null || child["item_index"] is null ||
                            Convert.ToInt32(child["item_index"]) != array.Count) throw Invalid();
                        array.Add(Read(Convert.ToInt32(child["node_id"]), depth + 1));
                    }
                    return array;
                case "object":
                    var obj = new JsonObject();
                    foreach (var child in children.OrderBy(child => Convert.ToInt32(child["node_id"])))
                    {
                        string key = child["prop_key"] as string ?? throw Invalid();
                        if (child["item_index"] is not null || obj.ContainsKey(key)) throw Invalid();
                        obj.Add(key, Read(Convert.ToInt32(child["node_id"]), depth + 1));
                    }
                    return obj;
                default: throw Invalid();
            }
        }
        if (!byId.TryGetValue(0, out var root) || root["parent_id"] is not null ||
            root["prop_key"] is not null || root["item_index"] is not null) throw Invalid();
        var result = Read(0, 0);
        if (consumed.Count != rows.Count) throw Invalid();
        return result;
    }
}
