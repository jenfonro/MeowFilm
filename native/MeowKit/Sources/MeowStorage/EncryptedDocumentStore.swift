import Foundation
import CryptoKit
import Darwin

/// v1 errors contain no plaintext, identifiers, filesystem URLs or underlying errors.
public enum EncryptedDocumentStoreError: Error, Equatable, Sendable {
    case invalidNamespace
    case invalidKey
    case invalidBaseURL
    case invalidLimits
    case unsafePath
    case documentTooLarge
    case namespaceFull
    case invalidEnvelope
    case unsupportedVersion
    case missingKey
    case keyProvider(EncryptedDocumentKeyError)
    case authenticationFailed
    case encryptionFailed
    case protectionFailed
    case storageFailure
    case revisionConflict
    case transactionFailed
}

/// Apple-only, complete-Data AES-256-GCM storage, not a subclass/fallback of DocumentStore.
/// v1 envelope: "MEOWEDOC" | 0x01 | nonce(12) | ciphertext | tag(16).
/// AAD: header | UInt16-BE(namespace UTF8 length) | namespace UTF8 |
///               UInt16-BE(document key UTF8 length) | document key UTF8.
/// Both identifiers are authenticated even when two providers return identical key bytes.
///
/// Product assembly owns one instance per namespace in a dedicated, existing, app-owned
/// directory (not caches/iCloud). All methods are synchronous; the lock covers update's
/// transform, which MUST NOT reenter this store. Independent writers/processes are not
/// supported. Same-process malicious filesystem mutation is not a security boundary.
/// Key material is local to each operation, never retained on this object. Swift Data
/// copies are not promised to be securely erased; callers own returned plaintext.
public final class EncryptedDocumentStore: @unchecked Sendable {
    public struct Limits: Equatable, Sendable {
        public let maximumDocumentBytes: Int
        /// Committed/enumerated encrypted bytes, including abandoned staging files.
        /// One in-flight replacement additionally needs at most maximumDocumentBytes + 37.
        public let maximumNamespaceBytes: Int
        public let maximumDocuments: Int

        public init(maximumDocumentBytes: Int = 8 * 1_024 * 1_024,
                    maximumNamespaceBytes: Int = 32 * 1_024 * 1_024,
                    maximumDocuments: Int = 128) {
            self.maximumDocumentBytes = maximumDocumentBytes
            self.maximumNamespaceBytes = maximumNamespaceBytes
            self.maximumDocuments = maximumDocuments
        }

        fileprivate func validate() throws {
            guard (1...64 * 1_024 * 1_024).contains(maximumDocumentBytes),
                  maximumNamespaceBytes >= maximumDocumentBytes + EncryptedDocumentFormat.overhead,
                  maximumNamespaceBytes <= 256 * 1_024 * 1_024,
                  (1...1_024).contains(maximumDocuments) else {
                throw EncryptedDocumentStoreError.invalidLimits
            }
        }
    }

    private let base: URL
    private let root: URL
    private let baseIdentity: FileIdentity
    private var rootIdentity: FileIdentity?
    private let namespace: String
    private let limits: Limits
    private let keyProvider: any EncryptedDocumentKeyProvider
    private let committer: any EncryptedDocumentCommitter
    private let lock = NSLock()

    /// Passive: validates directories/configuration, never creates a directory or key.
    public convenience init(baseURL: URL, namespace: String,
                            keyProvider: any EncryptedDocumentKeyProvider,
                            limits: Limits = Limits()) throws {
        try self.init(baseURL: baseURL, namespace: namespace, keyProvider: keyProvider,
                      limits: limits, committer: RenameEncryptedDocumentCommitter())
    }

