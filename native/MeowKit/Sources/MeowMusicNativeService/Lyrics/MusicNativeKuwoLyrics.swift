// SPDX-License-Identifier: GPL-3.0-only
// Protocol port: meowmusic cc22a7e kuwo/lyric.go and LX ad95d509 kw/lyric.js,
// kw/util.js (Apache-2.0). This is a public HTTP API, not an LX user script.
import Foundation
import zlib
import MeowMusicFeature

enum MusicNativeKuwoLyrics {
    static let maximum = MusicLyricCrypto.maximum
    static let userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124.0.0.0 Safari/537.36"
    static let wordPattern = #"<(-?\d+),(-?\d+)(?:,-?\d+)?>"#
    static func request(info: MJ) throws -> MusicNativeHTTPRequest {
        var keys = ["songmid", "musicId"].filter { !info[$0].id.isEmpty }
        if keys.isEmpty { keys = ["id"] }
        let ids = try keys.map { try nativeID(info[$0].id) }
        try musicRequire(!ids.isEmpty && Set(ids).count == 1, .lyricIdentityMismatch)
        return try request(id: ids[0])
    }
    private static func nativeID(_ value: String) throws -> String {
        // Fixed meowmusic accepts the old MUSIC_ alias without changing the
        // stored source key or merging another recording.
        let id = value.hasPrefix("MUSIC_") ? String(value.dropFirst(6)) : value
        return try MusicPlatformProtocol.numericID(.string(id))
    }
    static func request(id: String) throws -> MusicNativeHTTPRequest {
        let id = try nativeID(id)
        // Same fixed host/path as LX; HTTPS only. Never downgrade or add a proxy.
        var url = URLComponents(string: "https://mlyric.kuwo.cn/mobi.s")!
        url.queryItems = ["f": "web", "type": "lyric", "lrcx": "1", "rid": id, "encode": "utf8"]
            .sorted { $0.key < $1.key }.map { .init(name: $0.key, value: $0.value) }
        return .init(url: url.url!, headers: ["User-Agent": userAgent], maximumBytes: maximum)
    }
    static func permits(_ value: MusicNativeHTTPRequest) -> Bool {
        guard let query = MusicNativeSearchProtocol.query(value, host: "mlyric.kuwo.cn", path: "/mobi.s",
                keys: ["f", "type", "lrcx", "rid", "encode"]),
              let id = query["rid"], let expected = try? request(id: id) else { return false }
        return value.maximumBytes <= maximum && MusicNativeSearchProtocol.matches(value, expected)
    }
    static func decode(_ data: Data) throws -> MusicNativeLyrics {
        try musicRequire(data.count <= maximum, .limit)
        guard data.count > 10, String(data: data.prefix(10), encoding: .ascii)?.lowercased() == "tp=content",
              let split = data.range(of: Data([13, 10, 13, 10])), split.lowerBound >= 10, split.lowerBound <= 4096 else {
            throw MusicServiceError.invalidLyricResponse
        }
        let expanded = try inflateEnvelope(Data(data[split.upperBound...]))
        guard let text = String(data: expanded, encoding: .ascii) else { throw MusicServiceError.invalidLyricResponse }
        let base64 = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let encrypted = Data(base64Encoded: base64), encrypted.base64EncodedString() == base64 else {
            throw MusicServiceError.invalidLyricResponse
        }
        let key = Array("yeelion".utf8) // Public wire obfuscation key; not a credential.
        let plain = encrypted.enumerated().map { $0.element ^ key[$0.offset % key.count] }
        guard let lyric = String(bytes: plain, encoding: .utf8) else { throw MusicServiceError.invalidLyricResponse }
        let result = try parse(lyric)
        try Task.checkCancellation()
        return result
    }
    static func inflateEnvelope(_ input: Data) throws -> Data {
        try musicRequire(!input.isEmpty && input.count <= maximum, .invalidLyricResponse)
        var stream = z_stream()
        try musicRequire(inflateInit_(&stream, ZLIB_VERSION, Int32(MemoryLayout<z_stream>.size)) == Z_OK, .unavailable)
        defer { inflateEnd(&stream) }
        return try input.withUnsafeBytes { raw in
            stream.next_in = UnsafeMutablePointer<Bytef>(mutating: raw.bindMemory(to: Bytef.self).baseAddress)
            stream.avail_in = uInt(input.count)
            var output = Data()
            while true {
                try Task.checkCancellation()
                var chunk = [UInt8](repeating: 0, count: 4096)
                let status = chunk.withUnsafeMutableBytes { buffer -> Int32 in
                    stream.next_out = buffer.bindMemory(to: Bytef.self).baseAddress; stream.avail_out = 4096
                    return inflate(&stream, Z_NO_FLUSH)
                }
                let count = 4096 - Int(stream.avail_out)
                try musicRequire(output.count + count <= maximum, .limit)
                output.append(contentsOf: chunk.prefix(count))
                if status == Z_STREAM_END {
                    try musicRequire(stream.avail_in == 0, .invalidLyricResponse)
                    return output
                }
                try musicRequire(status == Z_OK && (count > 0 || stream.avail_in > 0), .invalidLyricResponse)
            }
        }
    }
    private struct Line { var at: String; var start: Int; let text: String }
    static func parse(_ text: String) throws -> MusicNativeLyrics {
        var lines: [Line] = [], translations: [Line] = [], seen: Set<String> = []
        var factor: Int64 = 1, factor2: Int64 = 1, offset = "", hasFactor = false, timedWords = false
        for row in try MusicNativePlatformLyricText.rows(text) {
            try Task.checkCancellation()
            if let tag = MusicNativeLyricsParser.matches(#"^\[kuwo:([0-7]{1,16})\]$"#, row).first {
                guard let n = Int64(tag[1], radix: 8), n / 10 > 0, n % 10 > 0, !hasFactor else {
                    throw MusicServiceError.invalidLyricResponse
                }
                factor = n / 10; factor2 = n % 10; hasFactor = true
            } else if row.hasPrefix("[kuwo:") { throw MusicServiceError.invalidLyricResponse }
            else if row.lowercased().hasPrefix("[offset:") {
                try musicRequire(offset.isEmpty, .invalidLyricResponse)
                offset = try MusicNativePlatformLyricText.offset(row)
            } else if let m = MusicNativeLyricsParser.matches(#"^\[(\d{1,3}):(\d{2}(?:\.\d{1,4})?)\](.*)$"#, row).first {
                let start = try MusicNativePlatformLyricText.clock(m[1], m[2])
                var at = m[1] + ":" + m[2]
                if MusicNativeLyricsParser.matches(#"\.\d\d$"#, at).count == 1 { at += "0" }
                let content = m[3].trimmingCharacters(in: .whitespaces)
                if seen.contains(at) {
                    if lines.count < 2 { continue }
                    var translated = lines.removeLast()
                    translated.at = lines[lines.count - 1].at; translated.start = lines[lines.count - 1].start
                    translations.append(translated)
                }
                seen.insert(at); lines.append(.init(at: at, start: start, text: content))
                timedWords = timedWords || !MusicNativeLyricsParser.matches(wordPattern, content).isEmpty
            }
        }
        try musicRequire(!lines.isEmpty && lines.count <= 6000 && translations.count <= 6000, .invalidLyricResponse)
        // Preserve LX's duplicate-timestamp translation extraction and ambiguity rule.
        try musicRequire(timedWords || !(Double(translations.count) > Double(lines.count) * 0.3 &&
            lines.count - translations.count > 6), .invalidLyricResponse)
        var output: [String] = []
        for line in lines {
            try Task.checkCancellation()
            let matches = MusicNativeLyricsParser.matches(wordPattern, line.text)
            if matches.isEmpty {
                let plain = try MusicNativePlatformLyricText.literal(line.text, entities: true)
                output.append(MusicNativePlatformLyricText.stamp(line.start) + plain); continue
            }
            try musicRequire(matches.count <= 4096 && matches[0].range.location == 0, .invalidLyricResponse)
            let ns = line.text as NSString
            var words: [MusicNativePlatformLyricText.Word] = []
            for (index, match) in matches.enumerated() {
                guard let a = Int32(match[1]), let b = Int32(match[2]) else { throw MusicServiceError.invalidLyricResponse }
                let start = Int(abs(Int64(a) + Int64(b)) / (factor * 2))
                let length = Int(abs(Int64(a) - Int64(b)) / (factor2 * 2))
                try musicRequire(start <= MusicNativePlatformLyricText.maximumTime && length <= MusicNativePlatformLyricText.maximumTime,
                    .invalidLyricResponse)
                // The fixed decoder shortens overlapping preceding words, not the new word.
                if let previous = words.last, start < previous.start + previous.duration {
                    words[words.count - 1].start = min(previous.start, start)
                    words[words.count - 1].duration = start - words[words.count - 1].start
                }
                let from = NSMaxRange(match.range), to = index + 1 < matches.count ? matches[index + 1].range.location : ns.length
                let plain = try MusicNativePlatformLyricText.literal(ns.substring(with: NSRange(location: from, length: to - from)), entities: true)
                words.append(.init(start: start, duration: length, text: plain))
            }
            output.append(try MusicNativePlatformLyricText.wordLine(start: line.start, words: words))
        }
        let translated = try translations.map { line -> String in
            let stripped = line.text.replacingOccurrences(of: wordPattern, with: "", options: .regularExpression)
            return MusicNativePlatformLyricText.stamp(line.start) + (try MusicNativePlatformLyricText.literal(stripped, entities: true))
        }.joined(separator: "\n")
        let lyric = offset + output.joined(separator: "\n"), translation = translated.isEmpty ? "" : offset + translated
        try musicRequire(lyric.utf8.count <= maximum && translation.utf8.count <= maximum, .limit)
        return .init(text: lyric, translation: translation)
    }
}

/// Internal conversion into the existing native YRC/LRC parser. No new DTO,
/// rendering engine or changes to the frozen search/candidate implementation.
enum MusicNativePlatformLyricText {
    static let maximumTime = 604_800_000
    struct Word { var start: Int; var duration: Int; let text: String }
    static func rows(_ text: String) throws -> [String] {
        try musicRequire(text.utf8.count <= MusicLyricCrypto.maximum, .limit)
        try musicRequire(!text.unicodeScalars.contains { ($0.value < 32 && ![9, 10, 13].contains($0.value)) || $0.value == 127 },
            .invalidLyricResponse)
        let value = text.hasPrefix("\u{feff}") ? String(text.dropFirst()) : text
        let rows = value.replacingOccurrences(of: "\r\n", with: "\n").replacingOccurrences(of: "\r", with: "\n")
            .components(separatedBy: "\n")
        try musicRequire(rows.count <= 12_016 && rows.allSatisfy { $0.utf8.count <= 16_384 }, .limit)
        return rows.map { $0.trimmingCharacters(in: .whitespaces) }
    }
    static func clock(_ minutes: String, _ seconds: String) throws -> Int {
        guard let minute = Int(minutes), let second = Double(seconds), (0..<60).contains(second) else {
            throw MusicServiceError.invalidLyricResponse
        }
        return minute * 60_000 + Int((second * 1000).rounded())
    }
    static func stamp(_ ms: Int) -> String { String(format: "[%02d:%02d.%03d]", ms / 60_000, ms / 1000 % 60, ms % 1000) }
    static func offset(_ value: String) throws -> String {
        guard let m = MusicNativeLyricsParser.matches(#"^\[offset:([+-]?\d{1,9})\]$"#, value, insensitive: true).first,
              let number = Int(m[1]), abs(number) <= maximumTime else { throw MusicServiceError.invalidLyricResponse }
        return "[offset:\(number)]\n"
    }
    static func literal(_ value: String, entities: Bool = false) throws -> String {
        let text = entities ? try MusicNativeKuwoSearch.text(.string(value), maximum: 16_384, empty: true) : value
        try musicRequire(text.utf8.count <= 16_384 &&
            MusicNativeLyricsParser.matches(#"[\r\n\x00]|\(\d+,\d+(?:,\d+)?\)|<-?\d+,|\[\d{1,3}:\d{2}|\[offset:"#, text).isEmpty,
            .invalidLyricResponse)
        return text
    }
    static func wordLine(start: Int, duration: Int? = nil, words: [Word]) throws -> String {
        try musicRequire(!words.isEmpty && words.count <= 4096 && (0...maximumTime).contains(start), .invalidLyricResponse)
        var lastEnd = 0, rendered = ""
        for word in words {
            try musicRequire(word.start >= 0 && word.duration >= 0 && word.start <= maximumTime &&
                word.duration <= maximumTime - word.start && start <= maximumTime - word.start - word.duration, .invalidLyricResponse)
            lastEnd = max(lastEnd, word.start + word.duration)
            rendered += "(\(start + word.start),\(word.duration),0)" + word.text
        }
        let length = duration ?? lastEnd
        try musicRequire(length >= lastEnd && length <= maximumTime - start, .invalidLyricResponse)
        return "[\(start),\(length)]" + rendered
    }
    static func lrc(_ bytes: Data, empty: Bool = false) throws -> String {
        guard let text = String(data: bytes, encoding: .utf8) else { throw MusicServiceError.invalidLyricResponse }
        let rows = try Self.rows(text)
        if empty && rows.allSatisfy(\.isEmpty) { return "" }
        var result: [String] = [], count = 0, hasOffset = false
        for row in rows {
            try Task.checkCancellation()
            if row.lowercased().hasPrefix("[offset:") {
                try musicRequire(!hasOffset, .invalidLyricResponse); hasOffset = true
                result.append(String(try offset(row).dropLast()))
            } else if let m = MusicNativeLyricsParser.matches(#"^((?:\[\d{1,3}:\d{2}(?:\.\d{1,4})?\])+)(.*)$"#, row).first {
                var prefix = ""
                for tag in MusicNativeLyricsParser.matches(#"\[(\d{1,3}):(\d{2}(?:\.\d{1,4})?)\]"#, m[1]) {
                    prefix += stamp(try clock(tag[1], tag[2])); count += 1
                }
                result.append(prefix + (try literal(m[2])))
            }
        }
        try musicRequire(count > 0 && count <= 6000, .invalidLyricResponse)
        let output = result.joined(separator: "\n")
        try musicRequire(output.utf8.count <= MusicLyricCrypto.maximum, .limit)
        return output
    }
}
