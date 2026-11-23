import Foundation

/// Original media bytes only. Metadata and eligibility belong to repositories.
public final class RawResourceDirectory: @unchecked Sendable {
    private let rootURL: URL

    public init(dataRoot: URL, namespace: String) throws {
        guard dataRoot.isFileURL, !dataRoot.path.isEmpty, !namespace.isEmpty else {
            throw SQLiteStorageError.invalidPath
        }
        let directory = dataRoot
            .appendingPathComponent("resources-v1", isDirectory: true)
            .appendingPathComponent(namespace, isDirectory: true)
        do {
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            #if os(iOS)
            var values = URLResourceValues()
            values.isExcludedFromBackup = true
            var target = directory
            try? target.setResourceValues(values)
            #endif
        } catch {
            throw SQLiteStorageError.io
        }
        rootURL = directory
    }

    private static func owned(_ name: String) -> Bool {
        let parts = name.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 2, ["bin", "part"].contains(String(parts[1])),
              let id = UUID(uuidString: String(parts[0])) else { return false }
        return id.uuidString.lowercased() == parts[0]
    }

    public func read(_ name: String, maximumBytes: Int) throws -> Data? {
        guard Self.owned(name), name.hasSuffix(".bin") else { throw SQLiteStorageError.invalidPath }
        let fileURL = rootURL.appendingPathComponent(name)
        guard FileManager.default.fileExists(atPath: fileURL.path) else { return nil }
        do {
            let attributes = try FileManager.default.attributesOfItem(atPath: fileURL.path)
            if let size = attributes[.size] as? NSNumber, size.intValue > maximumBytes {
                throw SQLiteStorageError.invalidValue
            }
            let data = try Data(contentsOf: fileURL)
            guard data.count <= maximumBytes else { throw SQLiteStorageError.invalidValue }
            return data
        } catch let error as SQLiteStorageError {
            throw error
        } catch {
            throw SQLiteStorageError.io
        }
    }

    public func write(_ data: Data) throws -> String {
        let finalName = UUID().uuidString.lowercased() + ".bin"
        do {
            try data.write(to: rootURL.appendingPathComponent(finalName), options: .atomic)
            return finalName
        } catch {
            throw SQLiteStorageError.io
        }
    }

    public func remove(_ name: String) throws {
        guard Self.owned(name) else { throw SQLiteStorageError.invalidPath }
        let fileURL = rootURL.appendingPathComponent(name)
        guard FileManager.default.fileExists(atPath: fileURL.path) else { return }
        do {
            try FileManager.default.removeItem(at: fileURL)
        } catch {
            throw SQLiteStorageError.io
        }
    }

    public func recover(referenced: Set<String>) throws {
        let names: [String]
        do {
            names = try FileManager.default.contentsOfDirectory(atPath: rootURL.path)
        } catch {
            throw SQLiteStorageError.io
        }
        for name in names where Self.owned(name) && !referenced.contains(name) {
            try? remove(name)
        }
    }
}