    init(baseURL: URL, namespace: String, keyProvider: any EncryptedDocumentKeyProvider,
         limits: Limits = Limits(), committer: any EncryptedDocumentCommitter) throws {
        guard EncryptedDocumentFormat.validIdentifier(namespace) else {
            throw EncryptedDocumentStoreError.invalidNamespace
        }
        try limits.validate()
        guard baseURL.isFileURL, baseURL.host == nil || baseURL.host == "" || baseURL.host == "localhost",
              baseURL.query == nil, baseURL.fragment == nil, baseURL.user == nil, baseURL.port == nil,
              baseURL.password == nil, baseURL.path.hasPrefix("/"),
              !baseURL.path.utf8.contains(0), baseURL.path.utf8.count <= 768 else {
            throw EncryptedDocumentStoreError.invalidBaseURL
        }
        // Resolve trusted system ancestors such as /var -> /private/var once. Reject a
        // symlink at the supplied base itself, and pin the resulting directory identity.
        guard let supplied = try Self.metadata(baseURL), Self.isDirectory(supplied) else {
            throw EncryptedDocumentStoreError.unsafePath
        }
        let canonical = baseURL.resolvingSymlinksInPath().standardizedFileURL
        guard canonical.path != "/", canonical.path.utf8.count <= 768,
              let info = try Self.metadata(canonical), Self.isDirectory(info) else {
            throw EncryptedDocumentStoreError.invalidBaseURL
        }
        self.base = canonical
        self.baseIdentity = FileIdentity(info)
        self.root = canonical.appendingPathComponent("n-" + EncryptedDocumentFormat.digest(namespace),
                                                     isDirectory: true)
        self.namespace = namespace
        self.keyProvider = keyProvider
        self.limits = limits
        self.committer = committer
        self.rootIdentity = nil
        _ = try checkDirectories()
    }

    public func read(key: String) throws -> Data? {
        lock.lock()
        defer { lock.unlock() }
        return try readUnlocked(key: key)
    }

    public func write(_ data: Data, key: String) throws {
        lock.lock()
        defer { lock.unlock() }
        try writeUnlocked(data, key: key)
    }

    /// No suspension between read, transform and commit. Throwing transforms are
    /// sanitized and leave the file/key unchanged; caller errors may contain secrets.
    public func update<Result>(key: String, defaultValue: Data,
                               transform: (inout Data) throws -> Result) throws -> Result {
        lock.lock()
        defer { lock.unlock() }
        var data = try readUnlocked(key: key) ?? defaultValue
        let result: Result
        do { result = try transform(&data) }
        catch { throw EncryptedDocumentStoreError.transactionFailed }
        try writeUnlocked(data, key: key)
        return result
    }

    /// Missing documents are a no-op. Unreadable/corrupt documents are NEVER deleted.
    /// The namespace key is retained, including when its last document is removed.
    public func remove(key: String) throws {
        lock.lock()
        defer { lock.unlock() }
        let destination = try location(key)
        guard try checkDirectories(), let previous = try envelope(at: destination) else { return }
        try EncryptedDocumentFormat.validate(previous)
        let material = try requiredKey()
        _ = try decrypt(previous, material: material, key: key)
        try confirmKey(material)
        _ = try checkDirectories()
        guard try envelope(at: destination) == previous else {
            throw EncryptedDocumentStoreError.revisionConflict
        }
        guard Darwin.unlink(destination.path) == 0 else {
            throw EncryptedDocumentStoreError.storageFailure
        }
    }

    private func readUnlocked(key: String) throws -> Data? {
        let destination = try location(key)
        guard try checkDirectories(), let bytes = try envelope(at: destination) else { return nil }
        // Validate the format before consulting protected storage.
        try EncryptedDocumentFormat.validate(bytes)
        return try decrypt(bytes, material: requiredKey(), key: key)
    }

