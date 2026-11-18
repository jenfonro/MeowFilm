import Foundation
import Darwin

/// Closed errors without paths, identifiers or document bytes.
public enum DocumentStoreError: Error, Equatable, Sendable {
    case invalidNamespace
    case invalidKey
    case invalidBaseURL
    case unsafePath
    case documentTooLarge
    case revisionConflict
    case storageFailure
}

/// Ordinary local documents: the caller's exact bytes in `<base>/<namespace>/<key>.json`,
/// readable and editable outside the App. No encryption, integrity envelope, namespace
/// quota or inventory gate; JSON and business validity belong to the consumer.
///
/// Product assembly owns one instance per namespace under a dedicated, app-owned base
/// directory. Methods are synchronous and serialized by one lock. Independent writers or
/// processes are not supported; an external edit simply becomes the current content.
/// Same-process malicious filesystem mutation is not a security boundary.
public final class DocumentStore: @unchecked Sendable {
    private let files: OrdinaryFileDirectory
    private let maximumDocumentBytes: Int?
    private let lock = NSLock()

    /// Passive: validates the namespace and URL, never creates a directory.
    public init(baseURL: URL, namespace: String, maximumDocumentBytes: Int? = nil) throws {
        files = try OrdinaryFileDirectory(baseURL: baseURL, namespace: namespace)
        self.maximumDocumentBytes = maximumDocumentBytes
    }

    /// nil means absent. An existing empty file is an empty document.
    public func read(key: String) throws -> Data? {
        let name = try fileName(key)
        lock.lock(); defer { lock.unlock() }
        guard let directory = try files.openDirectory(creating: false) else { return nil }
        defer { Darwin.close(directory) }
        return try files.contents(name, in: directory, maximumBytes: maximumDocumentBytes)
    }

    public func write(_ data: Data, key: String) throws {
        let name = try fileName(key)
        if let maximumDocumentBytes, data.count > maximumDocumentBytes { throw DocumentStoreError.documentTooLarge }
        lock.lock(); defer { lock.unlock() }
        guard let directory = try files.openDirectory(creating: true) else { throw DocumentStoreError.storageFailure }
        defer { Darwin.close(directory) }
        try files.replace(name, with: data, in: directory)
    }

    /// Replaces the document only if its current content equals `expected` (nil means
    /// absent). This is a content comparison, not a monotonic revision.
    public func compareAndSwap(key: String, expected: Data?, replacement: Data) throws {
        let name = try fileName(key)
        if let maximumDocumentBytes, replacement.count > maximumDocumentBytes { throw DocumentStoreError.documentTooLarge }
        lock.lock(); defer { lock.unlock() }
        guard let directory = try files.openDirectory(creating: expected == nil) else {
            throw DocumentStoreError.revisionConflict
        }
        defer { Darwin.close(directory) }
        guard try files.contents(name, in: directory, maximumBytes: maximumDocumentBytes) == expected else { throw DocumentStoreError.revisionConflict }
        try files.replace(name, with: replacement, in: directory)
    }

    /// Missing documents are a no-op. Only this store's directory entry is removed;
    /// a linked target is never followed.
    public func remove(key: String) throws {
        let name = try fileName(key)
        lock.lock(); defer { lock.unlock() }
        guard let directory = try files.openDirectory(creating: false) else { return }
        defer { Darwin.close(directory) }
        guard unlinkat(directory, name, 0) == 0 || errno == ENOENT else { throw DocumentStoreError.storageFailure }
    }

    private func fileName(_ key: String) throws -> String {
        guard Self.validIdentifier(key) else { throw DocumentStoreError.invalidKey }
        return key + ".json"
    }

    /// Lowercase ASCII only, so a case-insensitive volume cannot alias two identifiers
    /// and a readable name cannot traverse, hide or collide with a staging file.
    static func validIdentifier(_ value: String) -> Bool {
        let bytes = Array(value.utf8)
        func alphanumeric(_ byte: UInt8) -> Bool { (48...57).contains(byte) || (97...122).contains(byte) }
        guard let first = bytes.first, bytes.count <= 128, alphanumeric(first), !value.contains("..") else {
            return false
        }
        return bytes.allSatisfy { alphanumeric($0) || $0 == 45 || $0 == 46 || $0 == 95 }
    }
}
