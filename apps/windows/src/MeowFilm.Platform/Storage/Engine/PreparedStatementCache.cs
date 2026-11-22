using Microsoft.Data.Sqlite;

namespace MeowFilm.Platform.Storage;

/// <summary>Bounded per-connection LRU cache of prepared SqliteCommand instances.</summary>
internal sealed class PreparedStatementCache(SqliteConnection connection, int maxEntries = 256) : IDisposable
{
    internal sealed class Entry(string sql, SqliteCommand command)
    {
        public string Sql { get; } = sql;
        public SqliteCommand Command { get; } = command;
        public string[]? ParameterNames { get; set; }
        public bool InUse { get; set; }
    }

    internal struct Lease(PreparedStatementCache? cache, LinkedListNode<Entry>? node, SqliteCommand command) : IDisposable
    {
        private bool faulted;
        public SqliteCommand Command { get; } = command;

        public void MarkFaulted() => faulted = true;

        public void Dispose()
        {
            if (cache is null || node is null)
            {
                Command.Dispose();
                return;
            }
            cache.Return(node, faulted);
        }
    }

    private readonly Dictionary<string, LinkedListNode<Entry>> bySql = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> lru = new();
    private bool disposed;

    public Lease Rent(string sql, SqliteTransaction? transaction, ReadOnlySpan<(string Name, object? Value)> parameters)
    {
        if (sql.Contains('\0')) throw new DocumentStoreException(DocumentStoreError.InvalidRequest);
        if (disposed || !IsCacheable(sql))
        {
            var direct = CreateCommand(sql, transaction, parameters);
            return new Lease(null, null, direct);
        }

        if (bySql.TryGetValue(sql, out var existing))
        {
            if (existing.Value.InUse)
            {
                var nested = CreateCommand(sql, transaction, parameters);
                return new Lease(null, null, nested);
            }
            lru.Remove(existing);
            lru.AddFirst(existing);
            BindCached(existing.Value, transaction, parameters);
            existing.Value.InUse = true;
            return new Lease(this, existing, existing.Value.Command);
        }

        EvictIfNeeded();
        var command = CreateCommand(sql, transaction, parameters);
        try
        {
            command.Prepare();
        }
        catch
        {
            command.Dispose();
            throw;
        }
        var entry = new Entry(sql, command)
        {
            InUse = true,
            ParameterNames = CaptureNames(parameters)
        };
        var node = new LinkedListNode<Entry>(entry);
        lru.AddFirst(node);
        bySql.Add(sql, node);
        return new Lease(this, node, command);
    }

    private void Return(LinkedListNode<Entry> node, bool faulted)
    {
        var entry = node.Value;
        entry.Command.Transaction = null;
        entry.InUse = false;
        if (faulted || disposed)
        {
            bySql.Remove(entry.Sql);
            if (node.List == lru) lru.Remove(node);
            entry.Command.Dispose();
            return;
        }
        for (int i = 0; i < entry.Command.Parameters.Count; i++)
        {
            entry.Command.Parameters[i].Value = DBNull.Value;
        }
    }

    private void EvictIfNeeded()
    {
        while (bySql.Count >= maxEntries)
        {
            var candidate = lru.Last;
            while (candidate is not null && candidate.Value.InUse)
                candidate = candidate.Previous;
            if (candidate is null) return;
            bySql.Remove(candidate.Value.Sql);
            lru.Remove(candidate);
            candidate.Value.Command.Dispose();
        }
    }

    private SqliteCommand CreateCommand(string sql, SqliteTransaction? transaction, ReadOnlySpan<(string Name, object? Value)> parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        for (int i = 0; i < parameters.Length; i++)
            command.Parameters.AddWithValue(parameters[i].Name, Normalize(parameters[i].Value));
        return command;
    }

    private static void BindCached(Entry entry, SqliteTransaction? transaction, ReadOnlySpan<(string Name, object? Value)> parameters)
    {
        var command = entry.Command;
        command.Transaction = transaction;
        if (MatchesNames(entry.ParameterNames, parameters))
        {
            for (int i = 0; i < parameters.Length; i++)
                command.Parameters[i].Value = Normalize(parameters[i].Value);
            return;
        }
        command.Parameters.Clear();
        for (int i = 0; i < parameters.Length; i++)
            command.Parameters.AddWithValue(parameters[i].Name, Normalize(parameters[i].Value));
        entry.ParameterNames = CaptureNames(parameters);
    }

    private static bool MatchesNames(string[]? cached, ReadOnlySpan<(string Name, object? Value)> parameters)
    {
        if (cached is null) return parameters.Length == 0;
        if (cached.Length != parameters.Length) return false;
        for (int i = 0; i < cached.Length; i++)
            if (!string.Equals(cached[i], parameters[i].Name, StringComparison.Ordinal)) return false;
        return true;
    }

    private static string[]? CaptureNames(ReadOnlySpan<(string Name, object? Value)> parameters)
    {
        if (parameters.Length == 0) return null;
        var names = new string[parameters.Length];
        for (int i = 0; i < parameters.Length; i++) names[i] = parameters[i].Name;
        return names;
    }

    private static object Normalize(object? value) => value switch
    {
        null => DBNull.Value,
        bool flag => flag ? 1L : 0L,
        _ => value
    };

    private static bool IsCacheable(string sql)
    {
        if (sql.Length > 4096) return false;
        int start = 0;
        while (start < sql.Length && char.IsWhiteSpace(sql[start])) start++;
        int trimmedEnd = sql.Length;
        while (trimmedEnd > start && char.IsWhiteSpace(sql[trimmedEnd - 1])) trimmedEnd--;
        if (trimmedEnd <= start) return false;
        var span = sql.AsSpan(start, trimmedEnd - start);
        if (span.StartsWith("PRAGMA", StringComparison.OrdinalIgnoreCase) &&
            (span.Length == 6 || !char.IsAsciiLetterOrDigit(span[6]) && span[6] != '_'))
            return false;
        if (sql[trimmedEnd - 1] == ';') trimmedEnd--;
        bool inSingle = false, inDouble = false, inLineComment = false, inBlockComment = false;
        for (int i = start; i < trimmedEnd; i++)
        {
            char c = sql[i];
            char next = i + 1 < trimmedEnd ? sql[i + 1] : '\0';
            if (inLineComment) { if (c == '\n') inLineComment = false; continue; }
            if (inBlockComment) { if (c == '*' && next == '/') { inBlockComment = false; i++; } continue; }
            if (inSingle) { if (c == '\'' && next == '\'') { i++; continue; } if (c == '\'') inSingle = false; continue; }
            if (inDouble) { if (c == '"' && next == '"') { i++; continue; } if (c == '"') inDouble = false; continue; }
            if (c == '-' && next == '-') { inLineComment = true; i++; continue; }
            if (c == '/' && next == '*') { inBlockComment = true; i++; continue; }
            if (c == '\'') { inSingle = true; continue; }
            if (c == '"') { inDouble = true; continue; }
            if (c == ';') return false;
        }
        return true;
    }

    public void Dispose()
    {
        disposed = true;
        foreach (var entry in lru)
            entry.Command.Dispose();
        lru.Clear();
        bySql.Clear();
    }
}
