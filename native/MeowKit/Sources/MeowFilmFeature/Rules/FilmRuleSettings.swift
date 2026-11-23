import Foundation
import MeowRuntime

/// The public settings document projection and pure string encoding steps.
/// Regex compilation, validation and execution belong to the Node owner.
enum FilmRuleSettings {
    static func read(_ document: RuntimeValue?) throws -> FilmRuleSet {
        guard let document else { return initial }
        guard let fields = document.filmObject, fields["version"] == .number(1),
              let rules = fields["rules"] else { throw FilmSettingsStorageError.invalidDocument }
        do { return try JSONDecoder().decode(FilmRuleSet.self, from: RuntimeJSON.encode(rules)) }
        catch { throw FilmSettingsStorageError.invalidDocument }
    }

    static let inputWhitespace = CharacterSet(charactersIn:
        "\u{0009}\u{000a}\u{000b}\u{000c}\u{000d}\u{0020}\u{00a0}\u{1680}\u{2000}\u{2001}\u{2002}\u{2003}\u{2004}\u{2005}\u{2006}\u{2007}\u{2008}\u{2009}\u{200a}\u{2028}\u{2029}\u{202f}\u{205f}\u{3000}\u{feff}")
    private static let storedWhitespace = inputWhitespace.subtracting(CharacterSet(charactersIn: "\u{feff}"))
        .union(CharacterSet(charactersIn: "\u{0085}"))
    static func trim(_ value: String) -> String { value.trimmingCharacters(in: inputWhitespace) }

    static func newMatch(_ value: String, replace: String) -> FilmMatchRule? {
        var pattern = trim(value), flags = ""
        if pattern.hasPrefix("/"), let end = pattern.lastIndex(of: "/"), end != pattern.startIndex {
            flags = trim(String(pattern[pattern.index(after: end)...]))
            pattern = trim(String(pattern[pattern.index(after: pattern.startIndex)..<end]))
        }
        pattern = regexText(pattern)
        return pattern.isEmpty ? nil : .init(pattern: pattern, replace: trim(replace), flags: flags)
    }

    static func normalize(_ rules: FilmRuleSet) -> FilmRuleSet {
        func clean(_ rows: [String]) -> [String] {
            rows.map(trim).filter { !$0.isEmpty }
                .map { $0.trimmingCharacters(in: storedWhitespace) }.filter { !$0.isEmpty }
                .map(trim).filter { !$0.isEmpty }
        }
        func matches(_ rows: [FilmMatchRule]) -> [FilmMatchRule] {
            rows.map { row in
                var next = row
                next.pattern = regexText(trim(regexText(trim(regexText(trim(row.pattern))))))
                next.flags = trim(row.flags)
                return next
            }.filter { !$0.pattern.isEmpty }
        }
        return .init(episodeCleanRegexRules: clean(rules.episodeCleanRegexRules),
            episodeRules: matches(rules.episodeRules), movieRules: matches(rules.movieRules),
            aggregateRegexRules: clean(rules.aggregateRegexRules))
    }

    private static let escapedRegexUnits = Set("dDsSwWbB.()[]{}+*?^$|\\-_/".utf16)

    private static func regexText(_ text: String) -> String {
        let units = Array(text.utf16)
        var result: [UInt16] = [], index = 0
        while index < units.count {
            result.append(units[index])
            if units[index] == 92, index + 2 < units.count, units[index + 1] == 92,
               escapedRegexUnits.contains(units[index + 2]) { index += 1 }
            index += 1
        }
        return String(decoding: result, as: UTF16.self)
    }

    static func key(_ group: FilmRuleGroup) -> String {
        switch group {
        case .episodeClean: return "episodeCleanRegexRules"
        case .episode: return "episodeRules"
        case .movie: return "movieRules"
        case .search: return "aggregateRegexRules"
        }
    }

    // Shared dashboard defaults, identical to Windows' FilmRuleDefaults.json.
    // Initial setup omits the extra season-aware rule; explicit restore includes it.
    static let restore = FilmRuleSet(
        episodeCleanRegexRules: [
            #"\[(?!\s*[Ss]\d{1,2}(?:\s*[Ee]\d{1,5})?\s*\])[^\]]*\]"#,
            #"【(?!\s*[Ss]\d{1,2}(?:\s*[Ee]\d{1,5})?\s*】)[^】]*】"#,
            #"\((?!\s*[Ss]\d{1,2}(?:\s*[Ee]\d{1,5})?\s*\))[^)]*\)"#,
            #"（(?!\s*[Ss]\d{1,2}(?:\s*[Ee]\d{1,5})?\s*）)[^）]*）"#,
            #"(?:^|[\s\[\]\(\){}【】._-])(?:4k|8k|2160p|1080p|720p)(?=$|[\s\[\]\(\){}【】._-])"#,
            #"高\s*码\s*(?:率|资源|直链)?|码\s*率"#,
        ],
        episodeRules: [
            .init(pattern: #".*?(?:(?:[Ss](?:eason)?\s*0*(\d{1,2}))|(?:第\s*0*(\d{1,2})\s*(?:季|部|篇))).*?(?:第\s*0*(\d{1,4})\s*(?:集|话)|[Ee][Pp]?\s*0*(\d{1,4})(?:$|\D)).*?.*"#, replace: #"S$1$2E$3$4"#, flags: #"i"#),
            .init(pattern: #".*?([Ss]\d{1,2})?(?:第\s*(\d{1,4})\s*(?:集|话)|[Ee][Pp]?\s*(\d{1,4})(?:$|\D)).*?.*"#, replace: #"$1E$2$3"#, flags: #"i"#),
            .init(pattern: #"^[\s\[\]\(\){}【】._=-]*0*(\d{1,4})[\s\[\]\(\){}【】._=-]*(?:\.[A-Za-z0-9]{1,6})?\s*$"#, replace: #"E$1"#, flags: #"i"#),
        ],
        movieRules: [
            .init(pattern: #"^\s*(?!.*(?:S\d{1,2}\s*E\d{1,3}|第\s*\d+\s*[集话期]|(?:^|[\s._-])(?:EP?|E)\s*\d+(?:$|[\s._-])))(?=.*\b(?:19\d{2}|20\d{2})\b).*\.(?:mkv|mp4)\s*$"#, replace: #""#, flags: #"i"#),
        ],
        aggregateRegexRules: [
            #"\([^)]*\)|（[^）]*）|\[[^\]]*\]|\{[^}]*\}|【[^】]*】"#,
            #"(?<!新)年\s*番\s*\d+|(?<!新)年\s*番"#,
            #"更新\s*中|(?:更新(?:至|到)?|更(?:至|到)?|更|首\s*更)\s*(?:第\s*)?(?:EP?|E)?\s*(?:\d{1,4}|[一二三四五六七八九十百千万两零〇]{1,20})(?:\s*(?:\+|＋|多|余))?\s*(?:集|话|期)?|首\s*更"#,
            #"(?:HD\s*)?(?:4[kK]|8[kK])|(?:2160|1080|720)[pP]|国\s*漫|臻\s*彩|杜\s*比\s*音\s*效|已\s*刮\s*削|连\s*载\s*中|10\s*[- ]?bit"#,
            #"(?:19\d{2}|20\d{2})(?=\s*(?:(?:HD\s*)?(?:4[kK]|8[kK])|(?:更新|更)))"#,
            #"最\s*新\s*(?:一\s*集|更\s*新)"#,
            #"(?<=\D)\d{1,4}$"#,
        ])
    static var initial: FilmRuleSet {
        var rules = restore
        rules.episodeRules.removeFirst()
        return rules
    }
}
