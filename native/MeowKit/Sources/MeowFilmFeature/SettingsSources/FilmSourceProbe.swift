import Foundation
import MeowRuntime

/// Matches the reference probe: sample three videos and the first episode of
/// each line. A resolved address is not evidence of actual media playback.
struct FilmSourceProbe {
    let transport: CatScriptTransport
    let cat: FilmCatSource
    let site: FilmVideoSource
    private var steps: [FilmSourceProbeStep] = []

    init(transport: CatScriptTransport, cat: FilmCatSource, site: FilmVideoSource) {
        self.transport = transport; self.cat = cat; self.site = site
    }

    func run() async throws -> FilmSourceDetection {
        var probe = self
        return try await probe.execute()
    }

    private mutating func execute() async throws -> FilmSourceDetection {
        let home = try await call("home", .object([:]))
        var category: RuntimeValue?
        if let home {
            let id = Self.classes(home).map { Self.pick($0, ["type_id", "tid", "id", "typeId"]) }.first { !$0.isEmpty } ?? "0"
            category = try await call("category", .object(["id": .string(id), "page": .number(1),
                "filter": .bool(true), "filters": .object([:])]))
        }
        let items = category.map { Array(Self.list($0).prefix(10)) } ?? []
        if category != nil && items.isEmpty { empty("category", "EMPTY_LIST") }
        let categoryPlay = try await (items.isEmpty ? Attempt() : tryPlay(items))
        let searchForPlay = category == nil || items.isEmpty || (!categoryPlay.ok && categoryPlay.missing)
        let search: RuntimeValue?
        if searchForPlay || categoryPlay.ok {
            search = try await call("search", .object(["wd": .string("斗破"), "page": .number(1)]))
        } else { search = nil }
        let searchItems = search.map(Self.list) ?? []
        if search != nil && searchItems.isEmpty { empty("search", "EMPTY_LIST") }
        let searchPlay = try await (searchForPlay && !searchItems.isEmpty ? tryPlay(searchItems) : Attempt())
        let result = categoryPlay.ok ? (search != nil ? "valid" : "search_error") :
            searchPlay.ok ? (categoryPlay.missing ? "valid" : "category_error") : "invalid"
        try Task.checkCancellation()
        return .init(result: result, checkedAt: Int64(Date().timeIntervalSince1970 * 1000),
            playbackResolved: categoryPlay.ok || searchPlay.ok, steps: steps)
    }

    private struct Attempt { var ok = false; var missing = false }
    private struct Candidate: Hashable {
        let flag: String
        let id: String

        static func == (lhs: Self, rhs: Self) -> Bool {
            lhs.flag.utf8.elementsEqual(rhs.flag.utf8) && lhs.id.utf8.elementsEqual(rhs.id.utf8)
        }

        func hash(into hasher: inout Hasher) {
            hasher.combine(flag.utf8.count)
            for byte in flag.utf8 { hasher.combine(byte) }
            hasher.combine(id.utf8.count)
            for byte in id.utf8 { hasher.combine(byte) }
        }
    }

    private mutating func tryPlay(_ items: [RuntimeValue]) async throws -> Attempt {
        var missing = false
        let videos = items.filter { if case .object = $0 { return true }; return false }.prefix(3)
        for video in videos {
            try Task.checkCancellation()
            var candidates = Self.candidates(video)
            if candidates.isEmpty {
                let id = Self.pick(video, ["vod_id", "vodId", "id", "ID"])
                if id.isEmpty { empty("detail", "MISSING_VIDEO_ID"); continue }
                guard let detail = try await call("detail", .object(["id": .string(id), "vod_id": .string(id)])) else { continue }
                candidates = try await detailCandidates(detail)
                if candidates.isEmpty { missing = true; empty("detail", "MISSING_PLAY_METADATA"); continue }
            }
            for candidate in candidates {
                guard let reply = try await call("play", .object(["flag": .string(candidate.flag),
                    "id": .string(candidate.id), "siteApi": .string(site.api)])) else { continue }
                let url = Self.playURL(reply)
                if url.hasPrefix("meow.pan.v1.") {
                    steps.append(.init("play", "unsupported", "PAN_RESOLVER_UNAVAILABLE")); continue
                }
                if !url.isEmpty { return Attempt(ok: true, missing: missing) }
                empty("play", "MISSING_PLAY_URL")
            }
        }
        return Attempt(ok: false, missing: missing)
    }

