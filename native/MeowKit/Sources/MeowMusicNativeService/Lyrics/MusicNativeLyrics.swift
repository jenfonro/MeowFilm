// SPDX-License-Identifier: Apache-2.0
// Native generic text timing, from iWebPlus b900c259 lyric-timing.js.
import Foundation
import MeowMusicFeature

enum MusicNativeLyricsParser {
    struct Match {
        let values: [String]
        let range: NSRange
        subscript(_ i: Int) -> String { i < values.count ? values[i] : "" }
    }
    private static let xmlStartRegex = try! NSRegularExpression(pattern: #"^\s*<"#)
    private static let lyricContentAttrRegex = try! NSRegularExpression(pattern: #"\bLyricContent\s*=\s*(["'])([\s\S]*?)\1"#)
    private static let xmlEntityRegex = try! NSRegularExpression(pattern: #"&(amp|lt|gt|quot|apos|#\d+|#[xX][\da-fA-F]+);"#)
    private static let timedRegex = try! NSRegularExpression(pattern: #"\[(?:\d{1,3}:\d{2}(?:\.\d{1,3})?|\d+,\d+)\]"#)
    private static let offsetRegex = try! NSRegularExpression(pattern: #"\[offset:([+-]?\d+)\]"#, options: [.caseInsensitive])
    private static let yrcLineRegex = try! NSRegularExpression(pattern: #"^\[(\d+),(\d+)\](.*)$"#)
    private static let yrcWordPrefixRegex = try! NSRegularExpression(pattern: #"^\(\d+,\d+,\d+\)"#)
    private static let qrcWordSuffixRegex = try! NSRegularExpression(pattern: #"\(\d+,\d+\)$"#)
    private static let qrcWordRegex = try! NSRegularExpression(pattern: #"(.*?)\((\d+),(\d+)\)"#)
    private static let yrcWordRegex = try! NSRegularExpression(pattern: #"\((\d+),(\d+),\d+\)(.*?)(?=\(\d+,\d+,\d+\)|$)"#)
    private static let lrcLineRegex = try! NSRegularExpression(pattern: #"^((?:\[\d{1,3}:\d{2}(?:\.\d{1,3})?\])+)(.*)$"#)
    private static let lrcTagRegex = try! NSRegularExpression(pattern: #"\[(\d{1,3}):(\d{2}(?:\.\d{1,3})?)\]"#)
    private static let lrcWordMarkerRegex = try! NSRegularExpression(pattern: #"(?:\[\[(\d{1,3}):(\d{2}(?:\.\d{1,3})?)\]\]|<(\d{1,3}):(\d{2}(?:\.\d{1,3})?)>)"#)

    static func matches(_ re: NSRegularExpression, _ value: String) -> [Match] {
        let ns = value as NSString
        return re.matches(in: value, range: NSRange(location: 0, length: ns.length)).map { match in
            .init(values: (0..<match.numberOfRanges).map { match.range(at: $0).location == NSNotFound ? "" : ns.substring(with: match.range(at: $0)) }, range: match.range)
        }
    }
    private static func unwrapXML(_ value: String) -> String? {
        let ns = value as NSString
        guard xmlStartRegex.firstMatch(in: value, range: NSRange(location: 0, length: ns.length)) != nil else {
            return value
        }
        let attributes = matches(lyricContentAttrRegex, value)
        guard attributes.count == 1 else { return nil }
        return unescapeXMLEntities(attributes[0][2])
    }
    private static func unescapeXMLEntities(_ value: String) -> String {
        let ns = value as NSString
        let found = xmlEntityRegex.matches(in: value, range: NSRange(location: 0, length: ns.length))
        guard !found.isEmpty else { return value }
        var result = ""
        result.reserveCapacity(value.count)
        var cursor = 0
        for match in found {
            if match.range.location > cursor {
                result += ns.substring(with: NSRange(location: cursor, length: match.range.location - cursor))
            }
            let name = ns.substring(with: match.range(at: 1))
            if name.hasPrefix("#") {
                let hex = name.hasPrefix("#x") || name.hasPrefix("#X")
                let digits = String(name.dropFirst(hex ? 2 : 1))
                if let code = UInt32(digits, radix: hex ? 16 : 10),
                   code > 0, code <= 0x10FFFF, !(0xD800...0xDFFF).contains(code),
                   let scalar = UnicodeScalar(code) {
                    result.append(Character(scalar))
                } else {
                    result += ns.substring(with: match.range)
                }
            } else {
                switch name {
                case "amp": result.append("&")
                case "lt": result.append("<")
                case "gt": result.append(">")
                case "quot": result.append("\"")
                default: result.append("'")
                }
            }
            cursor = NSMaxRange(match.range)
        }
        if cursor < ns.length {
            result += ns.substring(from: cursor)
        }
        return result
    }
    static func timed(_ text: String) -> Bool {
        guard let unwrapped = unwrapXML(text) else { return false }
        let ns = unwrapped as NSString
        return timedRegex.firstMatch(in: unwrapped, range: NSRange(location: 0, length: ns.length)) != nil
    }
    static func clock(_ minute: String, _ second: String) -> Double { (Double(minute) ?? 0) * 60 + (Double(second) ?? 0) }
    static func parse(_ raw: MusicNativeLyrics, duration: Double = 0) throws -> [MusicLyricLine] {
        return try lines(raw.text, translation: raw.translation, romanization: raw.romanization, duration: duration)
    }
    struct Word { var time: Double; var duration: Double?; let text: String }
    struct Line { var time: Double; var end: Double?; let text: String; var words: [Word]; let ordinal: Int }
    static func lines(_ raw: String, translation: String = "", romanization: String = "", duration: Double = 0) throws -> [MusicLyricLine] {
        guard let input = unwrapXML(raw) else { return [] }
        let offset = (matches(offsetRegex, input).first.flatMap { Double($0[1]) } ?? 0) / 1000
        try musicRequire(offset.isFinite && abs(offset) <= 9_007_199_254_740, .invalidLyricResponse)
        var output: [Line] = []
        for row in input.components(separatedBy: .newlines) {
            let text = row.trimmingCharacters(in: .whitespacesAndNewlines)
            if let head = matches(yrcLineRegex, text).first {
                let time = (Double(head[1]) ?? 0) / 1000, end = time + (Double(head[2]) ?? 0) / 1000, content = head[3]
                let contentRange = NSRange(location: 0, length: (content as NSString).length)
                let qrc = yrcWordPrefixRegex.firstMatch(in: content, range: contentRange) == nil &&
                    qrcWordSuffixRegex.firstMatch(in: content, range: contentRange) != nil
                let words: [Word]
                if qrc {
                    words = matches(qrcWordRegex, content).filter { !$0[1].isEmpty }.map {
                        .init(time: (Double($0[2]) ?? 0) / 1000 + offset, duration: (Double($0[3]) ?? 0) / 1000, text: $0[1])
                    }
                } else {
                    words = matches(yrcWordRegex, content).filter { !$0[3].isEmpty }.map {
                        .init(time: (Double($0[1]) ?? 0) / 1000 + offset, duration: (Double($0[2]) ?? 0) / 1000, text: $0[3])
                    }
                }
                let plain = words.isEmpty ? content.trimmingCharacters(in: .whitespaces) : words.map(\.text).joined()
                if !plain.isEmpty { output.append(.init(time: time + offset, end: end + offset, text: plain, words: words, ordinal: output.count)) }
            } else if let head = matches(lrcLineRegex, text).first {
                let content = head[2].trimmingCharacters(in: .whitespaces), ns = content as NSString
                if content.isEmpty { continue }
                for tag in matches(lrcTagRegex, head[1]) {
                    let time = clock(tag[1], tag[2])
                    let markers = matches(lrcWordMarkerRegex, content)
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
        }
        output.sort { $0.time == $1.time ? $0.ordinal < $1.ordinal : $0.time < $1.time }
        var translated: [Int64: [String]] = [:]
        if !translation.isEmpty {
            for line in try lines(translation) {
                let key = Int64((line.time * 1000).rounded())
                if !(translated[key] ?? []).contains(line.text) { translated[key, default: []].append(line.text) }
            }
        }
        var romanized: [Int64: [String]] = [:]
        if !romanization.isEmpty {
            for line in try lines(romanization) {
                let key = Int64((line.time * 1000).rounded())
                if !(romanized[key] ?? []).contains(line.text) { romanized[key, default: []].append(line.text) }
            }
        }
        var result: [MusicLyricLine] = []
        var nextPosition = 0
        for (i, row) in output.enumerated() {
            while nextPosition < output.count && output[nextPosition].time <= row.time { nextPosition += 1 }
            let next = nextPosition < output.count ? output[nextPosition].time : nil
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
            let timeKey = Int64((time * 1000).rounded())
            let texts = (translated[timeKey] ?? []).filter { $0 != row.text }
            let romaTexts = (romanized[timeKey] ?? []).filter { $0 != row.text }
            result.append(.init(index: i, time: time, end: max(time, end), text: row.text,
                translation: texts.joined(separator: "\n"),
                romanization: romaTexts.joined(separator: "\n"),
                words: words))
        }
        return result
    }
}
