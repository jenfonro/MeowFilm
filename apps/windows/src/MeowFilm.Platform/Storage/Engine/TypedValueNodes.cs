using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace MeowFilm.Platform.Storage;

/// <summary>
/// Bounded typed value-tree codec strictly for declared third-party extension node tables
/// (e.g. third-party music source extras, imported novel book/source definition extensions).
/// Never used for internal business entities.
/// </summary>
public static class TypedValueNodes
{
    private const int MaxDepth = 64;

    public readonly record struct NodeRow(
        int NodeId, int? ParentId, string? PropKey, int? ItemIndex,
        string Kind, long? BoolValue, long? IntValue, double? RealValue, string? TextValue);

    private static DocumentStoreException Invalid() => new(DocumentStoreError.Corrupt);

    private static string Ident(string identifier)
    {
        if (string.IsNullOrEmpty(identifier)) throw new DocumentStoreException(DocumentStoreError.InvalidRequest);
        foreach (char c in identifier)
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '_'))
                throw new DocumentStoreException(DocumentStoreError.InvalidRequest);
        return identifier;
    }

    public static IReadOnlyList<NodeRow> Encode(JsonNode? root)
    {
        var rows = new List<NodeRow>();
        void Visit(JsonNode? node, int? parent, string? propKey, int? itemIndex, int depth)
        {
            if (depth > MaxDepth) throw new DocumentStoreException(DocumentStoreError.Limit);
            int id = rows.Count;
            string kind;
            long? flag = null, integer = null;
            double? real = null;
            string? text = null;
            if (node is null) kind = "null";
            else if (node is JsonObject) kind = "object";
            else if (node is JsonArray) kind = "array";
            else if (node is JsonValue scalar)
            {
                if (scalar.TryGetValue<string>(out var str)) { kind = "text"; text = str; }
                else if (scalar.TryGetValue<bool>(out bool boolean)) { kind = "boolean"; flag = boolean ? 1L : 0L; }
                else if (scalar.TryGetValue<long>(out long number)) { kind = "integer"; integer = number; }
                else if (scalar.TryGetValue<int>(out int i32)) { kind = "integer"; integer = i32; }
                else if (scalar.TryGetValue<double>(out double d))
                {
                    if (!double.IsFinite(d)) throw new DocumentStoreException(DocumentStoreError.InvalidRequest);
                    if (BitConverter.DoubleToInt64Bits(d) == BitConverter.DoubleToInt64Bits(-0.0))
                    {
                        kind = "number_text";
                        text = "-0";
                    }
                    else
                    {
                        kind = "real";
                        real = d;
                        text = d.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                    }
                }
                else if (scalar.TryGetValue<float>(out float f))
                {
                    if (!float.IsFinite(f)) throw new DocumentStoreException(DocumentStoreError.InvalidRequest);
                    double fd = f;
                    if (BitConverter.DoubleToInt64Bits(fd) == BitConverter.DoubleToInt64Bits(-0.0))
                    {
                        kind = "number_text";
                        text = "-0";
                    }
                    else
                    {
                        kind = "real";
                        real = fd;
                        text = fd.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                    }
                }
                else
                {
                    string literal;
                    try { literal = scalar.ToJsonString(); }
                    catch (ArgumentException) { throw new DocumentStoreException(DocumentStoreError.InvalidRequest); }
                    using var parsed = JsonDocument.Parse(literal);
                    if (parsed.RootElement.ValueKind != JsonValueKind.Number) throw Invalid();
                    if (literal == "-0")
                    {
                        kind = "number_text";
                        text = "-0";
                    }
                    else if (parsed.RootElement.TryGetInt64(out long parsedLong) &&
                             parsedLong.ToString(System.Globalization.CultureInfo.InvariantCulture) == literal)
                    {
                        kind = "integer";
                        integer = parsedLong;
                    }
                    else if (parsed.RootElement.TryGetDouble(out double parsedDouble) && double.IsFinite(parsedDouble) &&
                             parsedDouble.ToString("R", System.Globalization.CultureInfo.InvariantCulture) == literal)
                    {
                        kind = "real";
                        real = parsedDouble;
                        text = literal;
                    }
                    else
                    {
                        kind = "number_text";
                        text = literal;
                    }
                }
            }
            else throw Invalid();

            rows.Add(new NodeRow(id, parent, propKey, itemIndex, kind, flag, integer, real, text));
            if (node is JsonObject obj)
                foreach (var pair in obj) Visit(pair.Value, id, pair.Key, null, depth + 1);
            else if (node is JsonArray array)
                for (int i = 0; i < array.Count; i++) Visit(array[i], id, null, i, depth + 1);
        }
        Visit(root, null, null, null, 0);
        return rows;
    }

    public static JsonNode? Decode(IReadOnlyList<NodeRow> rows)
    {
        if (rows.Count == 0) return null;
        var byId = new Dictionary<int, NodeRow>(rows.Count);
        var childrenByParent = new Dictionary<int, List<NodeRow>>();
        foreach (var row in rows)
        {
            if (!byId.TryAdd(row.NodeId, row)) throw Invalid();
            if (row.ParentId is { } parent)
            {
                if (!childrenByParent.TryGetValue(parent, out var list))
                {
                    list = [];
                    childrenByParent[parent] = list;
                }
                list.Add(row);
            }
        }
        if (!byId.TryGetValue(0, out var root) || root.ParentId is not null ||
            root.PropKey is not null || root.ItemIndex is not null)
            throw Invalid();

        var consumed = new HashSet<int>();
        JsonNode? Read(int id, int depth)
        {
            if (depth > MaxDepth || !consumed.Add(id) || !byId.TryGetValue(id, out var row)) throw Invalid();
            var children = childrenByParent.GetValueOrDefault(id);
            if (row.Kind is not ("array" or "object") && children is { Count: > 0 }) throw Invalid();
            switch (row.Kind)
            {
                case "null": return null;
                case "text": return JsonValue.Create(row.TextValue ?? throw Invalid());
                case "boolean":
                    long flag = row.BoolValue ?? throw Invalid();
                    if (flag is not (0 or 1)) throw Invalid();
                    return JsonValue.Create(flag == 1);
                case "integer": return JsonValue.Create(row.IntValue ?? throw Invalid());
                case "real":
                {
                    double realValue = row.RealValue ?? throw Invalid();
                    if (!double.IsFinite(realValue)) throw Invalid();
                    return JsonValue.Create(realValue);
                }
                case "number_text":
                {
                    JsonNode? value;
                    try { value = JsonNode.Parse(row.TextValue ?? throw Invalid()); }
                    catch (JsonException) { throw Invalid(); }
                    if (value is not JsonValue scalar || scalar.GetValueKind() != JsonValueKind.Number) throw Invalid();
                    return value;
                }
                case "array":
                    var array = new JsonArray();
                    if (children is not null)
                    {
                        children.Sort((a, b) => Nullable.Compare(a.ItemIndex, b.ItemIndex));
                        foreach (var child in children)
                        {
                            if (child.PropKey is not null || child.ItemIndex is null || child.ItemIndex != array.Count)
                                throw Invalid();
                            array.Add(Read(child.NodeId, depth + 1));
                        }
                    }
                    return array;
                case "object":
                    var obj = new JsonObject();
                    if (children is not null)
                    {
                        children.Sort((a, b) => a.NodeId.CompareTo(b.NodeId));
                        foreach (var child in children)
                        {
                            string key = child.PropKey ?? throw Invalid();
                            if (child.ItemIndex is not null || obj.ContainsKey(key)) throw Invalid();
                            obj.Add(key, Read(child.NodeId, depth + 1));
                        }
                    }
                    return obj;
                default: throw Invalid();
            }
        }

        var result = Read(0, 0);
        if (consumed.Count != rows.Count) throw Invalid();
        return result;
    }

    public static NodeRow ReadRow(SqliteDataReader reader, int offset) => new(
        reader.GetInt32(offset),
        reader.GetNullableInt32(offset + 1),
        reader.GetNullableString(offset + 2),
        reader.GetNullableInt32(offset + 3),
        reader.GetString(offset + 4),
        reader.GetNullableInt64(offset + 5),
        reader.GetNullableInt64(offset + 6),
        reader.GetNullableDouble(offset + 7),
        reader.GetNullableString(offset + 8));

    public static void Replace(DatabaseSession db, string table, (string Column, object Value)[] keys, JsonNode? value,
        bool omitEmptyObject = true)
    {
        if (keys.Length == 0) throw new DocumentStoreException(DocumentStoreError.InvalidRequest);
        var safeTable = Ident(table);
        var where = string.Join(" AND ", keys.Select((k, i) => $"{Ident(k.Column)}=$k{i}"));
        var keyParams = keys.Select((k, i) => ($"$k{i}", (object?)k.Value)).ToArray();
        db.Execute($"DELETE FROM {safeTable} WHERE {where}", keyParams);
        if (value is null || (omitEmptyObject && value is JsonObject obj && obj.Count == 0)) return;

        var keyCols = string.Join(",", keys.Select(k => Ident(k.Column)));
        var keyPlaceholders = string.Join(",", keys.Select((_, i) => $"$k{i}"));
        var insertSql = $"INSERT INTO {safeTable}({keyCols},node_id,parent_id,prop_key,item_index,kind,bool_value,int_value,real_value,text_value) " +
            $"VALUES({keyPlaceholders},$nid,$pid,$pk,$idx,$kind,$bv,$iv,$rv,$tv)";
        var rows = Encode(value);
        var parameters = new (string Name, object? Value)[keys.Length + 9];
        for (int i = 0; i < keys.Length; i++) parameters[i] = keyParams[i];
        foreach (var row in rows)
        {
            parameters[keys.Length] = ("$nid", row.NodeId);
            parameters[keys.Length + 1] = ("$pid", row.ParentId);
            parameters[keys.Length + 2] = ("$pk", row.PropKey);
            parameters[keys.Length + 3] = ("$idx", row.ItemIndex);
            parameters[keys.Length + 4] = ("$kind", row.Kind);
            parameters[keys.Length + 5] = ("$bv", row.BoolValue);
            parameters[keys.Length + 6] = ("$iv", row.IntValue);
            parameters[keys.Length + 7] = ("$rv", row.RealValue);
            parameters[keys.Length + 8] = ("$tv", row.TextValue);
            db.Execute(insertSql, parameters);
        }
    }

    public static JsonObject? ReadObject(DatabaseSession db, string table, (string Column, object Value)[] keys)
    {
        if (keys.Length == 0) throw new DocumentStoreException(DocumentStoreError.InvalidRequest);
        var safeTable = Ident(table);
        var where = string.Join(" AND ", keys.Select((k, i) => $"{Ident(k.Column)}=$k{i}"));
        var keyParams = keys.Select((k, i) => ($"$k{i}", (object?)k.Value)).ToArray();
        var rows = db.Query(
            $"SELECT node_id,parent_id,prop_key,item_index,kind,bool_value,int_value,real_value,text_value FROM {safeTable} WHERE {where} ORDER BY node_id",
            r => ReadRow(r, 0), keyParams);
        if (rows.Count == 0) return null;
        return Decode(rows) as JsonObject ?? throw Invalid();
    }
}
