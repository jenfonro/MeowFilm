#if os(iOS) || os(macOS)
import Foundation
import Security
import LocalAuthentication

/// An item from an earlier release with the stricter when-unlocked class is adopted in
/// place when the store is configured for after-first-unlock. Every other mismatch,
/// including a weaker stored class, is still an accessibility mismatch.
enum KeychainAccessibilityAdmission: Equatable {
    case current
    case adopt
    case rejected

    static func classify(stored: String?, configured: CFString) -> Self {
        if stored == (configured as String) { return .current }
        if (configured as String) == (kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly as String),
           stored == (kSecAttrAccessibleWhenUnlockedThisDeviceOnly as String) { return .adopt }
        return .rejected
    }
}

/// Security/LocalAuthentication are confined to this adapter. No calls at initialization.
/// SDK declarations checked against Apple's documentation; runtime behavior still needs
/// a signed app/device test, particularly macOS data-protection Keychain entitlements.
struct SystemKeychainDriver: KeychainDriver {
    func read(key: KeychainItemKey, accessibility: CredentialAccessibility) -> KeychainReadResult {
        var query = baseQuery(key)
        query[kSecMatchLimit as String] = kSecMatchLimitOne
        query[kSecReturnData as String] = true
        query[kSecReturnAttributes as String] = true
        var result: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        guard status == errSecSuccess else {
            return KeychainReadResult(status: mapped(status), data: nil)
        }
        guard let attributes = result as? [String: Any],
              let data = attributes[kSecValueData as String] as? Data else {
            return KeychainReadResult(status: .failure(.corruptItem), data: nil)
        }
        let configured = accessibleValue(accessibility)
        switch KeychainAccessibilityAdmission.classify(stored: attributes[kSecAttrAccessible as String] as? String,
                                                       configured: configured) {
        case .current: break
        case .adopt:
            // The read succeeded, so the device is unlocked. Best effort: a failed
            // update keeps the stricter class and is retried by the next read.
            let change: [String: Any] = [kSecAttrAccessible as String: configured]
            _ = SecItemUpdate(baseQuery(key) as CFDictionary, change as CFDictionary)
        case .rejected:
            return KeychainReadResult(status: .failure(.accessibilityMismatch), data: nil)
        }
        return KeychainReadResult(status: .success, data: data)
    }

    func update(key: KeychainItemKey, data: Data, accessibility: CredentialAccessibility) -> KeychainDriverStatus {
        let changes: [String: Any] = [
            kSecValueData as String: data,
            kSecAttrAccessible as String: accessibleValue(accessibility)
        ]
        return mapped(SecItemUpdate(baseQuery(key) as CFDictionary, changes as CFDictionary))
    }

    func add(key: KeychainItemKey, data: Data, accessibility: CredentialAccessibility) -> KeychainDriverStatus {
        var query = baseQuery(key)
        query[kSecValueData as String] = data
        query[kSecAttrAccessible as String] = accessibleValue(accessibility)
        return mapped(SecItemAdd(query as CFDictionary, nil))
    }

    func delete(key: KeychainItemKey) -> KeychainDriverStatus {
        mapped(SecItemDelete(baseQuery(key) as CFDictionary))
    }

    private func baseQuery(_ key: KeychainItemKey) -> [String: Any] {
        let context = LAContext()
        // Do not show authentication dialogs during background operations or tests.
        // Interaction required by a protected/locked item is an explicit failure.
        context.interactionNotAllowed = true
        return [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: key.service,
            kSecAttrAccount as String: key.account,
            kSecAttrSynchronizable as String: false,
            kSecUseDataProtectionKeychain as String: true,
            kSecUseAuthenticationContext as String: context
        ]
    }

    private func accessibleValue(_ accessibility: CredentialAccessibility) -> CFString {
        switch accessibility {
        case .whenUnlockedThisDeviceOnly: return kSecAttrAccessibleWhenUnlockedThisDeviceOnly
        case .afterFirstUnlockThisDeviceOnly: return kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
        }
    }

    private func mapped(_ status: OSStatus) -> KeychainDriverStatus {
        switch status {
        case errSecSuccess: return .success
        case errSecItemNotFound: return .notFound
        case errSecDuplicateItem: return .duplicate
        case errSecInteractionNotAllowed: return .failure(.interactionNotAllowed)
        case errSecAuthFailed, errSecUserCanceled: return .failure(.accessDenied)
        case errSecMissingEntitlement: return .failure(.missingEntitlement)
        case errSecNotAvailable: return .failure(.unavailable)
        default: return .failure(.systemStatus(status))
        }
    }
}
#endif
