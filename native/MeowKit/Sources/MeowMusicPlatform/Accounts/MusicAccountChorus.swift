// SPDX-License-Identifier: GPL-3.0-only
// iWebPlus chorus-marker.js, src/kugou-media.ts and fixed official modules.
import Foundation
import MeowAccounts
import MeowMusicFeature
import MeowStorage

protocol MusicAccountChorusProvider: Sendable {
    func chorus(nativeID: String, credential: AccountCredential) async throws -> MusicChorusPoint?
}
enum MusicChorusWire {
    static func netease(_ root: [String: Any], id: String) throws -> MusicChorusPoint? {
        let rows = try MusicNeteaseWire.rows(root["chorus"] ?? root["data"] ?? [], maximum: 100)
        let matches = rows.filter { (try? MusicNeteaseWire.id($0["id"])) == id }
        guard matches.count == 1, let row = matches.first,
              let start = try? MusicQQWire.integer(row["startTime"], minimum: 1),
              let end = try? MusicQQWire.integer(row["endTime"], minimum: 1), end > start else { return nil }
        return .init(start: Double(start) / 1000, end: Double(end) / 1000)
    }
    static func kugou(_ root: [String: Any], hash: String) throws -> MusicChorusPoint? {
        guard try MusicKugouWire.integer(root["status"]) == 1,
              try MusicKugouWire.integer(root["error_code"] ?? 0) == 0,
              try MusicKugouWire.integer(root["errcode"] ?? 0) == 0 else {
            throw MusicServiceError.upstreamInvalid
        }
        if let error = root["error"], !(error is NSNull) {
            let falseFlag = (error as? NSNumber)?.doubleValue == 0 || (error as? String) == ""
            guard falseFlag else { throw MusicServiceError.upstreamInvalid }
        }
        let rows = try MusicKugouWire.rows(root["data"] ?? [], maximum: 100)
        let matches = rows.filter { (try? MusicKugouWire.hash($0["hash"])) == hash }
        guard matches.count == 1, let row = matches.first,
              let start = try? MusicKugouWire.integer(row["start_time"], minimum: 1),
              let end = try? MusicKugouWire.integer(row["end_time"], minimum: 1),
              let length = try? MusicKugouWire.integer(row["timelength"], minimum: 1),
              end > start, length == end - start else { return nil }
        return .init(start: Double(start) / 1000, end: Double(end) / 1000)
    }
}
extension NeteaseMusicAccountAdapter: MusicAccountChorusProvider {
    func chorus(nativeID: String, credential: AccountCredential) async throws -> MusicChorusPoint? {
        let id = try MusicNeteaseWire.id(nativeID), own = epoch
        let root = try await request("/api/song/chorus", data: ["ids": "[\"" + id + "\"]"],
                                     crypto: "weapi", context: context(credential))
        try check(own)
        return try MusicChorusWire.netease(root, id: id)
    }
}
extension KugouMusicAccountAdapter: MusicAccountChorusProvider {
    func chorus(nativeID: String, credential: AccountCredential) async throws -> MusicChorusPoint? {
        let hash = try MusicKugouWire.hash(nativeID), context = try context(credential)
        let data = String(decoding: try MusicKugouWire.json(["hash": hash]), as: UTF8.self)
        let root = try await request(.chorus, context: context, query: ["data": "[" + data + "]"])
        try check(context)
        return try MusicChorusWire.kugou(root, hash: hash)
    }
}

/// Disposable exact-recording metadata. No URL, source credentials, account
/// UID or trial offsets. A cache hit still requires a live local-scope fence.
struct MusicAccountChorusCache {
    struct Key: Codable, Equatable {
        let scope: String
        let platform: String
        let nativeID: String
        let duration: Double
    }
    struct Entry: Codable { let key: Key; let point: MusicChorusPoint; let expires: Date }
    private let store: DocumentStore?
    private var loaded = false
    private var entries: [Entry] = []
    init(store: DocumentStore?) { self.store = store }
    mutating func value(_ key: Key, actualDuration: Double, at now: Date) -> MusicChorusPoint? {
        load(at: now)
        return entries.first { $0.key == key &&
            $0.point.fits(recordingDuration: key.duration, actualDuration: actualDuration) }?.point
    }
    mutating func put(_ point: MusicChorusPoint, key: Key, at now: Date) {
        load(at: now)
        entries.removeAll { $0.key == key }
        entries.append(.init(key: key, point: point, expires: now.addingTimeInterval(30 * 86_400)))
        entries = Array(entries.suffix(100))
        if let data = try? JSONEncoder().encode(entries), data.count <= 128 * 1024 {
            try? store?.write(data, key: "music.chorus")
        }
    }
    private mutating func load(at now: Date) {
        if !loaded {
            loaded = true
            if let data = try? store?.read(key: "music.chorus"), data.count <= 128 * 1024,
               let values = try? JSONDecoder().decode([Entry].self, from: data), values.count <= 100 {
                entries = values
            }
        }
        entries.removeAll { $0.expires <= now || $0.expires > now.addingTimeInterval(30 * 86_400) ||
            !$0.point.fits(recordingDuration: $0.key.duration, actualDuration: $0.key.duration) }
    }
}