    private struct Navigation { let action: String; let payload: RuntimeValue }
    private struct Page {
        let document: RuntimeValue
        let action: String?
        let payload: RuntimeValue?
        let previousPage: Int64
        var ancestors: Set<Data>
        var lists: Set<Data>
    }

    private mutating func detailCandidates(_ detail: RuntimeValue) async throws -> [Candidate] {
        let first = Self.list(detail)
        let hasNavigation = first.contains { item in
            if Self.navigation(item) != nil { return true }
            if case .object? = item.catField("vod_navigation") { return true }
            return false
        }
        if !hasNavigation { return first.first.map(Self.candidates) ?? [] }
        var result: [Candidate] = [], seen: Set<Data> = []
        var queue = [Page(document: detail, action: nil, payload: nil, previousPage: 0, ancestors: [], lists: [])]
        var cursor = 0
        while cursor < queue.count {
            try Task.checkCancellation()
            var page = queue[cursor]; cursor += 1
            guard let list = page.document.catField("list")?.catArray else {
                steps.append(.init("detail", "failed", "INVALID_NAVIGATION")); continue
            }
            let current = Self.pageNumber(page.document, "page"), count = Self.pageNumber(page.document, "pagecount")
            if let action = page.action, page.previousPage > 0 || (current > 0 && count > current) {
                if page.previousPage > 0 && current <= page.previousPage {
                    steps.append(.init(action, "failed", "NAVIGATION_PAGE_NOT_ADVANCING")); continue
                }
                if !list.isEmpty, !page.lists.insert(try Self.canonical(.array(list))).inserted {
                    steps.append(.init(action, "failed", "NAVIGATION_REPEATED_PAGE")); continue
                }
            }
            for item in list {
                guard let nav = Self.navigation(item) else { result.append(contentsOf: Self.candidates(item)); continue }
                let key = try Self.canonical(.array([.string(nav.action), nav.payload]))
                if page.ancestors.contains(key) {
                    steps.append(.init(nav.action, "failed", "NAVIGATION_CYCLE")); continue
                }
                if !seen.insert(key).inserted { continue }
                if let child = try await call(nav.action, nav.payload) {
                    var ancestors = page.ancestors; ancestors.insert(key)
                    queue.append(Page(document: child, action: nav.action, payload: nav.payload,
                        previousPage: 0, ancestors: ancestors, lists: []))
                }
            }
            if let action = page.action, case .object(var payload)? = page.payload,
               !list.isEmpty, current > 0, count > current {
                if payload["pg"] != nil { payload["pg"] = .number(Double(current + 1)) }
                if payload["page"] != nil || payload["pg"] == nil { payload["page"] = .number(Double(current + 1)) }
                let body = RuntimeValue.object(payload)
                let key = try Self.canonical(.array([.string(action), body]))
                if !seen.insert(key).inserted {
                    steps.append(.init(action, "failed", "NAVIGATION_CYCLE")); continue
                }
                if let following = try await call(action, body) {
                    queue.append(Page(document: following, action: action, payload: body, previousPage: current,
                        ancestors: page.ancestors, lists: page.lists))
                }
            }
        }
        var unique: Set<Candidate> = []
        return result.filter { unique.insert($0).inserted }
    }

