import Foundation
import CryptoKit
import Darwin

/// Content-addressed deduplicating blob store (`blobs/<sha256>.<ext>`).
/// Preserves raw unredacted payloads per contract §4.3.
public final class DebugBlobStore: @unchecked Sendable {
    public let directory: URL
    private let fm = FileManager.default
    private let lock = NSLock()

    public init(sessionDirectory: URL) {
        self.directory = sessionDirectory.appendingPathComponent("blobs", isDirectory: true)
    }

    public static func sha256Hex(_ data: Data) -> String {
        let digest = SHA256.hash(data: data)
        return digest.map { String(format: "%02x", $0) }.joined()
    }

    @discardableResult
    public func write(data: Data, ext: String) -> (sha256: String, relativePath: String) {
        let cleanExt = ext.lowercased().filter { $0.isASCII && ($0.isLetter || $0.isNumber) }
        let suffix = cleanExt.isEmpty ? "bin" : cleanExt
        let hash = Self.sha256Hex(data)
        let fileName = "\(hash).\(suffix)"
        let relative = "blobs/\(fileName)"
        let target = directory.appendingPathComponent(fileName, isDirectory: false)

        lock.lock()
        defer { lock.unlock() }
        if fm.fileExists(atPath: target.path) {
            return (hash, relative)
        }
        try? fm.createDirectory(at: directory, withIntermediateDirectories: true)
        let staging = directory.appendingPathComponent(".\(fileName).\(UUID().uuidString).tmp", isDirectory: false)
        do {
            try data.write(to: staging, options: [.atomic])
            if Darwin.rename(staging.path, target.path) != 0 {
                try? fm.removeItem(at: staging)
            }
        } catch {
            try? fm.removeItem(at: staging)
        }
        return (hash, relative)
    }

    public func writeBinary(_ data: Data, ext: String, contentType: String) -> DebugJSONValue {
        let stored = write(data: data, ext: ext)
        return .object([
            "$blob": .string(stored.relativePath),
            "sha256": .string(stored.sha256),
            "bytes": .int(data.count),
            "contentType": .string(contentType)
        ])
    }

    public func foldData(
        _ value: DebugJSONValue,
        inlineLimit: Int,
        eventBytesLimit: Int
    ) -> DebugJSONValue {
        let folded = inlineLimit > 0 ? foldStrings(value, inlineLimit: inlineLimit) : value
        guard eventBytesLimit > 0 else { return folded }
        guard let encoded = try? folded.encode(), encoded.count > eventBytesLimit else {
            return folded
        }
        let stored = write(data: encoded, ext: "json")
        return .object([
            "$blob": .string(stored.relativePath),
            "sha256": .string(stored.sha256),
            "bytes": .int(encoded.count),
            "summary": summarize(folded)
        ])
    }

    private func foldStrings(_ value: DebugJSONValue, inlineLimit: Int) -> DebugJSONValue {
        switch value {
        case .string(let text):
            guard text.count > inlineLimit else { return value }
            let utf8 = Data(text.utf8)
            let stored = write(data: utf8, ext: "txt")
            let head = String(text.prefix(512))
            let tail = String(text.suffix(128))
            return .object([
                "$blob": .string(stored.relativePath),
                "sha256": .string(stored.sha256),
                "length": .int(text.count),
                "bytes": .int(utf8.count),
                "head": .string(head),
                "tail": .string(tail)
            ])
        case .array(let items):
            return .array(items.map { foldStrings($0, inlineLimit: inlineLimit) })
        case .object(let fields):
            if fields["$blob"] != nil { return value }
            var next: [String: DebugJSONValue] = [:]
            next.reserveCapacity(fields.count)
            for (k, v) in fields {
                next[k] = foldStrings(v, inlineLimit: inlineLimit)
            }
            return .object(next)
        default:
            return value
        }
    }

    private func summarize(_ value: DebugJSONValue) -> DebugJSONValue {
        switch value {
        case .object(let fields):
            let keys = fields.keys.sorted().map(DebugJSONValue.string)
            var counts: [String: DebugJSONValue] = [:]
            for (k, v) in fields {
                switch v {
                case .array(let arr): counts[k] = .int(arr.count)
                case .object(let obj): counts[k] = .int(obj.count)
                case .string(let str): counts[k] = .int(str.count)
                default: break
                }
            }
            return .object([
                "keys": .array(keys),
                "counts": .object(counts)
            ])
        case .array(let items):
            return .object([
                "keys": .array([]),
                "counts": .object(["items": .int(items.count)])
            ])
        default:
            return .object(["keys": .array([]), "counts": .object([:])])
        }
    }

    public static func readBlob(sessionDirectory: URL, sha256: String) -> (data: Data, contentType: String, relativePath: String)? {
        guard sha256.count == 64,
              sha256.allSatisfy({ ($0 >= "0" && $0 <= "9") || ($0 >= "a" && $0 <= "f") }) else {
            return nil
        }
        let blobsDir = sessionDirectory.appendingPathComponent("blobs", isDirectory: true)
        guard let entries = try? FileManager.default.contentsOfDirectory(atPath: blobsDir.path) else {
            return nil
        }
        let prefix = sha256 + "."
        guard let match = entries.sorted().first(where: { $0.hasPrefix(prefix) }) else {
            return nil
        }
        let fileURL = blobsDir.appendingPathComponent(match, isDirectory: false)
        guard let data = try? Data(contentsOf: fileURL) else { return nil }
        let ext = fileURL.pathExtension.lowercased()
        let contentType: String
        switch ext {
        case "txt", "log": contentType = "text/plain; charset=utf-8"
        case "json": contentType = "application/json; charset=utf-8"
        case "png": contentType = "image/png"
        case "jpg", "jpeg": contentType = "image/jpeg"
        case "webp": contentType = "image/webp"
        default: contentType = "application/octet-stream"
        }
        return (data, contentType, "blobs/\(match)")
    }
}
