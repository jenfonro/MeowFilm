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
    public RelationalRecord Relation(string name) => relations.Single(relation => relation.Name == name).Record;
    public sealed record SelectedRow(object?[] Key, int Ordinal, JsonObject Value);

    // A selected read never decodes a sibling column or loads an unselected relation.
    public JsonObject? ReadSelected(DatabaseSession db, object?[] key, params string[] members)
    {
        if (key.Length != keys.Length) throw Invalid();
        var row = db.Query("SELECT * FROM " + Q(table) + " WHERE " + Predicate(), Row, Parameters(key)).SingleOrDefault();
        if (row is null) return null;
        var batch = new ReadBatch();
        foreach (var relation in relations.Where(relation => members.Contains(relation.Name)))
        {
            var child = relation.Record;
            var rows = db.Query("SELECT * FROM " + Q(child.table) + " WHERE " + child.Predicate(key.Length) +
                " ORDER BY ordinal", Row, Parameters(key));
            batch.Add(child.table, rows, keys);
            child.LoadChildren(db, key, batch);
        }
        return Read(row, batch, members);
    }
    public IReadOnlyList<SelectedRow> ReadRows(DatabaseSession db, object?[] prefix, string[]? members = null)
    {
        if (prefix.Length > keys.Length) throw Invalid();
        var rows = db.Query("SELECT * FROM " + Q(table) + " WHERE " + Predicate(prefix.Length) + " ORDER BY ordinal", Row, Parameters(prefix));
        if (rows.Count == 0) return [];
        var batch = new ReadBatch();
        if (members is null)
            LoadChildren(db, prefix, batch);
        else
        {
            foreach (var relation in relations)
            {
                if (!members.Contains(relation.Name)) continue;
                var child = relation.Record;
                var childRows = db.Query("SELECT * FROM " + Q(child.table) + " WHERE " + child.Predicate(prefix.Length) +
                    " ORDER BY ordinal", Row, Parameters(prefix));
                batch.Add(child.table, childRows, keys);
                child.LoadChildren(db, prefix, batch);
            }
        }
        return rows.Select(row =>
        {
            var key = keys.Select(name => row[name]).ToArray();
            return new SelectedRow(key, Convert.ToInt32(row["ordinal"]), Read(row, batch, members));
        }).ToArray();
    }
    public bool HasRelation(DatabaseSession db, object?[] key, string name)
    {
        int index = Array.FindIndex(relations, relation => relation.Name == name);
        if (index < 0 || key.Length != keys.Length) throw Invalid();
        var masks = db.Query("SELECT shape_mask,null_mask FROM " + Q(table) + " WHERE " + Predicate(),
            row => (Shape: row.GetInt64(0), Nulls: row.GetInt64(1)), Parameters(key));
        if (masks.Count == 0) return false;
        long bit = 1L << (fields.Length + index);
        if ((masks[0].Shape & bit) == 0 || (masks[0].Nulls & bit) != 0)
            throw new DocumentStoreException(DocumentStoreError.Corrupt);
        return true;
    }

    // Only supplied fields/relations are replaced. Omitted columns, relationships and
    // extension nodes remain in SQLite; no partial projection is a complete previous value.
    public void Patch(DatabaseSession db, JsonObject value, object?[] key, int ordinal = 0) => WritePatch(db, value, key, ordinal, false);
    public void Replace(DatabaseSession db, JsonObject value, object?[] key, int ordinal = 0) => WritePatch(db, value, key, ordinal, true);
    private void WritePatch(DatabaseSession db, JsonObject value, object?[] key, int ordinal, bool replace)
    {
        if (key.Length != keys.Length) throw Invalid();
        if (Extra(value).Count != 0) throw Invalid(); // Existing extension nodes are preserved, never implicitly replaced.
        if (identityField is not null && value[identityField]?.GetValue<string>() != key[^1] as string) throw Invalid();
        long touched = 0, shape = 0, nulls = 0;
        var columns = new List<string>(); var cells = new List<object?>();
        for (int i = 0; i < fields.Length; i++)
            if (replace || value.ContainsKey(fields[i].Name))
            {
                touched |= 1L << i;
                if (value.ContainsKey(fields[i].Name)) shape |= 1L << i;
                if (value.ContainsKey(fields[i].Name) && value[fields[i].Name] is null) nulls |= 1L << i;
                columns.AddRange(fields[i].Columns); cells.AddRange(Cells(fields[i], value[fields[i].Name]));
            }
        for (int i = 0; i < relations.Length; i++)
            if (replace || value.ContainsKey(relations[i].Name))
            {
                touched |= 1L << (fields.Length + i);
                if (value.ContainsKey(relations[i].Name)) shape |= 1L << (fields.Length + i);
                if (value.ContainsKey(relations[i].Name) && value[relations[i].Name] is null) nulls |= 1L << (fields.Length + i);
            }
        string[] names = [.. keys, "ordinal", "shape_mask", "null_mask", .. columns];
        object?[] values = [.. key, ordinal, shape, nulls, .. cells];
        var updates = columns.Select(column => Q(column) + "=excluded." + Q(column)).ToList();
        updates.Add("ordinal=excluded.ordinal");
        updates.Add("shape_mask=((shape_mask & ~$touched) | excluded.shape_mask)");
        updates.Add("null_mask=((null_mask & ~$touched) | excluded.null_mask)");
        string current = Q(table) + ".";
        var changed = columns.Select(column => current + Q(column) + " IS NOT excluded." + Q(column)).ToList();
        changed.Add(current + "ordinal IS NOT excluded.ordinal");
        changed.Add(current + "shape_mask IS NOT ((" + current + "shape_mask & ~$touched) | excluded.shape_mask)");
        changed.Add(current + "null_mask IS NOT ((" + current + "null_mask & ~$touched) | excluded.null_mask)");
        db.Execute("INSERT INTO " + Q(table) + "(" + string.Join(",", names.Select(Q)) + ") VALUES(" +
            string.Join(",", values.Select((_, i) => "$p" + i)) + ") ON CONFLICT(" + string.Join(",", keys.Select(Q)) +
            ") DO UPDATE SET " + string.Join(",", updates) + " WHERE " + string.Join(" OR ", changed),
            [.. Parameters(values), ("$touched", touched)]);
        foreach (var relation in relations.Where(relation => replace || value.ContainsKey(relation.Name)))
        {
            var child = relation.Record;
            var next = Children(relation, value[relation.Name], key);
            IReadOnlyList<string> existing = relation.Kind == RecordRelationKind.Object ? [] : db.Query("SELECT " + Q(child.keys[^1]) + " FROM " + Q(child.table) + " WHERE " + child.Predicate(key.Length),
                row => row.GetString(0), Parameters(key));
            if (relation.Kind == RecordRelationKind.Object)
            {
                if (next.Count == 0) child.Delete(db, key);
                else child.Replace(db, next[0].Value, key);
            }
            else
            {
                var ids = next.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
                foreach (string id in existing.Where(id => !ids.Contains(id))) child.Delete(db, [.. key, id]);
                foreach (var item in next) child.Replace(db, item.Value, item.Key, item.Ordinal);
            }
        }
    }
    public bool Matches(DatabaseSession db, object?[] key, JsonObject value, bool complete = false)
    {
        if (key.Length != keys.Length) throw Invalid();
        var row = db.Query("SELECT * FROM " + Q(table) + " WHERE " + Predicate(), Row, Parameters(key)).SingleOrDefault();
        if (row is null) return false;
        long shape = Convert.ToInt64(row["shape_mask"]), nulls = Convert.ToInt64(row["null_mask"]);
        if (complete)
        {
            long expectedShape = 0, expectedNulls = 0;
            foreach (var (name, index) in fields.Select(field => field.Name).Concat(relations.Select(relation => relation.Name))
                .Select((name, index) => (name, index)))
                if (value.ContainsKey(name))
                {
                    expectedShape |= 1L << index;
                    if (value[name] is null) expectedNulls |= 1L << index;
                }
            if (shape != expectedShape || nulls != expectedNulls) return false;
            foreach (var relation in relations.Where(relation => value[relation.Name] is null))
            {
                var child = relation.Record;
                if (db.Scalar<long>("SELECT EXISTS(SELECT 1 FROM " + Q(child.table) + " WHERE " + child.Predicate(key.Length) + ")",
                    Parameters(key)) != 0) return false;
            }
        }
        foreach (var (field, index) in fields.Select((field, index) => (field, index)))
            if (value.ContainsKey(field.Name))
            {
                if ((shape & (1L << index)) == 0 || ((nulls & (1L << index)) != 0) != (value[field.Name] is null)) return false;
                var cells = Cells(field, value[field.Name]);
                if (field.Columns.Where((column, i) => !CellEquals(row[column], cells[i])).Any()) return false;
            }
        foreach (var (relation, index) in relations.Select((relation, index) => (relation, index)))
            if (value.ContainsKey(relation.Name))
            {
                long bit = 1L << (fields.Length + index);
                if ((shape & bit) == 0 || ((nulls & bit) != 0) != (value[relation.Name] is null)) return false;
                if (value[relation.Name] is null) continue;
                var expected = Children(relation, value[relation.Name], key);
                var child = relation.Record;
                var actual = db.Query("SELECT " + string.Join(",", child.keys.Select(Q)) + " FROM " + Q(child.table) +
                    " WHERE " + child.Predicate(key.Length) + " ORDER BY ordinal", Row, Parameters(key));
                if (actual.Count != expected.Count) return false;
                for (int i = 0; i < expected.Count; i++)
                {
                    var member = expected[i];
                    if (!child.keys.Select(name => actual[i][name]).SequenceEqual(member.Key) ||
                        !child.Matches(db, member.Key, member.Value, complete)) return false;
                }
            }
        return true;
    }
    public void InsertAt(DatabaseSession db, object?[] prefix, int ordinal)
    {
        if (prefix.Length != keys.Length - 1) throw Invalid();
        db.Execute("UPDATE " + Q(table) + " SET ordinal=ordinal+1 WHERE " + Predicate(prefix.Length) + " AND ordinal >= $ordinal",
            [.. Parameters(prefix), ("$ordinal", ordinal)]);
    }
    public void Delete(DatabaseSession db, object?[] key)
    {
        if (key.Length != keys.Length) throw Invalid();
        db.Execute("DELETE FROM " + Q(table) + " WHERE " + Predicate(), Parameters(key));
    }
    public void SetRelationPresent(DatabaseSession db, object?[] key, string name)
    {
        int index = Array.FindIndex(relations, relation => relation.Name == name);
        if (index < 0 || key.Length != keys.Length) throw Invalid();
        long bit = 1L << (fields.Length + index);
        db.Execute("UPDATE " + Q(table) + " SET shape_mask=shape_mask | $bit,null_mask=null_mask & ~$bit WHERE " + Predicate(),
            [.. Parameters(key), ("$bit", bit)]);
    }
    private static bool CellEquals(object? actual, object? expected) =>
        actual is byte[] actualBytes && expected is byte[] expectedBytes
            ? actualBytes.AsSpan().SequenceEqual(expectedBytes)
            : Equals(actual, expected);
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
    private JsonObject Read(IReadOnlyDictionary<string, object?> row, ReadBatch batch, string[]? members = null)
    {
        var obj = new JsonObject();
        if (identityField is not null) obj[identityField] = (string)row[keys[^1]]!;
        object?[] key = keys.Select(name => row[name]).ToArray();
        long shape = Convert.ToInt64(row["shape_mask"]), nulls = Convert.ToInt64(row["null_mask"]);
        if (members is null && (shape < 0 || shape >> (fields.Length + relations.Length) != 0 || (nulls & ~shape) != 0))
            throw new DocumentStoreException(DocumentStoreError.Corrupt);
        for (int i = 0; i < fields.Length; i++)
            if ((shape & (1L << i)) != 0 && (members is null || members.Contains(fields[i].Name))) obj[fields[i].Name] = Value(fields[i], row);
        for (int i = 0; i < relations.Length; i++)
        {
            var relation = relations[i];
            if (members is not null && !members.Contains(relation.Name)) continue;
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
        if (extensionTable is not null && members is null)
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
