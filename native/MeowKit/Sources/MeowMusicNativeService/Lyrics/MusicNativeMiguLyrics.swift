// SPDX-License-Identifier: GPL-3.0-only
// Fixed meowmusic cc22a7e migu/{catalog,lyric}.go and LX ad95d509
// mg/{musicInfo,lyric,utils/mrc}.js (Apache-2.0). No script/runtime or account.
import Foundation
import MeowMusicFeature

enum MusicNativeMiguLyrics {
    static let maximum = MusicLyricCrypto.maximum
    static let resourceURL = "https://c.musicapp.migu.cn/MIGUM2.0/v1.0/content/resourceinfo.do?resourceType=2"
    static let fileHeaders = [
        "Referer": "https://app.c.nf.migu.cn/", "channel": "0146921",
        "User-Agent": "Mozilla/5.0 (Linux; Android 5.1.1; Nexus 6 Build/LYZ28E) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/59.0.3071.115 Mobile Safari/537.36"
    ]
    struct Identity {
        let songID: String
        let copyrightID: String?
        init(_ info: MJ) throws {
            let keys = ["songmid", "songId"].filter { !info[$0].id.isEmpty }
            let ids = try keys.map { try MusicNativeMiguSearch.nativeID(info[$0]) }
            try musicRequire(!ids.isEmpty && Set(ids).count == 1, .lyricIdentityMismatch)
            songID = ids[0]
            copyrightID = info["copyrightId"].id.isEmpty ? nil : try MusicNativeMiguSearch.nativeID(info["copyrightId"])
        }
    }
    struct Resource {
        let main: MusicNativeHTTPRequest
        let translated: MusicNativeHTTPRequest?
        let isMRC: Bool
    }
    static func resourceRequest(id: String) throws -> MusicNativeHTTPRequest {
        _ = try MusicNativeMiguSearch.nativeID(.string(id))
        return .init(url: URL(string: resourceURL)!, method: "POST", headers: [
            "User-Agent": MusicNativeMiguSearch.userAgent, "Referer": "https://m.music.migu.cn/",
            "channel": "0146921", "Content-Type": "application/x-www-form-urlencoded"
        ], body: Data(("resourceId=" + id).utf8), maximumBytes: maximum)
    }
    static func resource(_ root: MJ, identity: Identity) throws -> Resource {
        let codes = ["code", "returnCode"].filter(root.contains).map { root[$0].id }
        try musicRequire(!codes.isEmpty && codes.allSatisfy { $0 == "000000" }, .upstreamInvalid)
        let data = root["data"].isObject ? root["data"] : root, list = data["resource"]
        try musicRequire(list.isArray && list.array.count == 1, .lyricIdentityMismatch)
        let row = list.array[0]
        try musicRequire(row["songId"].id == identity.songID &&
            (identity.copyrightID == nil || row["copyrightId"].id == identity.copyrightID), .lyricIdentityMismatch)
        for key in ["mrcUrl", "mrcurl", "lrcUrl", "trcUrl"] {
            try musicRequire(row[key] == .null || row[key].isString, .upstreamInvalid)
        }
        let mrc = row["mrcUrl"].string.isEmpty ? row["mrcurl"].string : row["mrcUrl"].string
        try musicRequire(row["mrcUrl"].string.isEmpty || row["mrcurl"].string.isEmpty ||
            row["mrcUrl"].string == row["mrcurl"].string, .upstreamInvalid)
        let main = mrc.isEmpty ? row["lrcUrl"].string : mrc
        try musicRequire(!main.isEmpty, .notFound)
        // Caller/search/import URL fields are never read. Only this verified resource
        // supplies the two ephemeral file URLs; no retry or fallback after an MRC error.
        return .init(main: try fileRequest(main), translated: row["trcUrl"].string.isEmpty ? nil : try fileRequest(row["trcUrl"].string),
                     isMRC: !mrc.isEmpty)
    }
    static func fileRequest(_ value: String) throws -> MusicNativeHTTPRequest {
        try musicRequire(value.utf8.count <= 4096 && !value.utf8.contains { $0 <= 32 || $0 == 127 || $0 == 92 }, .upstreamInvalid)
        guard var url = URLComponents(string: value), url.scheme == "https" || url.scheme == "http",
              url.user == nil, url.password == nil, url.port == nil, url.fragment == nil,
              let host = url.host, ["d.musicapp.migu.cn", "tyqk.migu.cn"].contains(host),
              !url.percentEncodedPath.contains("%"), !url.path.contains("//"),
              !url.path.split(separator: "/").contains(where: { $0 == "." || $0 == ".." }),
              url.path.range(of: #"^/[A-Za-z0-9_./-]+\.(?:lrc|mrc|trc)$"#, options: .regularExpression) != nil else {
            throw MusicServiceError.upstreamInvalid
        }
        // Resource files may advertise HTTP in the old SDK. Use the same resource
        // over HTTPS, never an ATS exemption, downgrade, host substitution or proxy.
        url.scheme = "https"
        guard let target = url.url else { throw MusicServiceError.upstreamInvalid }
        return .init(url: target, headers: fileHeaders, maximumBytes: maximum)
    }
    static func permits(_ value: MusicNativeHTTPRequest) -> Bool {
        guard value.maximumBytes <= maximum else { return false }
        if value.url.absoluteString == resourceURL, value.method == "POST",
           let body = value.body, let text = String(data: body, encoding: .utf8), text.hasPrefix("resourceId="),
           let expected = try? resourceRequest(id: String(text.dropFirst("resourceId=".count))) {
            return value.headers == expected.headers && body == expected.body
        }
        guard value.url.scheme == "https", value.method == "GET", value.body == nil,
              let expected = try? fileRequest(value.url.absoluteString) else { return false }
        return MusicNativeSearchProtocol.matches(value, expected)
    }
    // Signed 64-bit corrected-block TEA, not UInt32 XXTEA. Wrapping additions
    // and arithmetic right shifts match fixed LX toLong and meowmusic int64.
    static func decrypt(_ data: Data) throws -> String {
        try musicRequire(data.count <= maximum, .limit)
        guard let raw = String(data: data, encoding: .ascii) else { throw MusicServiceError.invalidLyricResponse }
        let hex = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        try musicRequire(hex.utf8.count >= 32 && hex.utf8.count % 16 == 0, .invalidLyricResponse)
        let input = Array(hex.utf8)
        try musicRequire(input.allSatisfy { (48...57).contains($0) || (65...70).contains($0) || (97...102).contains($0) },
            .invalidLyricResponse)
        var values: [Int64] = []
        for i in stride(from: 0, to: input.count, by: 16) {
            if i % 4096 == 0 { try Task.checkCancellation() }
            guard let n = UInt64(String(decoding: input[i..<i+16], as: UTF8.self), radix: 16) else {
                throw MusicServiceError.invalidLyricResponse
            }
            values.append(Int64(bitPattern: n))
        }
        let key: [Int64] = [27303562373562475, 18014862372307051, 22799692160172081, 34058940340699235]
        let delta: Int64 = 2654435769
        var sum = Int64(6 + 52 / values.count) * delta, y = values[0]
        while sum != 0 {
            let e = (sum >> 2) & 3
            for p in stride(from: values.count - 1, through: 0, by: -1) {
                if p % 1024 == 0 { try Task.checkCancellation() }
                let z = values[(p + values.count - 1) % values.count]
                let a = (y ^ sum) &+ (z ^ key[(p & 3) ^ Int(e)])
                let b = ((z >> 5) ^ (y << 2)) &+ ((y >> 3) ^ (z << 4))
                values[p] = values[p] &- (a ^ b); y = values[p]
            }
            sum -= delta
        }
        var units: [UInt16] = []
        for value in values {
            let bits = UInt64(bitPattern: value)
            for shift in stride(from: 0, to: 64, by: 16) { units.append(UInt16(truncatingIfNeeded: bits >> shift)) }
        }
        while units.last == 0 { units.removeLast() }
        // String(decoding:) would silently replace malformed surrogates.
        var index = 0
        while index < units.count {
            let value = units[index]
            if (0xd800...0xdbff).contains(value) {
                try musicRequire(index + 1 < units.count && (0xdc00...0xdfff).contains(units[index + 1]), .invalidLyricResponse)
                index += 2
            } else {
                try musicRequire(value != 0 && !(0xdc00...0xdfff).contains(value), .invalidLyricResponse); index += 1
            }
        }
        let result = String(decoding: units, as: UTF16.self)
        try musicRequire(!result.isEmpty && result.utf8.count <= maximum, .invalidLyricResponse)
        return result
    }
    static func mrc(_ data: Data) throws -> String { try parseMRC(decrypt(data)) }
    static func parseMRC(_ text: String) throws -> String {
        var result: [String] = []
        for row in try MusicNativePlatformLyricText.rows(text) {
            try Task.checkCancellation()
            guard let head = MusicNativeLyricsParser.matches(#"^\[(\d{1,10}),(\d{1,10})\](.*)$"#, row).first else {
                try musicRequire(MusicNativeLyricsParser.matches(#"^\[\d+,"#, row).isEmpty, .invalidLyricResponse); continue
            }
            guard let start = Int(head[1]), let duration = Int(head[2]), start <= MusicNativePlatformLyricText.maximumTime,
                  duration <= MusicNativePlatformLyricText.maximumTime - start else { throw MusicServiceError.invalidLyricResponse }
            let body = head[3], ns = body as NSString, markers = MusicNativeLyricsParser.matches(#"\((\d+),(\d+)\)"#, body)
            if markers.isEmpty {
                result.append("[\(start),\(duration)]" + (try MusicNativePlatformLyricText.literal(body)))
            } else {
                try musicRequire(markers.count <= 4096 && NSMaxRange(markers.last!.range) == ns.length, .invalidLyricResponse)
                var from = 0, previous = start, words: [MusicNativePlatformLyricText.Word] = []
                for marker in markers {
                    guard let at = Int(marker[1]), let length = Int(marker[2]), at >= previous && at >= start,
                          at <= MusicNativePlatformLyricText.maximumTime && length <= MusicNativePlatformLyricText.maximumTime - at else {
                        throw MusicServiceError.invalidLyricResponse
                    }
                    let word = try MusicNativePlatformLyricText.literal(ns.substring(with: NSRange(location: from, length: marker.range.location - from)))
                    words.append(.init(start: at - start, duration: length, text: word)); previous = at; from = NSMaxRange(marker.range)
                }
                result.append(try MusicNativePlatformLyricText.wordLine(start: start, duration: duration, words: words))
            }
            try musicRequire(result.count <= 6000, .limit)
        }
        try musicRequire(!result.isEmpty, .invalidLyricResponse)
        let output = result.joined(separator: "\n")
        try musicRequire(output.utf8.count <= maximum, .limit)
        return output
    }
}

extension MusicNativePlatforms {
    func miguLyrics(info: MJ) async throws -> MusicNativeLyrics {
        let own = epoch, identity = try MusicNativeMiguLyrics.Identity(info)
        let root = try await request(MusicNativeMiguLyrics.resourceRequest(id: identity.songID))
        try Task.checkCancellation(); try musicRequire(own == epoch, .staleResult)
        let resource = try MusicNativeMiguLyrics.resource(root, identity: identity)
        let bytes = try await requestData(resource.main)
        try Task.checkCancellation(); try musicRequire(own == epoch, .staleResult)
        let text: String
        if resource.isMRC { text = try MusicNativeMiguLyrics.mrc(bytes) }
        else { text = try MusicNativePlatformLyricText.lrc(bytes) }
        var translation = ""
        if let translated = resource.translated {
            try Task.checkCancellation(); try musicRequire(own == epoch, .staleResult)
            let data = try await requestData(translated)
            try Task.checkCancellation(); try musicRequire(own == epoch, .staleResult)
            translation = try MusicNativePlatformLyricText.lrc(data, empty: true)
        }
        try Task.checkCancellation(); try musicRequire(own == epoch, .staleResult)
        return .init(text: text, translation: translation)
    }
}
