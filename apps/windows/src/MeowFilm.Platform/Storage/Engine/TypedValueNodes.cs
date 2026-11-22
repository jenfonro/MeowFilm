using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace MeowFilm.Platform.Storage;

public enum StorageValueKind
{
    Null,
    Boolean,
    Integer,
    Real,
    ExactNumber,
    Text,
    Array,
    Object
}

public readonly record struct StorageValueField(string Name, StorageValue Value);

/// <summary>
/// Pure strongly-typed bounded value tree strictly for declared third-party extension node tables
/// (music_song_source_extras, novel_book_variables, windows_novel_source_definition_nodes,
/// windows_music_playback_source_rec_extras). Zero JSON-AST dependency.
/// </summary>
public sealed class StorageValue : IEquatable<StorageValue>
{
    public static StorageValue Null { get; } = new(StorageValueKind.Null, false, 0, 0, null, null, null);
    public static StorageValue True { get; } = new(StorageValueKind.Boolean, true, 0, 0, null, null, null);
    public static StorageValue False { get; } = new(StorageValueKind.Boolean, false, 0, 0, null, null, null);

    public StorageValueKind Kind { get; }
    public bool BoolValue { get; }
    public long IntValue { get; }
    public double RealValue { get; }
    public string? TextValue { get; }
    public IReadOnlyList<StorageValue>? ArrayItems { get; }
    public IReadOnlyList<StorageValueField>? ObjectFields { get; }

    private StorageValue(
        StorageValueKind kind, bool boolValue, long intValue, double realValue,
        string? textValue, IReadOnlyList<StorageValue>? arrayItems, IReadOnlyList<StorageValueField>? objectFields)
    {
        Kind = kind;
        BoolValue = boolValue;
        IntValue = intValue;
        RealValue = realValue;
        TextValue = textValue;
        ArrayItems = arrayItems;
        ObjectFields = objectFields;
    }

    public static StorageValue FromBool(bool value) => value ? True : False;
    public static StorageValue FromInt64(long value) => new(StorageValueKind.Integer, false, value, 0, null, null, null);
    public static StorageValue FromReal(double value, string? literal = null)
    {
        if (!double.IsFinite(value)) throw new DocumentStoreException(DocumentStoreError.InvalidRequest);
        if (BitConverter.DoubleToInt64Bits(value) == BitConverter.DoubleToInt64Bits(-0.0))
            return FromExactNumber("-0");
        return new(StorageValueKind.Real, false, 0, value, literal ?? value.ToString("R", CultureInfo.InvariantCulture), null, null);
    }
    public static StorageValue FromExactNumber(string literal)
    {
        if (string.IsNullOrEmpty(literal)) throw new DocumentStoreException(DocumentStoreError.InvalidRequest);
        return new(StorageValueKind.ExactNumber, false, 0, 0, literal, null, null);
    }
    public static StorageValue FromText(string value) => new(StorageValueKind.Text, false, 0, 0, value ?? throw new DocumentStoreException(DocumentStoreError.InvalidRequest), null, null);
    public static StorageValue FromArray(IReadOnlyList<StorageValue> items) => new(StorageValueKind.Array, false, 0, 0, null, items, null);
    public static StorageValue FromObject(IReadOnlyList<StorageValueField> fields) => new(StorageValueKind.Object, false, 0, 0, null, null, fields);

    public bool IsEmptyObject => Kind == StorageValueKind.Object && (ObjectFields?.Count ?? 0) == 0;

    public int EstimateBytes() => Kind switch
    {
        StorageValueKind.Null or StorageValueKind.Boolean => 4,
        StorageValueKind.Integer or StorageValueKind.Real => 16,
        StorageValueKind.ExactNumber or StorageValueKind.Text => 2 + Encoding.UTF8.GetByteCount(TextValue ?? ""),
        StorageValueKind.Array => 2 + (ArrayItems?.Sum(item => 1 + item.EstimateBytes()) ?? 0),
        StorageValueKind.Object => 2 + (ObjectFields?.Sum(f => 4 + Encoding.UTF8.GetByteCount(f.Name) + f.Value.EstimateBytes()) ?? 0),
        _ => 8
    };