    private mutating func call(_ operation: String, _ payload: RuntimeValue) async throws -> RuntimeValue? {
        try Task.checkCancellation()
        guard site.actions.contains(operation) else {
            steps.append(.init(operation, "unsupported", "CAPABILITY_UNAVAILABLE")); return nil
        }
        do {
            var value = try await transport.invoke(cat: cat, site: site, operation: operation, payload: payload)
            try Task.checkCancellation()
            if case .string(let text) = value { value = try CatScriptTransport.parse(Data(text.utf8)) }
            switch value {
            case .object, .array: break
            default: steps.append(.init(operation, "failed", "INVALID_RESPONSE")); return nil
            }
            for response in [value, value.catField("data") ?? .null] {
                let status = response.catField("statusCode")
                let httpFailure: Bool
                if case .number(let code)? = status { httpFailure = code >= 400 } else { httpFailure = false }
                if httpFailure || response.catField("ok") == .bool(false) || response.catField("success") == .bool(false) ||
                    !Self.text(response, "error").isEmpty {
                    let message = Self.pick(response, ["message", "msg", "error"])
                    let code = httpFailure ? "HTTP_" + Self.pick(response, ["statusCode"]) : "SCRIPT_ERROR"
                    steps.append(.init(operation, "failed", message.isEmpty ? code : message)); return nil
                }
            }
            steps.append(.init(operation, "success"))
            return value
        } catch is CancellationError { throw CancellationError() }
        catch {
            try Task.checkCancellation()
            let code = (error as? FilmSourceError)?.code ?? "CALL_FAILED"
            if ["CANCELLED", "STALE_SESSION", "HOST_CLOSED", "CLOSED", "DISABLED", "STARTING"].contains(code) {
                throw CancellationError()
            }
            steps.append(.init(operation, code == "CAPABILITY_UNAVAILABLE" ? "unsupported" : "failed", code))
            return nil
        }
    }

