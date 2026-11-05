import Foundation
import Security
import LocalAuthentication
import Darwin
import MeowMusicFeature
import MeowMusicPlatform

/// App-only destructive operation. A durable request is separate from execution:
/// only the next cold launch, before constructing any business owner, may erase.
final class MeowfilmUserDataReset: @unchecked Sendable {
    enum Failure: String, Error, LocalizedError {
        case request, files, keychainLocked, keychainPermission, keychain, preferences, cookies

        var errorDescription: String? {
            switch self {
            case .request: return "无法记录清除请求，尚未开始清除"
            case .files: return "本机文件清理未完成"
            case .keychainLocked: return "请解锁设备后重试清除"
            case .keychainPermission: return "当前安装缺少钥匙串访问权限，请检查签名"
            case .keychain: return "本机钥匙串清理未完成"
            case .preferences: return "偏好设置清理未完成"
            case .cookies: return "本机 Cookie 清理未完成"
            }
        }
    }

    // Includes keys left by the former encrypted ordinary-document stores.
    // Never enumerate/delete arbitrary services in the signed app's access group.
    static let keychainServices = [
        "music.accounts.v1",
        NativeMusicClientService.namespace(.netease),
        NativeMusicClientService.namespace(.kugou),
        MusicQQPublicPersistenceDocument.namespace,
        "music.library.v1", "music.playback-display.v1",
        "music.source-discovery.v1", "music.account-url-cache.v1"
    ].map { MeowfilmIdentity.bundleIdentifier + "." + $0 }

    private let support: URL
    private let directories: [URL]
    private let defaults: UserDefaults
    private let defaultsDomain: String
    private let deleteKeychain: @Sendable (String) throws -> Void
    private let clearNetworkState: @Sendable () throws -> Void
    private let lock = NSLock()
    private var marker: URL { support.appendingPathComponent("meowfilm-reset.pending") }

    static func live() throws -> MeowfilmUserDataReset {
        let fm = FileManager.default
        guard let support = fm.urls(for: .applicationSupportDirectory, in: .userDomainMask).first,
              let caches = fm.urls(for: .cachesDirectory, in: .userDomainMask).first,
              let domain = Bundle.main.bundleIdentifier else { throw Failure.files }
        return .init(support: support, caches: caches, temporary: fm.temporaryDirectory,
            defaults: .standard, defaultsDomain: domain,
            deleteKeychain: { service in
                let status = SecItemDelete(keychainQuery(service: service) as CFDictionary)
                switch status {
                case errSecSuccess, errSecItemNotFound: return
                case errSecInteractionNotAllowed: throw Failure.keychainLocked
                case errSecMissingEntitlement, errSecAuthFailed, errSecUserCanceled:
                    throw Failure.keychainPermission
                default: throw Failure.keychain
                }
            }, clearNetworkState: {
                let cookies = HTTPCookieStorage.shared
                for cookie in cookies.cookies ?? [] { cookies.deleteCookie(cookie) }
                guard cookies.cookies?.isEmpty != false else { throw Failure.cookies }
                URLCache.shared.removeAllCachedResponses()
            })
    }

    /// Exact service, local data-protection Keychain only. Deletion does not
    /// read/decode the item or require its obsolete accessibility policy to match.
    static func keychainQuery(service: String) -> [String: Any] {
        let context = LAContext()
        context.interactionNotAllowed = true
        return [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrSynchronizable as String: false,
            kSecUseDataProtectionKeychain as String: true,
            kSecUseAuthenticationContext as String: context
        ]
    }

    init(support: URL, caches: URL, temporary: URL, defaults: UserDefaults,
         defaultsDomain: String,
         deleteKeychain: @escaping @Sendable (String) throws -> Void,
         clearNetworkState: @escaping @Sendable () throws -> Void) {
        self.support = support
        directories = [
            support.appendingPathComponent("meowfilm", isDirectory: true),
            caches.appendingPathComponent("meowfilm", isDirectory: true),
            caches.appendingPathComponent("meowfilm.music.audio-cache.v1", isDirectory: true),
            temporary.appendingPathComponent("meowfilm", isDirectory: true)
        ]
        self.defaults = defaults; self.defaultsDomain = defaultsDomain
        self.deleteKeychain = deleteKeychain; self.clearNetworkState = clearNetworkState
    }

    func request() throws {
        lock.lock(); defer { lock.unlock() }
        let staged = support.appendingPathComponent("meowfilm-reset-" + UUID().uuidString + ".stage")
        var ownsStage = false
        defer { if ownsStage { try? FileManager.default.removeItem(at: staged) } }
        do {
            try FileManager.default.createDirectory(at: support, withIntermediateDirectories: true)
            try requireDirectory(support)
            ownsStage = true
            try Data([1]).write(to: staged, options: [.withoutOverwriting, .completeFileProtectionUntilFirstUserAuthentication])
            var url = staged
            var values = URLResourceValues(); values.isExcludedFromBackup = true
            try url.setResourceValues(values)
            let handle = try FileHandle(forWritingTo: staged)
            defer { try? handle.close() }
            try handle.synchronize()
            guard Darwin.rename(staged.path, marker.path) == 0 else { throw Failure.request }
            // No fallible operation after the request becomes visible.
        } catch { throw Failure.request }
    }

    /// No old business owners exist here. Failure leaves the marker intact;
    /// repeating after a partial erase is safe and never restores old data.
    func completePendingReset() throws {
        lock.lock(); defer { lock.unlock() }
        do {
            guard try attributes(marker) != nil else { return }
            try requireDirectory(support)
            for directory in directories {
                let parent = directory.deletingLastPathComponent()
                if try attributes(parent) != nil { try requireDirectory(parent) }
                if try attributes(directory) != nil {
                    // removeItem unlinks symbolic links; it never follows their targets.
                    try FileManager.default.removeItem(at: directory)
                }
            }
        } catch { throw Failure.files }
        for service in Self.keychainServices { try deleteKeychain(service) }
        defaults.removePersistentDomain(forName: defaultsDomain)
        // Clearing must reach the persistent domain before removing the restart fence.
        guard defaults.synchronize(),
              defaults.persistentDomain(forName: defaultsDomain)?.isEmpty != false else {
            throw Failure.preferences
        }
        try clearNetworkState()
        do { try FileManager.default.removeItem(at: marker) }
        catch { throw Failure.files }
    }

    private func attributes(_ url: URL) throws -> [FileAttributeKey: Any]? {
        do { return try FileManager.default.attributesOfItem(atPath: url.path) }
        catch let error as CocoaError where error.code == .fileReadNoSuchFile { return nil }
    }

    private func requireDirectory(_ url: URL) throws {
        guard try attributes(url)?[.type] as? FileAttributeType == .typeDirectory else {
            throw Failure.files
        }
    }
}
