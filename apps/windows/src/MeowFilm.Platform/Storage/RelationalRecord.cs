using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeowFilm.Platform.Storage;

public enum RecordFieldKind { Text, Integer, Real, Boolean, Identifier, Bytes }
public enum RecordRelationKind { Object, Array, Dictionary, Values, MapValues }
public sealed record RecordField(string Name, RecordFieldKind Kind, string? ColumnName = null)
{
    internal string Column => ColumnName ?? Name;
    internal string[] Columns => Kind == RecordFieldKind.Identifier ? [Column + "_kind", Column + "_value"] : [Column];
}
public sealed record RecordRelation(string Name, RecordRelationKind Kind, RelationalRecord Record, string? Identity = null);

/// <summary>Closed, repository-declared columns and relationships; no generic business EAV or JSON payload.</summary>
public sealed class RelationalRecord(string table, string[] keys, RecordField[] fields, RecordRelation[] relations,
    string? extensionTable = null, string? identityField = null)
{
    private readonly string table = table;
    private readonly string[] keys = keys;
    private readonly RecordField[] fields = fields;
    private readonly RecordRelation[] relations = relations;
    public string Table => table;
    public IReadOnlyList<string> Keys => keys;
    private static DocumentStoreException Invalid() => new(DocumentStoreError.InvalidRequest);
    private static string Q(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
    private string Predicate(int? count = null) => string.Join(" AND ", keys.Take(count ?? keys.Length)
        .Select((key, index) => Q(key) + " IS $p" + index).DefaultIfEmpty("1"));
    private static (string, object?)[] Parameters(IEnumerable<object?> values) =>
        values.Select((value, index) => ("$p" + index, value)).ToArray();
    private static IReadOnlyDictionary<string, object?> Row(Microsoft.Data.Sqlite.SqliteDataReader reader) =>
        Enumerable.Range(0, reader.FieldCount).ToDictionary(reader.GetName, i => reader.IsDBNull(i) ? null : reader.GetValue(i));
    private JsonObject Extra(JsonObject value)
    {
        var names = fields.Select(field => field.Name).Concat(relations.Select(relation => relation.Name)).ToHashSet(StringComparer.Ordinal);
        if (identityField is not null) names.Add(identityField);
        var extra = new JsonObject(value.Where(pair => !names.Contains(pair.Key)).Select(pair =>
            KeyValuePair.Create(pair.Key, pair.Value?.DeepClone())));
        if (extensionTable is null && extra.Count != 0) throw Invalid();
        return extra;
    }
    private static object?[] Cells(RecordField field, JsonNode? value)
    {
        if (value is null) return new object?[field.Columns.Length];
        if (value is not JsonValue scalar) throw Invalid();
        switch (field.Kind)
        {
            case RecordFieldKind.Text:
                if (!scalar.TryGetValue<string>(out var text)) throw Invalid();
                return [text];
            case RecordFieldKind.Boolean:
                if (!scalar.TryGetValue<bool>(out bool flag)) throw Invalid();
                return [flag ? 1L : 0L];
            case RecordFieldKind.Integer:
                if (!double.TryParse(scalar.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double number) ||
                    !double.IsFinite(number) || Math.Abs(number) > 9007199254740991 || Math.Truncate(number) != number) throw Invalid();
                long integer = (long)number;
                return [integer];
            case RecordFieldKind.Real:
                if (!double.TryParse(scalar.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double real) ||
                    !double.IsFinite(real)) throw Invalid();
                return [real];
            case RecordFieldKind.Identifier:
                if (scalar.TryGetValue<string>(out text)) return ["text", text];
                if (scalar.GetValueKind() != JsonValueKind.Number) throw Invalid();
                return ["number", scalar.ToJsonString()];
            case RecordFieldKind.Bytes:
                if (!scalar.TryGetValue<string>(out text)) throw Invalid();
                return [Convert.FromBase64String(text)];
            default: throw Invalid();
        }
    }
    private static JsonNode? Value(RecordField field, IReadOnlyDictionary<string, object?> row)
    {
        if (row[field.Columns[0]] is null) return null;
        return field.Kind switch
        {
            RecordFieldKind.Text => JsonValue.Create((string)row[field.Column]!),
            RecordFieldKind.Boolean => JsonValue.Create(Convert.ToInt64(row[field.Column]) == 1),
            RecordFieldKind.Integer => JsonValue.Create(Convert.ToInt64(row[field.Column])),
            RecordFieldKind.Real => JsonValue.Create(Convert.ToDouble(row[field.Column], CultureInfo.InvariantCulture)),
            RecordFieldKind.Identifier when row[field.Column + "_kind"] as string == "text" =>
                JsonValue.Create((string)row[field.Column + "_value"]!),
            RecordFieldKind.Identifier when row[field.Column + "_kind"] as string == "number" =>
                JsonNode.Parse((string)row[field.Column + "_value"]!),
            RecordFieldKind.Bytes => JsonValue.Create(Convert.ToBase64String((byte[])row[field.Column]!)),
            _ => throw new DocumentStoreException(DocumentStoreError.Corrupt)
        };
    }
    private sealed class ReadBatch
    {
        private readonly Dictionary<string, Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>>> tables = new(StringComparer.Ordinal);
        private static string Identity(IEnumerable<object?> key) => string.Concat(key.Select(value =>
        {
            var text = value as string ?? throw new DocumentStoreException(DocumentStoreError.Corrupt);
            return text.Length.ToString(CultureInfo.InvariantCulture) + ":" + text;
        }));
        internal IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows(string table, object?[] key) =>
            tables[table].TryGetValue(Identity(key), out var rows) ? rows : [];
        internal void Add(string table, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, IEnumerable<string> keys)
        {
            var names = keys.ToArray();
            tables[table] = rows.GroupBy(row => Identity(names.Select(name => row[name])), StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => (IReadOnlyList<IReadOnlyDictionary<string, object?>>)group.ToArray(), StringComparer.Ordinal);
        }
    }
    public JsonObject? Read(DatabaseSession db, object?[] key)
    {
        if (key.Length != keys.Length) throw Invalid();
        var row = db.Query("SELECT * FROM " + Q(table) + " WHERE " + Predicate(), Row, Parameters(key)).SingleOrDefault();
        if (row is null) return null;
        var batch = new ReadBatch();
        LoadChildren(db, key, batch);
        return Read(row, batch);
    }
    public IReadOnlyList<JsonObject> ReadMany(DatabaseSession db, object?[] prefix)
    {
        if (prefix.Length > keys.Length) throw Invalid();
        var rows = db.Query("SELECT * FROM " + Q(table) + " WHERE " + Predicate(prefix.Length) + " ORDER BY ordinal", Row, Parameters(prefix));
        if (rows.Count == 0) return [];
        var batch = new ReadBatch();
        LoadChildren(db, prefix, batch);
        return rows.Select(row => Read(row, batch)).ToArray();
    }
    // Each declared relation table is read once for this root/scope, not once per parent row.
    private void LoadChildren(DatabaseSession db, object?[] rootKey, ReadBatch batch)
    {
        foreach (var relation in relations)
        {
            var child = relation.Record;
            var rows = db.Query("SELECT * FROM " + Q(child.table) + " WHERE " + child.Predicate(rootKey.Length) +
                " ORDER BY ordinal", Row, Parameters(rootKey));
            batch.Add(child.table, rows, keys);
            child.LoadChildren(db, rootKey, batch);
        }
        if (extensionTable is not null)
        {
            var nodes = db.Query("SELECT * FROM " + Q(extensionTable) + " WHERE " + Predicate(rootKey.Length) +
                " ORDER BY node_id", Row, Parameters(rootKey));
            batch.Add(extensionTable, nodes, keys);
        }
    }
    private JsonObject Read(IReadOnlyDictionary<string, object?> row, ReadBatch batch)
    {
        var obj = new JsonObject();
        if (identityField is not null) obj[identityField] = (string)row[keys[^1]]!;
        object?[] key = keys.Select(name => row[name]).ToArray();
        long shape = Convert.ToInt64(row["shape_mask"]), nulls = Convert.ToInt64(row["null_mask"]);
        if (shape < 0 || shape >> (fields.Length + relations.Length) != 0 || (nulls & ~shape) != 0)
            throw new DocumentStoreException(DocumentStoreError.Corrupt);
        for (int i = 0; i < fields.Length; i++)
            if ((shape & (1L << i)) != 0) obj[fields[i].Name] = Value(fields[i], row);
        for (int i = 0; i < relations.Length; i++)
        {
            var relation = relations[i];
            long mask = 1L << (fields.Length + i);
            if ((shape & mask) == 0) continue;
            if ((nulls & mask) != 0) { obj[relation.Name] = null; continue; }
            var child = relation.Record;
            var rows = batch.Rows(child.table, key);
            if (relation.Kind == RecordRelationKind.Object)
            {
                if (rows.Count != 1) throw new DocumentStoreException(DocumentStoreError.Corrupt);
                obj[relation.Name] = child.Read(rows[0], batch);
            }
            else if (relation.Kind is RecordRelationKind.Dictionary or RecordRelationKind.MapValues)
            {
                var dictionary = new JsonObject();
                foreach (var entry in rows)
                {
                    string name = (string)entry[child.keys[^1]]!;
                    var value = child.Read(entry, batch);
                    dictionary.Add(name, relation.Kind == RecordRelationKind.MapValues ? value["value"]?.DeepClone() : value);
                }
                obj[relation.Name] = dictionary;
            }
            else
            {
                var array = new JsonArray();
                foreach (var entry in rows)
                {
                    var value = child.Read(entry, batch);
                    array.Add(relation.Kind == RecordRelationKind.Values ? value["value"]?.DeepClone() : value);
                }
                obj[relation.Name] = array;
            }
        }
        if (extensionTable is not null)
        {
            var nodes = batch.Rows(extensionTable, key);
            if (nodes.Count != 0)
            {
                if (DatabaseValueTree.Value(nodes) is not JsonObject extra || Extra(extra).Count != extra.Count)
                    throw new DocumentStoreException(DocumentStoreError.Corrupt);
                foreach (var pair in extra) obj.Add(pair.Key, pair.Value?.DeepClone());
            }
        }
        return obj;
    }
    private sealed record Child(string Id, object?[] Key, JsonObject Value, int Ordinal);
    private List<Child> Children(RecordRelation relation, JsonNode? value, object?[] key)
    {
        if (value is null) return [];
        if (relation.Kind == RecordRelationKind.Object)
            return value is JsonObject obj ? [new("", key, obj, 0)] : throw Invalid();
        IEnumerable<(string Id, JsonNode? Value)> members;
        if (relation.Kind is RecordRelationKind.Dictionary or RecordRelationKind.MapValues)
        {
            if (value is not JsonObject dictionary) throw Invalid();
            members = dictionary.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => (pair.Key, pair.Value));
        }
        else
        {
            if (value is not JsonArray array) throw Invalid();
            members = array.Select((node, index) => (relation.Identity is null ? index.ToString(CultureInfo.InvariantCulture) :
                relation.Kind == RecordRelationKind.Values ? node?.GetValue<string>() ?? throw Invalid() :
                    node?[relation.Identity]?.GetValue<string>() ?? throw Invalid(), node));
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<Child>();
        foreach (var (id, node) in members)
        {
            if (!seen.Add(id)) throw Invalid();
            JsonObject obj = relation.Kind is RecordRelationKind.Values or RecordRelationKind.MapValues ?
                new() { ["value"] = node?.DeepClone() } : node as JsonObject ?? throw Invalid();
            result.Add(new(id, [.. key, id], obj, result.Count));
        }
        return result;
    }
    public void Save(DatabaseSession db, JsonObject? value, JsonObject? previous, object?[] key, int ordinal = 0, int previousOrdinal = 0,
        IReadOnlyDictionary<string, object?>? indexed = null)
    {
        if (key.Length != keys.Length) throw Invalid();
        if (JsonNode.DeepEquals(value, previous) && ordinal == previousOrdinal) return;
        if (value is null) { db.Execute("DELETE FROM " + Q(table) + " WHERE " + Predicate(), Parameters(key)); return; }
        var extra = Extra(value);
        if (identityField is not null && value[identityField]?.GetValue<string>() != key[^1] as string) throw Invalid();
        long shape = 0, nulls = 0;
        foreach (var (name, index) in fields.Select(field => field.Name).Concat(relations.Select(relation => relation.Name))
            .Select((name, index) => (name, index)))
            if (value.ContainsKey(name)) { shape |= 1L << index; if (value[name] is null) nulls |= 1L << index; }
        string[] columns = [.. keys, "ordinal", "shape_mask", "null_mask", .. fields.SelectMany(field => field.Columns), .. indexed?.Keys ?? []];
        object?[] cells = [.. key, ordinal, shape, nulls, .. fields.SelectMany(field => Cells(field, value[field.Name])), .. indexed?.Values ?? []];
        var updates = columns.Where(column => !keys.Contains(column, StringComparer.Ordinal)).ToArray();
        db.Execute("INSERT INTO " + Q(table) + "(" + string.Join(",", columns.Select(Q)) + ") VALUES(" +
            string.Join(",", cells.Select((_, i) => "$p" + i)) + ") ON CONFLICT(" + string.Join(",", keys.Select(Q)) +
            ") DO UPDATE SET " + string.Join(",", updates.Select(column => Q(column) + "=excluded." + Q(column))) +
            " WHERE " + string.Join(" OR ", updates.Select(column => Q(table) + "." + Q(column) + " IS NOT excluded." + Q(column))),
            Parameters(cells));
        foreach (var relation in relations)
        {
            var before = Children(relation, previous?[relation.Name], key);
            var next = Children(relation, value[relation.Name], key);
            var oldById = before.ToDictionary(child => child.Id, StringComparer.Ordinal);
            var nextIds = next.Select(child => child.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var child in before.Where(child => !nextIds.Contains(child.Id)))
                relation.Record.Save(db, null, child.Value, child.Key);
            foreach (var child in next)
            {
                oldById.TryGetValue(child.Id, out var old);
                relation.Record.Save(db, child.Value, old?.Value, child.Key, child.Ordinal, old?.Ordinal ?? 0);
            }
        }
        if (extensionTable is not null && !JsonNode.DeepEquals(extra, Extra(previous ?? new())))
        {
            db.Execute("DELETE FROM " + Q(extensionTable) + " WHERE " + Predicate(), Parameters(key));
            if (extra.Count != 0)
                foreach (var node in DatabaseValueTree.Rows(extra))
                {
                    object?[] values = [.. key, .. node];
                    db.Execute("INSERT INTO " + Q(extensionTable) + "(" + string.Join(",", keys.Concat(DatabaseValueTree.Columns).Select(Q)) +
                        ") VALUES(" + string.Join(",", values.Select((_, i) => "$p" + i)) + ")", Parameters(values));
                }
        }
    }
}