    private mutating func empty(_ operation: String, _ reason: String) { steps.append(.init(operation, "empty", reason)) }
    private static func list(_ value: RuntimeValue) -> [RuntimeValue] {
        if let array = value.catArray { return array }
        let data = value.catField("data")
        return [value.catField("list"), data, data?.catField("list"), data?.catField("vod_list"), data?.catField("items")]
            .compactMap { $0?.catArray }.first ?? []
    }
    private static func classes(_ value: RuntimeValue) -> [RuntimeValue] {
        let data = value.catField("data")
        return [value.catField("class"), value.catField("classes"), data?.catField("class"),
                data?.catField("classes"), data?.catField("types"), value.catField("types")]
            .compactMap { $0?.catArray }.first ?? []
    }
    private static func candidates(_ value: RuntimeValue) -> [Candidate] {
        let from = pick(value, ["vod_play_from", "play_from", "playFrom"])
        let urls = pick(value, ["vod_play_url", "play_url", "playUrl"])
        let flags = from.isEmpty ? [] : literalComponents(from, separator: "$$$")
        let blocks = urls.isEmpty ? [] : literalComponents(urls, separator: "$$$")
        var result: [Candidate] = [], seen: Set<Candidate> = []
        for i in 0..<max(flags.count, blocks.count) {
            let flag = i < flags.count && !trim(flags[i]).isEmpty ? trim(flags[i]) : "源\(i + 1)"
            let block = i < blocks.count ? trim(blocks[i]) : ""
            if block.isEmpty { continue }
            let split = flag.range(of: "|||", options: .literal) != nil
            let subFlags = split ? literalComponents(flag, separator: "|||") : [flag]
            let subBlocks = split ? literalComponents(block, separator: "|||") : [block]
            for j in 0..<max(subFlags.count, subBlocks.count) {
                let label = j < subFlags.count && !trim(subFlags[j]).isEmpty ? trim(subFlags[j]) : flag
                let episodes = j < subBlocks.count ? subBlocks[j] : ""
                let line = trim(episodes.range(of: "#", options: .literal).map { String(episodes[..<$0.lowerBound]) } ?? episodes)
                let id = trim(line.range(of: "$", options: .literal).map { String(line[$0.upperBound...]) } ?? line)
                let candidate = Candidate(flag: label, id: id)
                if !id.isEmpty && seen.insert(candidate).inserted { result.append(candidate) }
            }
        }
        return result
    }
    // Protocol separators are literal scalars, even beside combining marks.
    // Preserve empty segments and original text, as the reference parser does.
    private static func literalComponents(_ value: String, separator: String) -> [String] {
        var result: [String] = []
        var start = value.startIndex
        while let range = value.range(of: separator, options: .literal, range: start..<value.endIndex) {
            result.append(String(value[start..<range.lowerBound]))
            start = range.upperBound
        }
        result.append(String(value[start...]))
        return result
    }
    private static func playURL(_ value: RuntimeValue) -> String {
        for item in [value.catField("url"), value.catField("playUrl"), value.catField("data")?.catField("url")] {
            if let text = item?.catText, !trim(text).isEmpty { return trim(text) }
            guard let array = item?.catArray else { continue }
            let strings = array.compactMap(\.catText).map(trim).filter { !$0.isEmpty }
            if let url = strings.last(where: { $0.lowercased().hasPrefix("https://") || $0.lowercased().hasPrefix("http://") }) ?? strings.first {
                return url
            }
        }
        return ""
    }
    private static func navigation(_ value: RuntimeValue) -> Navigation? {
        guard !pick(value, ["vod_id"]).isEmpty, text(value, "vod_play_from").isEmpty, text(value, "vod_play_url").isEmpty else { return nil }
        let nav = value.catField("vod_navigation") ?? .null
        var action = text(nav, "action")
        if action.isEmpty && text(value, "vod_tag") == "folder" { action = "category" }
        func letter(_ byte: UInt8) -> Bool { (65...90).contains(byte) || (97...122).contains(byte) }
        guard let first = action.utf8.first, letter(first),
              action.utf8.allSatisfy({ letter($0) || (48...57).contains($0) || $0 == 95 || $0 == 45 }) else { return nil }
        let payload: RuntimeValue
        if case .object(let fields)? = nav.catField("payload") { payload = .object(fields) }
        else {
            var fields = ["id": value.catField("vod_id") ?? .null]
            if action == "category" { fields["page"] = .number(1) }
            payload = .object(RuntimeObject(fields))
        }
        return Navigation(action: action, payload: payload)
    }
    private static func trim(_ value: String) -> String { value.trimmingCharacters(in: .whitespacesAndNewlines) }
    private static func text(_ value: RuntimeValue, _ key: String) -> String { value.catField(key)?.catText.map(trim) ?? "" }
    private static func pick(_ value: RuntimeValue, _ keys: [String]) -> String {
        for key in keys {
            let item = value.catField(key)
            let string: String
            if let text = item?.catText { string = trim(text) }
            else if case .number(let number)? = item {
                string = number.rounded(.towardZero) == number && abs(number) < Double(Int64.max) ? String(Int64(number)) : String(number)
            } else { string = "" }
            if !string.isEmpty { return string }
        }
        return ""
    }
    private static func pageNumber(_ value: RuntimeValue, _ key: String) -> Int64 { max(0, Int64(pick(value, [key])) ?? 0) }
    // Traversal identity ignores member order but preserves every UTF-8 key.
    private static func canonical(_ value: RuntimeValue) throws -> Data {
        var bytes = Data()
        try appendCanonical(value, to: &bytes)
        return bytes
    }
    private static func appendCanonical(_ value: RuntimeValue, to bytes: inout Data) throws {
        switch value {
        case .object(let fields):
            bytes.append(0x7b)
            let keys = fields.keys.sorted { $0.utf8.lexicographicallyPrecedes($1.utf8) }
            for (index, key) in keys.enumerated() {
                if index > 0 { bytes.append(0x2c) }
                bytes.append(try RuntimeJSON.encode(.string(key)))
                bytes.append(0x3a)
                try appendCanonical(fields[key]!, to: &bytes)
            }
            bytes.append(0x7d)
        case .array(let values):
            bytes.append(0x5b)
            for (index, value) in values.enumerated() {
                if index > 0 { bytes.append(0x2c) }
                try appendCanonical(value, to: &bytes)
            }
            bytes.append(0x5d)
        default: bytes.append(try RuntimeJSON.encode(value))
        }
    }
}