    private func writeUnlocked(_ data: Data, key: String) throws {
        let destination = try location(key)
        guard data.count <= limits.maximumDocumentBytes else {
            throw EncryptedDocumentStoreError.documentTooLarge
        }
        let exists = try checkDirectories()
        let previous: Data?
        if exists { previous = try envelope(at: destination) }
        else { previous = nil }
        if let previous { try EncryptedDocumentFormat.validate(previous) }
        let inventory = try namespaceInventory(exists: exists)
        let nextBytes = data.count + EncryptedDocumentFormat.overhead
        let retainedBytes = inventory.bytes - (previous?.count ?? 0)
        guard retainedBytes + nextBytes <= limits.maximumNamespaceBytes,
              inventory.count + (previous == nil ? 1 : 0) <= limits.maximumDocuments else {
            throw EncryptedDocumentStoreError.namespaceFull
        }

        let material: Data
        if let existing = try providerRead() {
            material = existing
        } else {
            // ANY file, including unknown/staged files, forbids automatic reprovisioning.
            guard inventory.count == 0, previous == nil else {
                throw EncryptedDocumentStoreError.missingKey
            }
            material = try providerCreate()
        }
        if let previous { _ = try decrypt(previous, material: material, key: key) }
        let sealed: Data
        do {
            let box = try AES.GCM.seal(data, using: SymmetricKey(data: material),
                                       authenticating: EncryptedDocumentFormat.aad(namespace: namespace, key: key))
            guard let combined = box.combined, combined.count == data.count + 28 else {
                throw EncryptedDocumentStoreError.encryptionFailed
            }
            sealed = EncryptedDocumentFormat.header + combined
        } catch {
            throw EncryptedDocumentStoreError.encryptionFailed
        }

        try prepareDirectory()
        let staged = root.appendingPathComponent("t-" + UUID().uuidString.lowercased() + ".stage")
        var committed = false
        var ownsStagedFile = false
        defer {
            // Only this operation's unique staging path; orphan ciphertext is never
            // interpreted as an empty namespace on a later write.
            if ownsStagedFile && !committed { try? FileManager.default.removeItem(at: staged) }
        }
        do {
            var options: Data.WritingOptions = [.withoutOverwriting]
            #if os(iOS)
            options.insert(.completeFileProtection)
            #endif
            // The only Data written to disk is the complete authenticated envelope.
            try sealed.write(to: staged, options: options)
            ownsStagedFile = true
        } catch { throw EncryptedDocumentStoreError.storageFailure }
        try protect(staged, directory: false)
        try syncStagedFile(staged)
        // Re-read, do not use a retained unlocked key as a lock-state bypass.
        try confirmKey(material)
        _ = try checkDirectories()
        guard try envelope(at: destination) == previous else {
            throw EncryptedDocumentStoreError.revisionConflict
        }
        do { try committer.commit(staged: staged, destination: destination) }
        catch { throw EncryptedDocumentStoreError.storageFailure }
        // No fallible work after rename: never report failure after destroying the old file.
        committed = true
    }

    private func decrypt(_ bytes: Data, material: Data, key: String) throws -> Data {
        try EncryptedDocumentFormat.validate(bytes)
        do {
            let box = try AES.GCM.SealedBox(combined: bytes.dropFirst(EncryptedDocumentFormat.header.count))
            return try AES.GCM.open(box, using: SymmetricKey(data: material),
                                    authenticating: EncryptedDocumentFormat.aad(namespace: namespace, key: key))
        } catch { throw EncryptedDocumentStoreError.authenticationFailed }
    }

    private func requiredKey() throws -> Data {
        guard let material = try providerRead() else { throw EncryptedDocumentStoreError.missingKey }
        return material
    }

    private func confirmKey(_ expected: Data) throws {
        guard try requiredKey() == expected else {
            throw EncryptedDocumentStoreError.authenticationFailed
        }
    }

    private func providerRead() throws -> Data? {
        do {
            guard let data = try keyProvider.readKey(namespace: namespace) else { return nil }
            guard data.count == 32 else { throw EncryptedDocumentKeyError.invalidKeyMaterial }
            return data
        } catch let error as EncryptedDocumentKeyError {
            throw EncryptedDocumentStoreError.keyProvider(error)
        } catch { throw EncryptedDocumentStoreError.keyProvider(.backendFailure) }
    }

    private func providerCreate() throws -> Data {
        do {
            let data = try keyProvider.createKeyIfAbsent(namespace: namespace)
            guard data.count == 32 else { throw EncryptedDocumentKeyError.invalidKeyMaterial }
            return data
        } catch let error as EncryptedDocumentKeyError {
            throw EncryptedDocumentStoreError.keyProvider(error)
        } catch { throw EncryptedDocumentStoreError.keyProvider(.backendFailure) }
    }

    private func location(_ key: String) throws -> URL {
        guard EncryptedDocumentFormat.validIdentifier(key) else {
            throw EncryptedDocumentStoreError.invalidKey
        }
        // Hash filenames so case-insensitive filesystems cannot alias distinct identifiers.
        return root.appendingPathComponent("d-" + EncryptedDocumentFormat.digest(key) + ".meowenc")
    }

    private struct FileIdentity: Equatable {
        let device: dev_t
        let inode: ino_t
        init(_ info: stat) { device = info.st_dev; inode = info.st_ino }
    }

    private static func metadata(_ url: URL) throws -> stat? {
        var info = stat()
        // lstat("link/") can traverse a final directory symlink; strip that slash.
        var path = url.path
        while path.count > 1 && path.hasSuffix("/") { path.removeLast() }
        if Darwin.lstat(path, &info) == 0 { return info }
        if errno == ENOENT { return nil }
        throw EncryptedDocumentStoreError.storageFailure
    }

