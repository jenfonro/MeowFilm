import Foundation
import Darwin

/// App-only destructive operation. A durable request is separate from execution:
/// only the next cold launch, before constructing any business owner, may erase.
final class MeowfilmUserDataReset: @unchecked Sendable {
    enum Failure: String, Error, LocalizedError {
        case request, files, cookies

        var errorDescription: String? {
            switch self {
            case .request: return "无法记录清除请求，尚未开始清除"
            case .files: return "本机文件清理未完成"
            case .cookies: return "本机 Cookie 清理未完成"
            }
        }
    }

    private let support: URL
    private let directories: [URL]
    private let markerName: String
    private let clearNetworkState: @Sendable () throws -> Void
    private let lock = NSLock()
    private var marker: URL { support.appendingPathComponent(markerName) }

    static func live() throws -> MeowfilmUserDataReset {
        let fm = FileManager.default
        guard let support = fm.urls(for: .applicationSupportDirectory, in: .userDomainMask).first,
              support.isFileURL else { throw Failure.files }
        return .init(support: support, temporary: fm.temporaryDirectory,
            clearNetworkState: {
                let cookies = HTTPCookieStorage.shared
                for cookie in cookies.cookies ?? [] { cookies.deleteCookie(cookie) }
                guard cookies.cookies?.isEmpty != false else { throw Failure.cookies }
                URLCache.shared.removeAllCachedResponses()
            })
    }

    static func isolated(dataRoot: URL) throws -> MeowfilmUserDataReset {
        let normalized = dataRoot.standardizedFileURL
        try FileManager.default.createDirectory(at: normalized, withIntermediateDirectories: true)
        let markerParent = normalized.deletingLastPathComponent()
        return .init(
            support: markerParent,
            markerName: ".\(normalized.lastPathComponent)-reset.pending",
            directories: [normalized],
            clearNetworkState: {
                URLCache.shared.removeAllCachedResponses()
            }
        )
    }

    init(support: URL, temporary: URL,
         clearNetworkState: @escaping @Sendable () throws -> Void) {
        self.support = support
        self.markerName = "meowfilm-reset.pending"
        directories = [
            support.appendingPathComponent("meowfilm", isDirectory: true),
            temporary.appendingPathComponent("meowfilm", isDirectory: true)
        ]
        self.clearNetworkState = clearNetworkState
    }

    private init(support: URL, markerName: String, directories: [URL],
                 clearNetworkState: @escaping @Sendable () throws -> Void) {
        self.support = support
        self.markerName = markerName
        self.directories = directories
        self.clearNetworkState = clearNetworkState
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
