import Foundation
import MeowSQLite
import MeowDebug

public struct SQLiteDebugQueryResult: Sendable {
    public let db: String
    public let columns: [String]
    public let rows: [[String: DebugJSONValue]]
    public let truncated: Bool
    public let durationMs: Double

    public func toJSON() -> DebugJSONValue {
        .object([
            "db": .string(db),
            "columns": .array(columns.map(DebugJSONValue.string)),
            "rows": .array(rows.map(DebugJSONValue.object)),
            "rowCount": .int(rows.count),
            "truncated": .bool(truncated),
            "durationMs": .number((durationMs * 1000).rounded() / 1000)
        ])
    }
}

extension SQLiteConnection {
    func executeDebugReadOnlyQuery(
        dbName: String,
        sql: String,
        bindings: [SQLiteValue],
        maxRows: Int
    ) throws -> SQLiteDebugQueryResult {
        let trimmed = sql.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty, !trimmed.utf8.contains(0) else {
            throw DebugAPIError.invalidArgument("SQL statement must be non-empty")
        }

        let started = DispatchTime.now().uptimeNanoseconds
        let statement: OpaquePointer
        do {
            statement = try prepareUncached(trimmed, bindings)
        } catch {
            let errMsg = String(cString: sqlite3_errmsg(handle))
            throw DebugAPIError.invalidArgument("SQLite prepare failed: \(errMsg)")
        }
        defer { sqlite3_finalize(statement) }

        let colCount = Int(sqlite3_column_count(statement))
        let columns: [String] = (0..<colCount).map { i in
            sqlite3_column_name(statement, Int32(i)).map { String(cString: $0) } ?? "col_\(i)"
        }
        let cursor = SQLiteCursor(statement, columnCount: Int32(colCount))

        var rows: [[String: DebugJSONValue]] = []
        var truncated = false
        let limit = min(max(maxRows, 1), 5000)

        while true {
            let stepRC = sqlite3_step(statement)
            if stepRC == SQLITE_DONE { break }
            guard stepRC == SQLITE_ROW else {
                let errMsg = String(cString: sqlite3_errmsg(handle))
                throw DebugAPIError.invalidArgument("Read-only SQL query failed: \(errMsg)")
            }
            if rows.count >= limit {
                truncated = true
                break
            }
            var row: [String: DebugJSONValue] = [:]
            row.reserveCapacity(colCount)
            for i in 0..<colCount {
                let colName = columns[i]
                switch (try? cursor.value(Int32(i))) ?? .null {
                case .null: row[colName] = .null
                case .integer(let v): row[colName] = .integer(v)
                case .real(let v): row[colName] = .optional(v)
                case .text(let s): row[colName] = .string(s)
                case .blob(let d): row[colName] = .string(d.base64EncodedString())
                }
            }
            rows.append(row)
        }

        let durationMs = Double(DispatchTime.now().uptimeNanoseconds - started) / 1_000_000.0
        return SQLiteDebugQueryResult(db: dbName, columns: columns, rows: rows, truncated: truncated, durationMs: durationMs)
    }
}

extension SQLiteDatabase {
    public func debugReadOnlyQuery(
        sql: String,
        params: [DebugJSONValue] = [],
        maxRows: Int = 200
    ) async throws -> SQLiteDebugQueryResult {
        let bindings: [SQLiteValue] = try params.map { value in
            switch value {
            case .null: return .null
            case .bool(let b): return .bool(b)
            case .integer(let i): return .integer(i)
            case .number(let d): return .real(d)
            case .string(let s): return .text(s)
            default:
                throw DebugAPIError.invalidArgument("SQL parameters must be scalar JSON values")
            }
        }
        let dbName = role.rawValue
        do {
            let result = try await read { db in
                try db.executeDebugReadOnlyQuery(dbName: dbName, sql: sql, bindings: bindings, maxRows: maxRows)
            }
            DebugRuntime.log(
                .storage,
                sub: "debug.sql",
                event: "query.ok",
                durationMs: result.durationMs,
                data: .object([
                    "db": .string(dbName),
                    "sql": .string(sql),
                    "rowCount": .int(result.rows.count),
                    "truncated": .bool(result.truncated)
                ])
            )
            return result
        } catch let apiError as DebugAPIError {
            DebugRuntime.log(
                .storage,
                sub: "debug.sql",
                event: "query.rejected",
                level: .warn,
                data: .object(["db": .string(dbName), "sql": .string(sql)]),
                error: DebugErrorPayload(code: apiError.code.rawValue, type: "DebugAPIError", message: apiError.message)
            )
            throw apiError
        } catch {
            DebugRuntime.logError(.storage, sub: "debug.sql", event: "query.error", error: error,
                                  data: .object(["db": .string(dbName), "sql": .string(sql)]))
            throw error
        }
    }
}
