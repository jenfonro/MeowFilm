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
        return try lines(raw.text, translation: raw.translation, duration: duration)
    }
    struct Word { var time: Double; var duration: Double?; let text: String }
    struct Line { var time: Double; var end: Double?; let text: String; var words: [Word]; let ordinal: Int }
    static func lines(_ raw: String, translation: String = "", duration: Double = 0) throws -> [MusicLyricLine] {
        let input = raw
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
            let texts = (translated[Int64((time * 1000).rounded())] ?? []).filter { $0 != row.text }
            result.append(.init(index: i, time: time, end: max(time, end), text: row.text, translation: texts.joined(separator: "\n"),
                words: words))
        }
        return result
    }
}