    private static func isDirectory(_ info: stat) -> Bool {
        info.st_mode & mode_t(S_IFMT) == mode_t(S_IFDIR)
    }

    private static func isPrivateFile(_ info: stat) -> Bool {
        info.st_mode & mode_t(S_IFMT) == mode_t(S_IFREG) && info.st_nlink == 1
    }

    private func checkDirectories() throws -> Bool {
        guard base.resolvingSymlinksInPath().path == base.path,
              let info = try Self.metadata(base), Self.isDirectory(info),
              FileIdentity(info) == baseIdentity else {
            throw EncryptedDocumentStoreError.unsafePath
        }
        guard let info = try Self.metadata(root) else {
            guard rootIdentity == nil else { throw EncryptedDocumentStoreError.unsafePath }
            return false
        }
        guard Self.isDirectory(info),
              root.resolvingSymlinksInPath().path == root.path,
              rootIdentity == nil || rootIdentity == FileIdentity(info) else {
            throw EncryptedDocumentStoreError.unsafePath
        }
        rootIdentity = FileIdentity(info)
        return true
    }

    private func envelope(at url: URL) throws -> Data? {
        guard let pathInfo = try Self.metadata(url) else { return nil }
        guard Self.isPrivateFile(pathInfo) else { throw EncryptedDocumentStoreError.unsafePath }
        let fd = Darwin.open(url.path, O_RDONLY | O_NOFOLLOW | O_NONBLOCK | O_CLOEXEC)
        guard fd >= 0 else { throw EncryptedDocumentStoreError.storageFailure }
        defer { Darwin.close(fd) }
        var info = stat()
        guard fstat(fd, &info) == 0 else { throw EncryptedDocumentStoreError.storageFailure }
        guard Self.isPrivateFile(info), FileIdentity(info) == FileIdentity(pathInfo) else {
            throw EncryptedDocumentStoreError.unsafePath
        }
        guard info.st_size >= 0,
              info.st_size <= off_t(limits.maximumDocumentBytes + EncryptedDocumentFormat.overhead) else {
            throw EncryptedDocumentStoreError.documentTooLarge
        }
        // No mmap: bound allocation before reading, reject short reads or growth.
        let count = Int(info.st_size)
        var bytes = Data(count: count)
        var offset = 0
        while offset < count {
            let readCount = bytes.withUnsafeMutableBytes { buffer in
                Darwin.read(fd, buffer.baseAddress!.advanced(by: offset), count - offset)
            }
            if readCount < 0 && errno == EINTR { continue }
            guard readCount > 0 else { throw EncryptedDocumentStoreError.storageFailure }
            offset += readCount
        }
        var extra: UInt8 = 0
        var extraCount: Int
        repeat { extraCount = Darwin.read(fd, &extra, 1) } while extraCount < 0 && errno == EINTR
        guard extraCount == 0 else { throw EncryptedDocumentStoreError.storageFailure }
        return bytes
    }

    private func namespaceInventory(exists: Bool) throws -> (count: Int, bytes: Int) {
        guard exists else { return (0, 0) }
        // Enumerate lazily and stop at the budget, rather than allocating an unbounded
        // list of filenames. Hidden/abandoned files count; directory errors never skip.
        var enumerationFailed = false
        guard let entries = FileManager.default.enumerator(
            at: root, includingPropertiesForKeys: nil, options: [.skipsSubdirectoryDescendants],
            errorHandler: { _, _ in enumerationFailed = true; return false }
        ) else { throw EncryptedDocumentStoreError.storageFailure }
        var total = 0, count = 0
        while let next = entries.nextObject() {
            guard let entry = next as? URL else { throw EncryptedDocumentStoreError.storageFailure }
            count += 1
            guard count <= limits.maximumDocuments else { throw EncryptedDocumentStoreError.namespaceFull }
            guard let info = try Self.metadata(entry), Self.isPrivateFile(info) else {
                throw EncryptedDocumentStoreError.unsafePath
            }
            guard info.st_size >= 0, info.st_size <= off_t(limits.maximumNamespaceBytes) else {
                throw EncryptedDocumentStoreError.namespaceFull
            }
            total += Int(info.st_size)
            guard total <= limits.maximumNamespaceBytes else { throw EncryptedDocumentStoreError.namespaceFull }
        }
        guard !enumerationFailed else { throw EncryptedDocumentStoreError.storageFailure }
        return (count, total)
    }

