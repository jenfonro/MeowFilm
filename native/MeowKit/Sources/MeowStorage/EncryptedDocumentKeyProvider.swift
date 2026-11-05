import Foundation
import CryptoKit
import Security
import LocalAuthentication

/// v1: synchronous, thread-safe, local-only operations. No logging, UI or callbacks.
/// A namespace has one immutable 32-byte AES key. Reading MUST NOT create it.
/// Creation is insert-if-absent, never replacement; only the document owner may request
/// it after establishing that the namespace contains no files. No key rotation API.
/// Production providers must recheck protected storage on EVERY call, not cache keys.
public protocol EncryptedDocumentKeyProvider: Sendable {
    func readKey(namespace: String) throws -> Data?
    func createKeyIfAbsent(namespace: String) throws -> Data
}

/// Closed error vocabulary: no item values, service names, paths or underlying errors.
public enum EncryptedDocumentKeyError: Error, Equatable, Sendable {
    case invalidConfiguration
    case invalidNamespace
    case invalidKeyMaterial
    case locked
    case accessDenied
    case missingEntitlement
    case unavailable
    case policyMismatch
    case conflict
    case backendFailure
}

/// Apple adapter; deliberately independent of MeowAccounts.
/// No SecItem call at initialization, no access-group override, no synchronization.
/// Entitlements still determine the actual default access group; products must use a
/// dedicated stable service and verify signed-device behavior. No memory-erasure claim.
public final class KeychainEncryptedDocumentKeyProvider: EncryptedDocumentKeyProvider, @unchecked Sendable {
    private let service: String
    private let driver: any EncryptedDocumentKeychainDriver
    private let lock = NSLock()

    public convenience init(service: String) throws {
        try self.init(service: service, driver: SystemEncryptedDocumentKeychainDriver())
    }

    init(service: String, driver: any EncryptedDocumentKeychainDriver) throws {
        guard EncryptedDocumentFormat.validIdentifier(service, maximumBytes: 160) else {
            throw EncryptedDocumentKeyError.invalidConfiguration
        }
        self.service = service
        self.driver = driver
    }

    public func readKey(namespace: String) throws -> Data? {
        lock.lock()
        defer { lock.unlock() }
        return try read(account: account(namespace))
    }

    public func createKeyIfAbsent(namespace: String) throws -> Data {
        lock.lock()
        defer { lock.unlock() }
        let account = try account(namespace)
        if let existing = try read(account: account) { return existing }
        let candidate = SymmetricKey(size: .bits256).withUnsafeBytes { Data($0) }
        switch driver.add(service: service, account: account, data: candidate) {
        case .success, .duplicate:
            // Read back even after successful insertion: enforce current access policy
            // and use the winner of an insertion race, never overwrite that winner.
            guard let stored = try read(account: account) else {
                throw EncryptedDocumentKeyError.conflict
            }
            return stored
        case .notFound:
            throw EncryptedDocumentKeyError.conflict
        case .failure(let error):
            throw error
        }
    }

    private func account(_ namespace: String) throws -> String {
        guard EncryptedDocumentFormat.validIdentifier(namespace) else {
            throw EncryptedDocumentKeyError.invalidNamespace
        }
        return "meow-encrypted-documents-v1:" + EncryptedDocumentFormat.digest(namespace)
    }

    private func read(account: String) throws -> Data? {
        let result = driver.read(service: service, account: account)
        switch result.status {
        case .notFound: return nil
        case .success:
            guard let data = result.data, data.count == 32 else {
                throw EncryptedDocumentKeyError.invalidKeyMaterial
            }
            return data
        case .duplicate: throw EncryptedDocumentKeyError.conflict
        case .failure(let error): throw error
        }
    }
}

// Internal fault-injection seam; unit tests never execute the system driver.
enum EncryptedDocumentKeychainStatus: Equatable, Sendable {
    case success
    case notFound
    case duplicate
    case failure(EncryptedDocumentKeyError)
}

struct EncryptedDocumentKeychainReadResult: Sendable {
    let status: EncryptedDocumentKeychainStatus
    let data: Data?
}

protocol EncryptedDocumentKeychainDriver: Sendable {
    func read(service: String, account: String) -> EncryptedDocumentKeychainReadResult
    func add(service: String, account: String, data: Data) -> EncryptedDocumentKeychainStatus
}

struct SystemEncryptedDocumentKeychainDriver: EncryptedDocumentKeychainDriver {
    func read(service: String, account: String) -> EncryptedDocumentKeychainReadResult {
        var query = baseQuery(service: service, account: account)
        query[kSecMatchLimit as String] = kSecMatchLimitOne
        query[kSecReturnData as String] = true
        query[kSecReturnAttributes as String] = true
        var result: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        guard status == errSecSuccess else {
            return .init(status: mapped(status), data: nil)
        }
        guard let attributes = result as? [String: Any],
              let data = attributes[kSecValueData as String] as? Data else {
            return .init(status: .failure(.invalidKeyMaterial), data: nil)
        }
        guard (attributes[kSecAttrSynchronizable as String] as? Bool) != true else {
            return .init(status: .failure(.policyMismatch), data: nil)
        }
        guard Self.matchesAccessibility(attributes[kSecAttrAccessible as String] as? String) else {
            return .init(status: .failure(.policyMismatch), data: nil)
        }
        return .init(status: .success, data: data)
    }

    static func matchesAccessibility(_ stored: String?) -> Bool {
        stored == (kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly as String)
    }

    func add(service: String, account: String, data: Data) -> EncryptedDocumentKeychainStatus {
        var query = baseQuery(service: service, account: account)
        query[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
        query[kSecValueData as String] = data
        return mapped(SecItemAdd(query as CFDictionary, nil))
    }

    private func baseQuery(service: String, account: String) -> [String: Any] {
        // Fresh context for each operation; no reused authentication session/key cache.
        let context = LAContext()
        context.interactionNotAllowed = true
        return [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecAttrSynchronizable as String: false,
            kSecUseDataProtectionKeychain as String: true,
            kSecUseAuthenticationContext as String: context
        ]
    }

    private func mapped(_ status: OSStatus) -> EncryptedDocumentKeychainStatus {
        switch status {
        case errSecSuccess: return .success
        case errSecItemNotFound: return .notFound
        case errSecDuplicateItem: return .duplicate
        case errSecInteractionNotAllowed: return .failure(.locked)
        case errSecAuthFailed, errSecUserCanceled: return .failure(.accessDenied)
        case errSecMissingEntitlement: return .failure(.missingEntitlement)
        case errSecNotAvailable: return .failure(.unavailable)
        default: return .failure(.backendFailure)
        }
    }
}
