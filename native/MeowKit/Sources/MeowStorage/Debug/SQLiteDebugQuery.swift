import Foundation
import MeowSQLite
import MeowDebug

public struct SQLiteDebugQueryResult: Sendable {
    public let columns: [String]
    public let rows: [[DebugJSONValue]]
    public let truncated: Bool
    public let durationMs: Double

    public func toJSON() -> DebugJSONValue {
        .object([
            "columns": .array(columns.map(DebugJSONValue.string)),
            "rows": .array(rows.map(DebugJSONValue.array)),
            "truncated": .bool(truncated),
            "durationMs": .number((durationMs * 1000).rounded() / 1000)
        ])
    }
}

extension SQLiteConnection {
    func executeDebugReadOnlyQuery(
        sql: String,
        bindings: [SQLiteValue],
        maxRows: Int
    ) throws -> SQLiteDebugQueryResult {
        let trimmed = sql.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty, !trimmed.utf8.contains(0) else {
            throw DebugAPIError.invalidArgument("SQL statement must be non-empty")
        }
        guard Self.isReadOnlyLeadingVerb(trimmed) else {
            throw DebugAPIError.invalidArgument("Only read-only SELECT, PRAGMA, or EXPLAIN queries are permitted")
        }

        let started = DispatchTime.now().uptimeNanoseconds
        var statement: OpaquePointer?
        var trailingSQL = ""
        let rc: Int32 = trimmed.withCString { cSQL in
            var tail: UnsafePointer<CChar>?
            let prepRC = sqlite3_prepare_v2(handle, cSQL, -1, &statement, &tail)
            if let tail {
                trailingSQL = String(cString: tail).trimmingCharacters(in: .whitespacesAndNewlines)
            }
            return prepRC
        }
        guard rc == SQLITE_OK, let statement else {
            if let statement { sqlite3_finalize(statement) }
            let errMsg = String(cString: sqlite3_errmsg(handle))
            throw DebugAPIError.invalidArgument("SQLite prepare failed: \(errMsg)")
        }
        defer { sqlite3_finalize(statement) }

        if !trailingSQL.isEmpty && trailingSQL != ";" {
            throw DebugAPIError.invalidArgument("Multiple SQL statements are not allowed")
        }

        // SQLite engine-level read-only enforcement rejects any write statement or mutating PRAGMA.
        guard sqlite3_stmt_readonly(statement) != 0 else {
            throw DebugAPIError.invalidArgument("SQLite rejected statement as non-readonly")
        }

        guard Int(sqlite3_bind_parameter_count(statement)) == bindings.count else {
            throw DebugAPIError.invalidArgument("Parameter count mismatch")
        }
        let transient = unsafeBitCast(-1, to: sqlite3_destructor_type.self)
        for (idx, value) in bindings.enumerated() {
            let pos = Int32(idx + 1)
            let bindRC: Int32
            switch value {
            case .null: bindRC = sqlite3_bind_null(statement, pos)
            case .integer(let v): bindRC = sqlite3_bind_int64(statement, pos, v)
            case .real(let v):
                guard v.isFinite else { throw DebugAPIError.invalidArgument("Non-finite real parameter") }
                bindRC = sqlite3_bind_double(statement, pos, v)
            case .text(let s):
                bindRC = s.withCString { sqlite3_bind_text(statement, pos, $0, Int32(s.utf8.count), transient) }
            case .blob(let d):
                bindRC = d.isEmpty
                    ? sqlite3_bind_zeroblob(statement, pos, 0)
                    : d.withUnsafeBytes { sqlite3_bind_blob(statement, pos, $0.baseAddress, Int32(d.count), transient) }
            }
            guard bindRC == SQLITE_OK else {
                throw DebugAPIError.invalidArgument("SQLite bind failed at parameter \(pos)")
            }
        }

        let colCount = Int(sqlite3_column_count(statement))
        let columns: [String] = (0..<colCount).map { i in
            if let name = sqlite3_column_name(statement, Int32(i)) {
                return String(cString: name)
            }
            return "col_\(i)"
        }

        var rows: [[DebugJSONValue]] = []
        var truncated = false
        let limit = min(max(maxRows, 1), 2000)

        while true {
            let stepRC = sqlite3_step(statement)
            if stepRC == SQLITE_DONE { break }
            guard stepRC == SQLITE_ROW else {
                let errMsg = String(cString: sqlite3_errmsg(handle))
                throw DebugAPIError.internalError("SQLite step failed: \(errMsg)")
            }
            if rows.count >= limit {
                truncated = true
                break
            }
            var row: [DebugJSONValue] = []
            row.reserveCapacity(colCount)
            for i in 0..<colCount {
                let c = Int32(i)
                switch sqlite3_column_type(statement, c) {
                case SQLITE_NULL:
                    row.append(.null)
                case SQLITE_INTEGER:
                    row.append(.integer(sqlite3_column_int64(statement, c)))
                case SQLITE_FLOAT:
                    row.append(.optional(sqlite3_column_double(statement, c)))
                case SQLITE_TEXT:
                    let count = Int(sqlite3_column_bytes(statement, c))
                    if count == 0 {
                        row.append(.string(""))
                    } else if let ptr = sqlite3_column_text(statement, c) {
                        let data = Data(bytes: ptr, count: count)
                        row.append(.string(String(decoding: data, as: UTF8.self)))
                    } else {
                        row.append(.null)
                    }
                case SQLITE_BLOB:
                    let count = Int(sqlite3_column_bytes(statement, c))
                    if count == 0 {
                        row.append(.string("base64:"))
                    } else if let ptr = sqlite3_column_blob(statement, c) {
                        let data = Data(bytes: ptr, count: count)
                        row.append(.string("base64:" + data.base64EncodedString()))
                    } else {
                        row.append(.null)
                    }
                default:
                    row.append(.null)
                }
            }
            rows.append(row)
        }

        let durationMs = Double(DispatchTime.now().uptimeNanoseconds - started) / 1_000_000.0
        return SQLiteDebugQueryResult(columns: columns, rows: rows, truncated: truncated, durationMs: durationMs)
    }

