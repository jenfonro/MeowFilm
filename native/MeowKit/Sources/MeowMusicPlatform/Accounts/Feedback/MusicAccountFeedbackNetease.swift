// SPDX-License-Identifier: GPL-3.0-only
import Foundation
import MeowAccounts
import MeowMusicFeature

protocol MusicNeteasePlaybackLogUploading: MusicAccountProvider {
    func uploadPlaybackLog(_ records: Data, credential: AccountCredential,
                           authorization: MusicPlaybackFeedbackAuthorization) async throws -> MusicPlaybackFeedbackReceipt
}

/// Only playback facts from the official UA file channel. No unrelated telemetry
/// or fabricated official device/page fields are part of this adapter.
enum MusicAccountFeedbackNeteaseWire {
    static let uploadURL = URL(string: "https://clientlog3.music.163.com/api/clientlog/encrypt/upload?multiupload=true")!

    static func records(_ value: MusicPlaybackFeedbackReport) throws -> Data {
        try records(nativeID: value.target.nativeID, source: value.target.source, sourceID: value.target.sourceID,
            algorithm: value.target.algorithm, start: value.event == .start, listened: value.listened,
            media: value.mediaListened, rate: value.rate, reason: value.reason, occurredAt: value.occurredAt,
            heartbeat: value.target.origin?.kind == .heartbeat)
    }
    static func records(nativeID: String, source: String, sourceID: String, algorithm: String, start: Bool,
                        listened: Double, media: Double, rate: Double,
                        reason: MusicPlaybackFeedbackReport.Reason?, occurredAt: TimeInterval,
                        heartbeat: Bool = false) throws -> Data {
        // Integer representation is a wire constraint, not a listening threshold.
        func integer(_ seconds: Double, scale: Double = 1) throws -> Int64 {
            let number = floor(seconds * scale)
            guard number.isFinite, number >= 0, number < Double(Int64.max) else { throw MusicServiceError.invalidRequest }
            return Int64(number)
        }
        guard let id = Int64(try MusicNeteaseWire.id(nativeID)) else { throw MusicServiceError.invalidRequest }
        var fields: [String: Any] = ["id": id, "type": "song",
            "_logTime": try integer(occurredAt, scale: 1000),
            "time": start ? 0 : try integer(media), "realtime": start ? 0 : try integer(listened)]
        if !source.isEmpty { fields["source"] = source }
        if !sourceID.isEmpty { fields["sourceId"] = sourceID }
        if !algorithm.isEmpty { fields["alg"] = algorithm }
        if heartbeat { fields["play_scene"] = "heartbeat" }
        if !start {
            guard let reason else { throw MusicServiceError.invalidRequest }
            fields["end"] = reason.rawValue
            fields["play_speed"] = rate
        }
        let json = try JSONSerialization.data(withJSONObject: fields, options: [.sortedKeys, .withoutEscapingSlashes])
        guard json.count <= 16_384 else { throw MusicServiceError.limit } // File/transport resource budget.
        let action = start ? "_plv" : "_pld"
        return Data("\(try integer(occurredAt))\u{1}\(action)\u{1}".utf8) + json + Data([10])
    }

    static func confirmed(_ response: MusicAccountHTTPResponse, filename: String) throws -> Bool {
        guard response.status == 200, response.data.count <= 65_536 else { return false }
        let root = try MusicQQWire.decode(response.data)
        guard try MusicNeteaseWire.code(root["code"]) == 200,
              let data = root["data"] as? [String: Any],
              let files = data["successfiles"] as? [String] else { return false }
        return files.contains(filename)
    }

    static func request(records: Data, cookies: [String: String], filename: String,
                        encode: (Data, Data) throws -> Data = MusicNeteasePlaybackLogCodec.encode) throws -> MusicAccountHTTPRequest {
        // Authentication exists only in this in-memory upload, never the outbox.
        let metadata = try MusicQQWire.json(["MUSIC_U": cookies["MUSIC_U"]!])
        let encoded = try encode(metadata, records)
        let boundary = "meowfilm-" + UUID().uuidString.lowercased()
        let prefix = "--\(boundary)\r\nContent-Disposition: form-data; name=\"file\"; filename=\"\(filename)\"\r\nContent-Type: multipart/form-data\r\n\r\n"
        let body = Data(prefix.utf8) + encoded + Data("\r\n--\(boundary)--\r\n".utf8)
        return .init(url: uploadURL, method: "POST",
            headers: ["Content-Type": "multipart/form-data; boundary=\(boundary)", "Cache-Control": "no-store",
                "Cookie": cookies.sorted { $0.key < $1.key }.map { "\($0.key)=\($0.value)" }.joined(separator: "; "),
                "User-Agent": "Meowfilm"],
            body: body, maximumBytes: 65_536)
    }
}

extension NeteaseMusicAccountAdapter: MusicAccountFeedbackProvider, MusicNeteasePlaybackLogUploading {
    func feedback(_ value: MusicPlaybackFeedbackReport, credential: AccountCredential,
                  session: MusicAccountFeedbackNativeSession?,
                  guard authorization: MusicPlaybackFeedbackAuthorization) async throws -> MusicAccountFeedbackNativeResult {
        try await authorization.check()
        guard value.target.platform == .netease else { throw MusicServiceError.targetChanged }
        return .init(receipt: try await uploadPlaybackLog(MusicAccountFeedbackNeteaseWire.records(value),
            credential: credential, authorization: authorization))
    }
    func uploadPlaybackLog(_ records: Data, credential: AccountCredential,
                           authorization: MusicPlaybackFeedbackAuthorization) async throws -> MusicPlaybackFeedbackReceipt {
        try await authorization.check()
        let context = try context(credential), own = epoch
        let filename = "play_" + UUID().uuidString.lowercased()
        let request = try MusicAccountFeedbackNeteaseWire.request(records: records,
            cookies: context.cookies, filename: filename)
        let response = try await MusicAccountFeedbackWire.send(request, transport: transport, authorization: authorization)
        try check(context, epoch: own)
        // Never import response cookies, rotate credentials or replay a bad ACK.
        return try MusicAccountFeedbackNeteaseWire.confirmed(response, filename: filename) ? .confirmed : .unconfirmed
    }
}