    private func prepareDirectory() throws {
        if try checkDirectories() == false {
            var attributes: [FileAttributeKey: Any] = [.posixPermissions: 0o700]
            #if os(iOS)
            attributes[.protectionKey] = FileProtectionType.complete
            #endif
            do {
                try FileManager.default.createDirectory(at: root, withIntermediateDirectories: false,
                                                        attributes: attributes)
            } catch { throw EncryptedDocumentStoreError.storageFailure }
            _ = try checkDirectories()
        }
        try protect(root, directory: true)
    }

    private func protect(_ url: URL, directory: Bool) throws {
        do {
            var attributes: [FileAttributeKey: Any] = [.posixPermissions: directory ? 0o700 : 0o600]
            #if os(iOS)
            attributes[.protectionKey] = FileProtectionType.complete
            #endif
            try FileManager.default.setAttributes(attributes, ofItemAtPath: url.path)
            var target = url
            var values = URLResourceValues()
            values.isExcludedFromBackup = true
            try target.setResourceValues(values)
            target.removeAllCachedResourceValues()
            guard try target.resourceValues(forKeys: [.isExcludedFromBackupKey]).isExcludedFromBackup == true else {
                throw EncryptedDocumentStoreError.protectionFailed
            }
            #if os(iOS)
            let saved = try FileManager.default.attributesOfItem(atPath: url.path)[.protectionKey]
            guard NativeFileProtectionVerification.inspect(saved) != .rejected else {
                throw EncryptedDocumentStoreError.protectionFailed
            }
            #endif
        } catch { throw EncryptedDocumentStoreError.protectionFailed }
    }

    private func syncStagedFile(_ url: URL) throws {
        let fd = Darwin.open(url.path, O_RDWR | O_NOFOLLOW | O_NONBLOCK | O_CLOEXEC)
        guard fd >= 0 else { throw EncryptedDocumentStoreError.storageFailure }
        defer { Darwin.close(fd) }
        guard Darwin.fsync(fd) == 0 else { throw EncryptedDocumentStoreError.storageFailure }
    }
}

// A narrow commit seam permits deterministic I/O failures with real CryptoKit and files.
// Success MUST mean committed; throwing MUST mean destination unchanged.
protocol EncryptedDocumentCommitter: Sendable {
    func commit(staged: URL, destination: URL) throws
}

struct RenameEncryptedDocumentCommitter: EncryptedDocumentCommitter {
    func commit(staged: URL, destination: URL) throws {
        guard Darwin.rename(staged.path, destination.path) == 0 else {
            throw EncryptedDocumentStoreError.storageFailure
        }
    }
}

enum EncryptedDocumentFormat {
    static let magic = Data("MEOWEDOC".utf8)
    static let header = magic + Data([1])
    static let overhead = 9 + 12 + 16

    static func validIdentifier(_ value: String, maximumBytes: Int = 128) -> Bool {
        let bytes = value.utf8
        func alphanumeric(_ byte: UInt8) -> Bool {
            (48...57).contains(byte) || (65...90).contains(byte) || (97...122).contains(byte)
        }
        guard !bytes.isEmpty, bytes.count <= maximumBytes, let first = bytes.first,
              alphanumeric(first), !value.contains("..") else { return false }
        return bytes.allSatisfy { alphanumeric($0) || $0 == 45 || $0 == 46 || $0 == 95 }
    }

    static func digest(_ value: String) -> String {
        SHA256.hash(data: Data(value.utf8)).map { String(format: "%02x", $0) }.joined()
    }

    static func aad(namespace: String, key: String) -> Data {
        var result = header
        for value in [namespace, key] {
            let bytes = Data(value.utf8)
            result.append(UInt8(bytes.count >> 8))
            result.append(UInt8(bytes.count & 0xff))
            result.append(bytes)
        }
        return result
    }

    static func validate(_ bytes: Data) throws {
        guard bytes.count >= header.count, bytes.prefix(magic.count) == magic else {
            throw EncryptedDocumentStoreError.invalidEnvelope
        }
        guard bytes[bytes.startIndex + magic.count] == 1 else {
            throw EncryptedDocumentStoreError.unsupportedVersion
        }
        guard bytes.count >= overhead else { throw EncryptedDocumentStoreError.invalidEnvelope }
    }
}