    private static func isReadOnlyLeadingVerb(_ sql: String) -> Bool {
        var cursor = sql[...]
        while true {
            cursor = cursor.drop(while: { $0.isWhitespace })
            if cursor.hasPrefix("--") {
                if let nl = cursor.firstIndex(of: "\n") {
                    cursor = cursor[cursor.index(after: nl)...]
                } else {
                    return false
                }
            } else if cursor.hasPrefix("/*") {
                if let end = cursor.range(of: "*/") {
                    cursor = cursor[end.upperBound...]
                } else {
                    return false
                }
            } else {
                break
            }
        }
        let head = cursor.prefix(while: { $0.isASCII && $0.isLetter }).uppercased()
        if head == "SELECT" || head == "WITH" || head == "EXPLAIN" { return true }
        if head == "PRAGMA" {
            return isReadOnlyPragma(cursor.dropFirst(head.count))
        }
        return false
    }

    private static func isReadOnlyPragma(_ remainder: Substring) -> Bool {
        // Disallow assignment syntax (`PRAGMA key = val`).
        guard !remainder.contains("=") else { return false }
        let trimmed = remainder.drop(while: { $0.isWhitespace })
        guard !trimmed.isEmpty else { return false }

        // Extract optional schema prefix (`main.pragma_name`) and pragma identifier.
        var identCursor = trimmed
        let firstIdent = identCursor.prefix(while: { $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "_") })
        guard !firstIdent.isEmpty else { return false }
        identCursor = identCursor.dropFirst(firstIdent.count).drop(while: { $0.isWhitespace })

        let pragmaName: String
        if identCursor.first == "." {
            identCursor = identCursor.dropFirst().drop(while: { $0.isWhitespace })
            let secondIdent = identCursor.prefix(while: { $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "_") })
            guard !secondIdent.isEmpty else { return false }
            pragmaName = secondIdent.lowercased()
            identCursor = identCursor.dropFirst(secondIdent.count).drop(while: { $0.isWhitespace })
        } else {
            pragmaName = firstIdent.lowercased()
        }

        // Reject mutating/side-effect PRAGMAs even in argumentless form.
        let forbiddenMutatingPragmas: Set<String> = [
            "optimize", "shrink_memory", "wal_checkpoint", "incremental_vacuum", "analysis_limit"
        ]
        if forbiddenMutatingPragmas.contains(pragmaName) { return false }

        // Parenthesized PRAGMAs (`PRAGMA foo(bar)`) can mutate connection settings (e.g. `foreign_keys(0)`),
        // so allow parentheses only for strictly read-only metadata/check PRAGMAs.
        if identCursor.first == "(" || remainder.contains("(") {
            let allowedFunctionPragmas: Set<String> = [
                "table_info", "table_xinfo", "index_list", "index_info", "index_xinfo",
                "foreign_key_list", "foreign_key_check", "table_list", "integrity_check", "quick_check"
            ]
            return allowedFunctionPragmas.contains(pragmaName)
        }
        return true
    }
}

extension SQLiteDatabase {
    public func debugReadOnlyQuery(
        sql: String,
        params: [DebugJSONValue] = [],
        maxRows: Int = 500
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
        do {
            let result = try await read { db in
                try db.executeDebugReadOnlyQuery(sql: sql, bindings: bindings, maxRows: maxRows)
            }
            DebugRuntime.log(
                .storage,
                sub: "debug.sql",
                event: "query.ok",
                durationMs: result.durationMs,
                data: .object([
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
                data: .object(["sql": .string(sql)]),
                error: DebugErrorPayload(code: apiError.code.rawValue, type: "DebugAPIError", message: apiError.message)
            )
            throw apiError
        } catch {
            DebugRuntime.logError(.storage, sub: "debug.sql", event: "query.error", error: error, data: .object(["sql": .string(sql)]))
            throw error
        }
    }
}
