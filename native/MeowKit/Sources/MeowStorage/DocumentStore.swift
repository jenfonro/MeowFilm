import Foundation
import MeowContracts

public enum StoreError: Error, Equatable {
    case invalidNamespace
    case invalidKey
    case revisionConflict
}

/// Each feature receives its own namespace. No module can enumerate another store.
public actor DocumentStore {
    private let root: URL
    private let encoder: JSONEncoder
    private let decoder = JSONDecoder()

    public init(baseURL: URL, namespace: String) throws {
        guard Self.validComponent(namespace) else { throw StoreError.invalidNamespace }
        self.root = baseURL.appendingPathComponent(namespace, isDirectory: true)
        encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
    }
    private static func validComponent(_ value: String) -> Bool {
        !value.isEmpty && value.count <= 100 &&
        value.unicodeScalars.allSatisfy { CharacterSet.alphanumerics.union(CharacterSet(charactersIn: "-_")).contains($0) }
    }
    private func location(_ key: String) throws -> URL {
        guard Self.validComponent(key) else { throw StoreError.invalidKey }
        return root.appendingPathComponent(key + ".json")
    }
    public func read<T: Decodable & Sendable>(_ type: T.Type, key: String) throws -> T? {
        let url = try location(key)
        guard FileManager.default.fileExists(atPath: url.path) else { return nil }
        return try decoder.decode(type, from: Data(contentsOf: url))
    }
    public func write<T: Encodable & Sendable>(_ value: T, key: String) throws {
        // Encode before touching the destination. Atomic replacement preserves the last good document.
        let bytes = try encoder.encode(value)
        try bytes.write(to: location(key), options: .atomic)
    }
    public func update<T: Codable & Sendable, Result: Sendable>(
        _ type: T.Type, key: String, defaultValue: T,
        transform: @Sendable (inout T) throws -> Result
    ) throws -> Result {
        // No suspension between read and commit: concurrent imports cannot overwrite each other.
        var value = try read(type, key: key) ?? defaultValue
        let result = try transform(&value)
        try write(value, key: key)
        return result
    }
    public func remove(key: String) throws {
        let url = try location(key)
        if FileManager.default.fileExists(atPath: url.path) {
            try FileManager.default.removeItem(at: url)
        }
    }
}