    public bool Equals(StorageValue? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null || Kind != other.Kind) return false;
        return Kind switch
        {
            StorageValueKind.Null => true,
            StorageValueKind.Boolean => BoolValue == other.BoolValue,
            StorageValueKind.Integer => IntValue == other.IntValue,
            StorageValueKind.Real => BitConverter.DoubleToInt64Bits(RealValue) == BitConverter.DoubleToInt64Bits(other.RealValue),
            StorageValueKind.ExactNumber or StorageValueKind.Text => string.Equals(TextValue, other.TextValue, StringComparison.Ordinal),
            StorageValueKind.Array => ArrayEquals(ArrayItems!, other.ArrayItems!),
            StorageValueKind.Object => ObjectEquals(ObjectFields!, other.ObjectFields!),
            _ => false
        };
    }

    public override bool Equals(object? obj) => obj is StorageValue other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Kind, BoolValue, IntValue, RealValue, TextValue);

    private static bool ArrayEquals(IReadOnlyList<StorageValue> left, IReadOnlyList<StorageValue> right)
    {
        if (left.Count != right.Count) return false;
        for (int i = 0; i < left.Count; i++)
            if (!left[i].Equals(right[i])) return false;
        return true;
    }

    private static bool ObjectEquals(IReadOnlyList<StorageValueField> left, IReadOnlyList<StorageValueField> right)
    {
        if (left.Count != right.Count) return false;
        bool sameOrder = true;
        for (int i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i].Name, right[i].Name, StringComparison.Ordinal)) { sameOrder = false; break; }
            if (!left[i].Value.Equals(right[i].Value)) return false;
        }
        if (sameOrder) return true;
        var map = new Dictionary<string, StorageValue>(right.Count, StringComparer.Ordinal);
        foreach (var f in right) map[f.Name] = f.Value;
        foreach (var f in left)
            if (!map.TryGetValue(f.Name, out var rv) || !f.Value.Equals(rv)) return false;
        return true;
    }

    public static bool StructuralEquals(StorageValue? left, StorageValue? right) =>
        left is null ? right is null : left.Equals(right);
}

