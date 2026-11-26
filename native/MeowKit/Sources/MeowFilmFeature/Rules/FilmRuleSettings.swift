import Foundation

/// Pure string encoding and typed conversion between `FilmRuleSet` and `FilmRulesRecord`.
/// Regex compilation, validation and execution belong to the Node owner.
enum FilmRuleSettings {
    static func ruleSet(from record: FilmRulesRecord?) -> FilmRuleSet {
        guard let record else { return initial }
        return FilmRuleSet(
            episodeCleanRegexRules: record.episodeCleanRegexRules,
            episodeRules: record.episodeRules.map { FilmMatchRule(pattern: $0.pattern, replace: $0.replace, flags: $0.flags) },
            movieRules: record.movieRules.map { FilmMatchRule(pattern: $0.pattern, replace: $0.replace, flags: $0.flags) },
            aggregateRegexRules: record.aggregateRegexRules,
            seasonRules: record.seasonRules.map { FilmMatchRule(pattern: $0.pattern, replace: $0.replace, flags: $0.flags) }
        )
    }

    static func record(from rules: FilmRuleSet) -> FilmRulesRecord {
        FilmRulesRecord(
            episodeCleanRegexRules: rules.episodeCleanRegexRules,
            episodeRules: rules.episodeRules.map { FilmRuleEntryRecord(pattern: $0.pattern, replace: $0.replace, flags: $0.flags) },
            movieRules: rules.movieRules.map { FilmRuleEntryRecord(pattern: $0.pattern, replace: $0.replace, flags: $0.flags) },
            aggregateRegexRules: rules.aggregateRegexRules,
            seasonRules: rules.seasonRules.map { FilmRuleEntryRecord(pattern: $0.pattern, replace: $0.replace, flags: $0.flags) }
        )
    }

    static func trim(_ value: String) -> String { FilmInputWhitespace.trim(value) }

    static func normalize(_ rules: FilmRuleSet) -> FilmRuleSet {
        func clean(_ rows: [String]) -> [String] {
            rows.map(trim).filter { !$0.isEmpty }
        }
        func matches(_ rows: [FilmMatchRule]) -> [FilmMatchRule] {
            rows.map { row in
                var next = row
                next.pattern = regexText(trim(row.pattern))
                next.flags = trim(row.flags)
                return next
            }.filter { !$0.pattern.isEmpty }
        }
        return .init(episodeCleanRegexRules: clean(rules.episodeCleanRegexRules),
            episodeRules: matches(rules.episodeRules), movieRules: matches(rules.movieRules),
            aggregateRegexRules: clean(rules.aggregateRegexRules),
            seasonRules: matches(rules.seasonRules))
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
        ],
        seasonRules: [
            .init(pattern: #"^.*?(?:[Ss](?:eason)?\s*0*(\d{1,2})(?=\b|[Ee]|\D)|第\s*0*(\d{1,2})\s*(?:季|部|篇)|(?:^|\s)0*(\d{1,2})\s*季(?![度节])).*$"#, replace: #"S$1$2$3"#, flags: #"i"#),
            .init(pattern: #"^.*?(?<!新)年\s*番\s*0*(\d{1,2}).*$"#, replace: #"S$1"#, flags: #"i"#),
            .init(pattern: #"^.*?(?<!新)年\s*番(?!\s*\d).*$"#, replace: #"S1"#, flags: #"i"#),
            .init(pattern: #"^\s*(.*[\u4e00-\u9fffA-Za-z])\s*0*([1-9])\s*$"#, replace: #"S$2"#, flags: #"i"#),
        ])
    static var initial: FilmRuleSet { restore }
}
