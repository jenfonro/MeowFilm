import Foundation

struct KeychainItemKey: Hashable, Sendable {
    let service: String
    let account: String

    init(service: String, identity: AccountIdentity) {
        self.service = service
        // Fixed UUID components and an encoded middle component cannot collide through
        // delimiters in a site key. No platform username/secret becomes a Keychain label.
        // Opaque protocol keys preserve their UTF-8 bytes, matching JS identity.
        let site = Data(identity.source.siteKey.utf8).base64EncodedString()
        account = "v1:\(identity.source.installationID.uuidString.lowercased()):\(site):\(identity.accountID.uuidString.lowercased())"
    }
}

enum KeychainDriverStatus: Equatable, Sendable {
    case success
    case notFound
    case duplicate
    case failure(CredentialStoreError)
}

struct KeychainReadResult: Sendable {
    let status: KeychainDriverStatus
    let data: Data?
}

/// Internal seam: tests inject a driver and never call SecItem functions.
protocol KeychainDriver: Sendable {
    func read(key: KeychainItemKey, accessibility: CredentialAccessibility) -> KeychainReadResult
    func update(key: KeychainItemKey, data: Data, accessibility: CredentialAccessibility) -> KeychainDriverStatus
    func add(key: KeychainItemKey, data: Data, accessibility: CredentialAccessibility) -> KeychainDriverStatus
    func delete(key: KeychainItemKey) -> KeychainDriverStatus
}

/// Uses data-protection Keychain without an access-group override or iCloud sync.
/// Actual group access is determined by the signed app's entitlements; the product must
/// supply its own service namespace. Cross-app sharing is not configured or validated.
/// Initialization is passive; errors never select memory/preferences/file storage.
/// Lock serializes the bounded update/add retry within this instance.
public final class KeychainCredentialStore: CredentialStore, @unchecked Sendable {
    public let accessibility: CredentialAccessibility
    private let service: String
    private let driver: any KeychainDriver
    private let lock = NSLock()

    public init(
        service: String,
        accessibility: CredentialAccessibility = .whenUnlockedThisDeviceOnly
    ) throws {
        try Self.validateService(service)
        self.service = service
        self.accessibility = accessibility
        #if os(iOS) || os(macOS)
        driver = SystemKeychainDriver()
        #else
        throw CredentialStoreError.unsupportedPlatform
        #endif
    }

    init(service: String, accessibility: CredentialAccessibility = .whenUnlockedThisDeviceOnly,
         driver: any KeychainDriver) throws {
        try Self.validateService(service)
        self.service = service
        self.accessibility = accessibility
        self.driver = driver
    }

    public func read(for identity: AccountIdentity) throws -> CredentialRecord? {
        lock.lock()
        defer { lock.unlock() }
        let result = driver.read(key: key(for: identity), accessibility: accessibility)
        switch result.status {
        case .notFound:
            return nil
        case .success:
            guard let data = result.data else { throw CredentialStoreError.corruptItem }
            return try CredentialRecordCodec.decode(data)
        case .duplicate:
            throw CredentialStoreError.conflict
        case .failure(let error):
            throw error
        }
    }

    public func write(_ record: CredentialRecord, for identity: AccountIdentity) throws {
        lock.lock()
        defer { lock.unlock() }
        let key = key(for: identity)
        let data = CredentialRecordCodec.encode(record)
        switch driver.update(key: key, data: data, accessibility: accessibility) {
        case .success: return
        case .failure(let error): throw error
        case .duplicate: throw CredentialStoreError.conflict
        case .notFound: break
        }
        switch driver.add(key: key, data: data, accessibility: accessibility) {
        case .success: return
        case .failure(let error): throw error
        case .notFound: throw CredentialStoreError.conflict
        case .duplicate:
            // One bounded retry if an item appeared between update and add.
            switch driver.update(key: key, data: data, accessibility: accessibility) {
            case .success: return
            case .failure(let error): throw error
            case .duplicate, .notFound: throw CredentialStoreError.conflict
            }
        }
    }

    public func remove(for identity: AccountIdentity) throws {
        lock.lock()
        defer { lock.unlock() }
        switch driver.delete(key: key(for: identity)) {
        case .success, .notFound: return
        case .failure(let error): throw error
        case .duplicate: throw CredentialStoreError.conflict
        }
    }

    private func key(for identity: AccountIdentity) -> KeychainItemKey {
        KeychainItemKey(service: service, identity: identity)
    }

    private static func validateService(_ service: String) throws {
        guard !service.isEmpty,
              service == service.trimmingCharacters(in: .whitespacesAndNewlines),
              service.rangeOfCharacter(from: .controlCharacters) == nil else {
            throw CredentialStoreError.invalidConfiguration
        }
    }
}
