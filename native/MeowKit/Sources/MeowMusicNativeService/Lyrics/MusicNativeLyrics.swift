// SPDX-License-Identifier: Apache-2.0
// Native port of iWebPlus b900c259 lyric-timing.js / kugou-lyrics.js.
import Foundation
import MeowMusicFeature

enum MusicNativeLyricsParser {
    struct Match {
        let values: [String]
        let range: NSRange
        subscript(_ i: Int) -> String { i < values.count ? values[i] : "" }
    }
    static func matches(_ expression: String, _ value: String, insensitive: Bool = false) -> [Match] {
        guard let re = try? NSRegularExpression(pattern: expression, options: insensitive ? [.caseInsensitive] : []) else { return [] }
        let ns = value as NSString
        return re.matches(in: value, range: NSRange(location: 0, length: ns.length)).map { match in
            .init(values: (0..<match.numberOfRanges).map { match.range(at: $0).location == NSNotFound ? "" : ns.substring(with: match.range(at: $0)) }, range: match.range)
        }
    }
    static func timed(_ text: String) -> Bool { !matches(#"\[(?:\d{1,3}:\d{2}(?:\.\d{1,3})?|\d+,\d+)\]"#, text).isEmpty }
    static func clock(_ minute: String, _ second: String) -> Double { (Double(minute) ?? 0) * 60 + (Double(second) ?? 0) }
    static func parse(_ raw: MusicNativeLyrics, duration: Double = 0) throws -> [MusicLyricLine] {
        let value: MusicNativeLyrics
        switch raw.encoding {
        case .text: value = raw
        case .qqQRC:
            value = .init(text: try MusicLyricCrypto.qrc(raw.text), translation: try MusicLyricCrypto.qrc(raw.translation),
                          romanization: try MusicLyricCrypto.qrc(raw.romanization))
        case .kugouKRC: value = try kugou(MusicLyricCrypto.krc(raw.text))
        }
        try musicRequire(value.text.utf8.count <= MusicLyricCrypto.maximum && value.translation.utf8.count <= MusicLyricCrypto.maximum, .limit)
        let result = try lines(value.text, translation: value.translation, duration: duration)
        try musicRequire(result.count <= 6000 && result.reduce(0) { $0 + $1.words.count } <= 100_000, .limit)
        return result
    }
    static func xmlText(_ input: String) -> String {
        guard input.trimmingCharacters(in: .whitespacesAndNewlines).hasPrefix("<") else { return input }
        let attributes = matches(#"\bLyricContent\s*=\s*(["'])([\s\S]*?)\1"#, input)
        guard attributes.count == 1 else { return "" }
        let text = attributes[0][2], ns = text as NSString
        let entities = matches(#"&(amp|lt|gt|quot|apos|#\d+|#[xX][\da-fA-F]+);"#, text)
        var result = "", start = 0
        for entity in entities {
            result += ns.substring(with: NSRange(location: start, length: entity.range.location - start))
            let name = entity[1], value: String?
            if let literal = ["amp": "&", "lt": "<", "gt": ">", "quot": "\"", "apos": "'"][name] { value = literal }
            else {
                let hex = name.lowercased().hasPrefix("#x")
                let code = UInt32(name.dropFirst(hex ? 2 : 1), radix: hex ? 16 : 10) ?? 0
                value = code > 0 && code <= 0x10ffff && !(0xd800...0xdfff).contains(code) ? UnicodeScalar(code).map(String.init) : nil
            }
            result += value ?? entity[0]; start = NSMaxRange(entity.range)
        }
        result += ns.substring(from: start); return result
    }
    struct Word { var time: Double; var duration: Double?; let text: String }
    struct Line { var time: Double; var end: Double?; let text: String; var words: [Word]; let ordinal: Int }
    static func lines(_ raw: String, translation: String = "", duration: Double = 0) throws -> [MusicLyricLine] {
        let input = xmlText(raw)
        let offset = (matches(#"\[offset:([+-]?\d+)\]"#, input, insensitive: true).first.flatMap { Double($0[1]) } ?? 0) / 1000
        try musicRequire(offset.isFinite && abs(offset) <= 9_007_199_254_740, .invalidLyricResponse)
        var output: [Line] = []
        for row in input.components(separatedBy: .newlines) {
            let text = row.trimmingCharacters(in: .whitespacesAndNewlines)
            if let head = matches(#"^\[(\d+),(\d+)\](.*)$"#, text).first {
                let time = (Double(head[1]) ?? 0) / 1000, end = time + (Double(head[2]) ?? 0) / 1000, content = head[3]
                let qrc = matches(#"^\(\d+,\d+,\d+\)"#, content).isEmpty && !matches(#"\(\d+,\d+\)$"#, content).isEmpty
                let words: [Word]
                if qrc {
                    words = matches(#"(.*?)\((\d+),(\d+)\)"#, content).filter { !$0[1].isEmpty }.map {
                        .init(time: (Double($0[2]) ?? 0) / 1000 + offset, duration: (Double($0[3]) ?? 0) / 1000, text: $0[1])
                    }
                } else {
                    words = matches(#"\((\d+),(\d+),\d+\)(.*?)(?=\(\d+,\d+,\d+\)|$)"#, content).filter { !$0[3].isEmpty }.map {
                        .init(time: (Double($0[1]) ?? 0) / 1000 + offset, duration: (Double($0[2]) ?? 0) / 1000, text: $0[3])
                    }
                }
                let plain = words.isEmpty ? content.trimmingCharacters(in: .whitespaces) : words.map(\.text).joined()
                if !plain.isEmpty { output.append(.init(time: time + offset, end: end + offset, text: plain, words: words, ordinal: output.count)) }
            } else if let head = matches(#"^((?:\[\d{1,3}:\d{2}(?:\.\d{1,3})?\])+)(.*)$"#, text).first {
                let content = head[2].trimmingCharacters(in: .whitespaces), ns = head[2].trimmingCharacters(in: .whitespaces) as NSString
                if content.isEmpty { continue }
                for tag in matches(#"\[(\d{1,3}):(\d{2}(?:\.\d{1,3})?)\]"#, head[1]) {
                    let time = clock(tag[1], tag[2])
                    let markers = matches(#"(?:\[\[(\d{1,3}):(\d{2}(?:\.\d{1,3})?)\]\]|<(\d{1,3}):(\d{2}(?:\.\d{1,3})?)>)"#, content)
                    var words: [Word] = [], end: Double?
                    if let first = markers.first {
                        let before = ns.substring(to: first.range.location)
                        let relative = !first[1].isEmpty && clock(first[1], first[2]) < time - 1
                        let stamps = markers.map {
                            clock($0[1].isEmpty ? $0[3] : $0[1], $0[2].isEmpty ? $0[4] : $0[2]) + (relative ? time : 0) + offset
                        }
                        if !before.isEmpty {
                            words.append(.init(time: time + offset, duration: max(0, stamps[0] - time - offset), text: before))
                        }
                        for (i, marker) in markers.enumerated() {
                            let from = NSMaxRange(marker.range), to = i + 1 < markers.count ? markers[i + 1].range.location : ns.length
                            let word = ns.substring(with: NSRange(location: from, length: to - from))
                            if !word.isEmpty {
                                let length: Double? = i + 1 < stamps.count ? max(0, stamps[i + 1] - stamps[i]) : nil
                                words.append(.init(time: stamps[i], duration: length, text: word))
                            } else if i + 1 == markers.count { end = stamps[i] }
                        }
                    }
                    let plain = markers.isEmpty ? content : words.map(\.text).joined()
                    if !plain.isEmpty { output.append(.init(time: time + offset, end: end, text: plain, words: words, ordinal: output.count)) }
                }
            }
            try musicRequire(output.count <= 6000, .limit)
        }
        output.sort { $0.time == $1.time ? $0.ordinal < $1.ordinal : $0.time < $1.time }
        var translated: [Int64: [String]] = [:]
        if !translation.isEmpty {
            for line in try lines(translation) {
                let key = Int64((line.time * 1000).rounded())
                if !(translated[key] ?? []).contains(line.text) { translated[key, default: []].append(line.text) }
            }
        }
        var result: [MusicLyricLine] = []
        for (i, row) in output.enumerated() {
            let next = output.dropFirst(i + 1).first { $0.time > row.time }?.time
            let end = max(row.time, row.end ?? next ?? (duration > row.time ? duration : row.time))
            try musicRequire(row.time.isFinite && end.isFinite && abs(row.time) <= 9_007_199_254_740, .invalidLyricResponse)
            var words: [MusicLyricWord] = []
            for (j, word) in row.words.enumerated() {
                let boundary = j + 1 < row.words.count ? row.words[j + 1].time : row.end ?? next
                let length = word.duration ?? boundary.map { max(0, $0 - word.time) } ?? 0
                try musicRequire(word.time.isFinite && length.isFinite && length >= 0, .invalidLyricResponse)
                words.append(.init(time: word.time, duration: length, text: word.text))
            }
            let time = max(0, row.time)
            let texts = (translated[Int64((time * 1000).rounded())] ?? []).filter { $0 != row.text }
            result.append(.init(index: i, time: time, end: max(time, end), text: row.text, translation: texts.joined(separator: "\n"),
                words: words))
        }
        return result
    }
    static func kugou(_ krc: String) throws -> MusicNativeLyrics {
        var rows: [(start: Int, text: String, yrc: String)] = [], offsets: [String] = [], languages: [String] = []
        for raw in krc.components(separatedBy: .newlines) {
            let line = raw.trimmingCharacters(in: .whitespacesAndNewlines)
            if let offset = matches(#"^\[offset:([+-]?\d+)\]$"#, line, insensitive: true).first { offsets.append(offset[1]); continue }
            if let language = matches(#"^\[language:([A-Za-z0-9+/=]+)\]$"#, line).first { languages.append(language[1]); continue }
            guard let head = matches(#"^\[(\d+),(\d+)\](.*)$"#, line).first else {
                try musicRequire(matches(#"^\[\d+,"#, line).isEmpty, .invalidLyricResponse); continue
            }
            guard let start = Int(head[1]), let duration = Int(head[2]), start <= 9_007_199_254_740_991 - duration else { throw MusicServiceError.invalidLyricResponse }
            let words = matches(#"<(\d+),(\d+),(\d+)>(.*?)(?=<\d+,\d+,\d+>|$)"#, head[3])
            try musicRequire(words.first?.range.location == 0 || words.isEmpty && matches(#"<\d+,"#, head[3]).isEmpty, .invalidLyricResponse)
            var yrc = "", plain = ""
            for word in words {
                guard let relative = Int(word[1]), let length = Int(word[2]), start <= 9_007_199_254_740_991 - relative,
                      length <= 9_007_199_254_740_991, matches(#"\(\d+,\d+,\d+\)|<\d+,"#, word[4]).isEmpty else { throw MusicServiceError.invalidLyricResponse }
                yrc += "(\(start + relative),\(length),\(word[3]))" + word[4]; plain += word[4]
            }
            rows.append((start, words.isEmpty ? head[3] : plain, "[\(start),\(duration)]" + (words.isEmpty ? head[3] : yrc)))
            try musicRequire(rows.count <= 6000, .limit)
        }
        try musicRequire(offsets.allSatisfy {
            guard let value = Int($0) else { return false }
            return value >= -9_007_199_254_740_991 && value <= 9_007_199_254_740_991
        }, .invalidLyricResponse)
        let offset = Int(offsets.first ?? "0") ?? 0, prefix = offsets.isEmpty ? "" : "[offset:\(offset)]\n"
        var translation = ""
        if languages.count == 1, offsets.count <= 1, let bytes = Data(base64Encoded: languages[0]), let language = try? MJ.parse(bytes, maximum: MusicLyricCrypto.maximum),
           language["version"] == .n(1) {
            let tracks = language["content"].array.filter { $0["type"] == .n(1) }, translated = tracks.first?["lyricContent"].array ?? []
            if tracks.count == 1 && translated.count == rows.count &&
                translated.allSatisfy({ $0.array.count == 1 && $0.array[0].isString && matches(#"[\r\n]|\[\d{1,3}:\d{2}|<\d{1,3}:\d{2}"#, $0.array[0].string).isEmpty }) {
                var counts: [Int: Int] = [:]
                for row in rows { counts[max(0, row.start + offset), default: 0] += 1 }
                var texts: [String] = []
                for (i, row) in rows.enumerated() {
                    let text = translated[i].array[0].string
                    if counts[max(0, row.start + offset)] == 1 && !row.text.trimmingCharacters(in: .whitespaces).isEmpty && !text.isEmpty {
                        texts.append(String(format: "[%02d:%02d.%03d]", row.start / 60000, row.start / 1000 % 60, row.start % 1000) + text)
                    }
                }
                if !texts.isEmpty { translation = prefix + texts.joined(separator: "\n") }
            }
        }
        return .init(text: prefix + rows.map(\.yrc).joined(separator: "\n"), translation: translation)
    }
}