/// <summary>
/// Bounded typed value-tree SQLite codec strictly for declared third-party extension node tables.
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

    public static IReadOnlyList<NodeRow> Encode(StorageValue root)
    {
        var rows = new List<NodeRow>();
        void Visit(StorageValue node, int? parent, string? propKey, int? itemIndex, int depth)
        {
            if (depth > MaxDepth) throw new DocumentStoreException(DocumentStoreError.Limit);
            int id = rows.Count;
            switch (node.Kind)
            {
                case StorageValueKind.Null:
                    rows.Add(new NodeRow(id, parent, propKey, itemIndex, "null", null, null, null, null));
                    break;
                case StorageValueKind.Boolean:
                    rows.Add(new NodeRow(id, parent, propKey, itemIndex, "boolean", node.BoolValue ? 1L : 0L, null, null, null));
                    break;
                case StorageValueKind.Integer:
                    rows.Add(new NodeRow(id, parent, propKey, itemIndex, "integer", null, node.IntValue, null, null));
                    break;
                case StorageValueKind.Real:
                    rows.Add(new NodeRow(id, parent, propKey, itemIndex, "real", null, null, node.RealValue, node.TextValue));
                    break;
                case StorageValueKind.ExactNumber:
                    rows.Add(new NodeRow(id, parent, propKey, itemIndex, "number_text", null, null, null, node.TextValue));
                    break;
                case StorageValueKind.Text:
                    rows.Add(new NodeRow(id, parent, propKey, itemIndex, "text", null, null, null, node.TextValue));
                    break;
                case StorageValueKind.Array:
                    rows.Add(new NodeRow(id, parent, propKey, itemIndex, "array", null, null, null, null));
                    if (node.ArrayItems is { } items)
                        for (int i = 0; i < items.Count; i++) Visit(items[i], id, null, i, depth + 1);
                    break;
                case StorageValueKind.Object:
                    rows.Add(new NodeRow(id, parent, propKey, itemIndex, "object", null, null, null, null));
                    if (node.ObjectFields is { } fields)
                        foreach (var field in fields) Visit(field.Value, id, field.Name, null, depth + 1);
                    break;
                default:
                    throw Invalid();
            }
        }
        Visit(root, null, null, null, 0);
        return rows;
    }

    public static StorageValue? Decode(IReadOnlyList<NodeRow> rows)
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
        StorageValue Read(int id, int depth)
        {
            if (depth > MaxDepth || !consumed.Add(id) || !byId.TryGetValue(id, out var row)) throw Invalid();
            var children = childrenByParent.GetValueOrDefault(id);
            if (row.Kind is not ("array" or "object") && children is { Count: > 0 }) throw Invalid();
            switch (row.Kind)
            {
                case "null": return StorageValue.Null;
                case "text": return StorageValue.FromText(row.TextValue ?? throw Invalid());
                case "boolean":
                    long flag = row.BoolValue ?? throw Invalid();
                    if (flag is not (0 or 1)) throw Invalid();
                    return StorageValue.FromBool(flag == 1);
                case "integer": return StorageValue.FromInt64(row.IntValue ?? throw Invalid());
                case "real":
                {
                    double realValue = row.RealValue ?? throw Invalid();
                    if (!double.IsFinite(realValue)) throw Invalid();
                    return StorageValue.FromReal(realValue, row.TextValue);
                }
                case "number_text":
                {
                    string literal = row.TextValue ?? throw Invalid();
                    if (literal.Length == 0) throw Invalid();
                    return StorageValue.FromExactNumber(literal);
                }
                case "array":
                {
                    var items = new List<StorageValue>(children?.Count ?? 0);
                    if (children is not null)
                    {
                        children.Sort((a, b) => Nullable.Compare(a.ItemIndex, b.ItemIndex));
                        foreach (var child in children)
                        {
                            if (child.PropKey is not null || child.ItemIndex is null || child.ItemIndex != items.Count)
                                throw Invalid();
                            items.Add(Read(child.NodeId, depth + 1));
                        }
                    }
                    return StorageValue.FromArray(items);
                }
                case "object":
                {
                    var fields = new List<StorageValueField>(children?.Count ?? 0);
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    if (children is not null)
                    {
                        children.Sort((a, b) => a.NodeId.CompareTo(b.NodeId));
                        foreach (var child in children)
                        {
                            string key = child.PropKey ?? throw Invalid();
                            if (child.ItemIndex is not null || !seen.Add(key)) throw Invalid();
                            fields.Add(new StorageValueField(key, Read(child.NodeId, depth + 1)));
                        }
                    }
                    return StorageValue.FromObject(fields);
                }
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

    public static void Replace(DatabaseSession db, string table, (string Column, object Value)[] keys, StorageValue? value,
        bool omitEmptyObject = true)
    {
        if (keys.Length == 0) throw new DocumentStoreException(DocumentStoreError.InvalidRequest);
        var safeTable = Ident(table);
        var where = string.Join(" AND ", keys.Select((k, i) => $"{Ident(k.Column)}=$k{i}"));
        var keyParams = keys.Select((k, i) => ($"$k{i}", (object?)k.Value)).ToArray();
        db.Execute($"DELETE FROM {safeTable} WHERE {where}", keyParams);
        if (value is null || (omitEmptyObject && value.IsEmptyObject)) return;

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

    public static StorageValue? ReadObject(DatabaseSession db, string table, (string Column, object Value)[] keys)
    {
        if (keys.Length == 0) throw new DocumentStoreException(DocumentStoreError.InvalidRequest);
        var safeTable = Ident(table);
        var where = string.Join(" AND ", keys.Select((k, i) => $"{Ident(k.Column)}=$k{i}"));
        var keyParams = keys.Select((k, i) => ($"$k{i}", (object?)k.Value)).ToArray();
        var rows = db.Query(
            $"SELECT node_id,parent_id,prop_key,item_index,kind,bool_value,int_value,real_value,text_value FROM {safeTable} WHERE {where} ORDER BY node_id",
            r => ReadRow(r, 0), keyParams);
        if (rows.Count == 0) return null;
        var decoded = Decode(rows);
        if (decoded is null || decoded.Kind != StorageValueKind.Object) throw Invalid();
        return decoded;
    }
}
