import Foundation

/// Codable is used only to project small, flat typed rows in memory. Repositories
/// supply a closed column list; nested values and unknown fields are not stored.
public enum SQLiteRecord {
    public static func values<T: Encodable>(_ value: T, columns: [String]) throws -> [SQLiteValue] {
        let encoder = JSONEncoder(); encoder.dateEncodingStrategy = .secondsSince1970
        guard let fields = try JSONSerialization.jsonObject(with: encoder.encode(value)) as? [String: Any] else {
            throw SQLiteStorageError.invalidValue
        }
        return try columns.map { column in
            guard let value = fields[column], !(value is NSNull) else { return .null }
            if let text = value as? String { return .text(text) }
            guard let number = value as? NSNumber else { throw SQLiteStorageError.invalidValue }
            if CFGetTypeID(number) == CFBooleanGetTypeID() { return .bool(number.boolValue) }
            let double = number.doubleValue
            guard double.isFinite else { throw SQLiteStorageError.invalidValue }
            if double.rounded(.towardZero) == double, let integer = Int64(exactly: double) { return .integer(integer) }
            return .real(double)
        }
    }

    public static func decode<T: Decodable>(_ type: T.Type, row: SQLiteRow,
                                            columns: [String], booleans: Set<String> = []) throws -> T {
        var fields: [String: Any] = [:]
        for name in columns {
            switch row[name] {
            case .null: fields[name] = NSNull()
            case .text(let text): fields[name] = text
            case .integer(let value): fields[name] = booleans.contains(name) ? NSNumber(value: value != 0) : NSNumber(value: value)
            case .real(let value): fields[name] = value
            case .blob: throw SQLiteStorageError.invalidValue
            }
        }
        let decoder = JSONDecoder(); decoder.dateDecodingStrategy = .secondsSince1970
        return try decoder.decode(type, from: JSONSerialization.data(withJSONObject: fields))
    }
}
