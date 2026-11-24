import Foundation
import MeowRuntime

public struct NovelSourceCandidateMatch: Equatable, Sendable {
    public var installationId: String
    public var bookUrl: String
    public var identityName: String
    public var identityAuthor: String
    public var identityKey: String
    public var sameBook: Bool
    public var sameSource: Bool
    public var needsConfirmation: Bool
}

public struct NovelSourceCandidatesResult: Equatable, Sendable {
    public var targetName: String
    public var targetAuthor: String
    public var targetKey: String
    public var candidates: [NovelSourceCandidateMatch]
}

/// Borrows the novel runtime owner. Node executes pure rules; closing this
/// consumer cancels and drains only its accepted calls.
public actor NovelRuleProcessor {
    private let runtime: NovelRuntime
    private var pending: [UUID: Task<RuntimeValue, Error>] = [:]
    private var closed = false
    private var closing: Task<Void, Never>?
    public init(runtime: NovelRuntime) { self.runtime = runtime }
    public func invoke(_ fields: [String: RuntimeValue]) async throws -> RuntimeValue {
        guard !closed else { throw NovelError.closed }
        try Task.checkCancellation()
        let request = RuntimeValue.object(["request": .object(RuntimeObject(fields))])
        let id = UUID(), runtime = runtime
        let task = Task { try await runtime.call("rules.process", request) }
        pending[id] = task
        defer { pending[id] = nil }
        let output: RuntimeValue
        do {
            output = try await withTaskCancellationHandler { try await task.value } onCancel: { task.cancel() }
        } catch let error as RuntimeHTTPError {
            switch error {
            case .cancelled, .remote("CANCELLED"): throw CancellationError()
            case .closed, .remote("CLOSED"): throw NovelError.closed
            case .timeout: throw NovelError.ruleExecution("TIMEOUT")
            case .invalidRequest: throw NovelError.ruleExecution("INVALID_REQUEST")
            case .invalidResponse: throw NovelError.invalidResponse
            case .remote(let code): throw NovelError.ruleExecution(code)
            }
        }
        try Task.checkCancellation()
        guard !closed else { throw NovelError.closed }
        return output
    }
    public func identity(_ book: NovelBook) async throws -> String {
        let output = try await invoke(["operation": .string("identity"), "book": book.wire])
        guard let key = output["key"].string, output["name"].string != nil, output["author"].string != nil else { throw NovelError.invalidResponse }
        return key
    }
    public func sourceCandidatesBatch(_ books: [NovelBook], for current: NovelBook) async throws -> NovelSourceCandidatesResult {
        let output = try await invoke([
            "operation": .string("sourceCandidates"),
            "books": .array(books.map(\.wire)),
            "target": current.wire,
        ])
        let target = output["targetIdentity"]
        guard let targetName = target["name"].string,
              let targetAuthor = target["author"].string,
              let targetKey = target["key"].string,
              let list = output["candidates"].array else { throw NovelError.invalidResponse }
        let matches: [NovelSourceCandidateMatch] = try list.map { item in
            let book = item["book"]
            let identity = item["identity"]
            guard let inst = book["installationId"].string,
                  let url = book["bookUrl"].string,
                  let name = identity["name"].string,
                  let author = identity["author"].string,
                  let key = identity["key"].string,
                  let sameBook = item["sameBook"].bool,
                  let sameSource = item["sameSource"].bool,
                  let needsConfirmation = item["needsConfirmation"].bool else { throw NovelError.invalidResponse }
            return NovelSourceCandidateMatch(
                installationId: inst,
                bookUrl: url,
                identityName: name,
                identityAuthor: author,
                identityKey: key,
                sameBook: sameBook,
                sameSource: sameSource,
                needsConfirmation: needsConfirmation
            )
        }
        return NovelSourceCandidatesResult(targetName: targetName, targetAuthor: targetAuthor, targetKey: targetKey, candidates: matches)
    }
    public func sourceCandidates(_ results: [NovelSearchBook], for current: NovelBook) async throws -> [NovelSearchBook] {
        guard !results.isEmpty else { return [] }
        let batch = try await sourceCandidatesBatch(results.flatMap(\.candidates), for: current)
        let matchedKeys = Set(batch.candidates.filter(\.sameBook).map { $0.installationId + "\n" + $0.bookUrl })
        var seen = Set<String>()
        var flattened: [NovelSearchBook] = []
        for group in results {
            for candidate in group.candidates {
                let key = candidate.installationId + "\n" + candidate.bookUrl
                guard matchedKeys.contains(key), seen.insert(key).inserted else { continue }
                flattened.append(NovelSearchBook(book: candidate, candidates: [candidate], groupKey: key))
            }
        }
        return flattened
    }
    public func merge(_ books: [NovelBook], sources: [NovelSource]) async throws -> [NovelSearchBook] {
        let output = try await invoke(["operation": .string("merge"), "books": .array(books.map(\.wire))])
        guard let values = output["books"].array else { throw NovelError.invalidResponse }
        return try values.map { value in
            func book(_ item: RuntimeValue) throws -> NovelBook {
                guard let source = sources.first(where: { $0.id == item["installationId"].string }) else { throw NovelError.invalidResponse }
                return try NovelBook.parse(item, source: source)
            }
            guard let key = value["groupKey"].string, let candidates = value["candidates"].array else { throw NovelError.invalidResponse }
            return try .init(book: book(value), candidates: candidates.map(book), groupKey: key)
        }
    }
    public func match(_ chapters: [NovelChapter], anchor: NovelAnchor) async throws -> Int? {
        let output = try await invoke(["operation": .string("matchChapter"), "chapters": .array(chapters.map(\.wire)),
                                       "anchor": try .encoding(anchor)])
        guard output["requiresSelection"].bool == false, let index = output["index"].number,
              index >= 0, index < Double(chapters.count) else { return nil }
        return Int(index)
    }
    public func transform(_ content: NovelContent, book: NovelBook, source: NovelSource,
                          rules: [NovelRule], conversion: String) async throws -> NovelContent {
        let output = try await invoke(["operation": .string("transform"), "title": .string(content.title),
            "blocks": try .encoding(content.blocks), "book": book.wire, "source": try .encoding(source),
            "rules": try .encoding(rules), "conversion": .string(conversion)])
        guard let title = output["title"].string else { throw NovelError.invalidResponse }
        return try NovelContent(title: title, blocks: output["blocks"].decoding([NovelBlock].self), imageStyle: content.imageStyle)
    }

    public func matchesURL(_ url: String, pattern: String) async throws -> Bool {
        let output = try await invoke(["operation": .string("matchUrl"), "pattern": .string(pattern), "url": .string(url)])
        guard let matched = output.bool else { throw NovelError.invalidResponse }
        return matched
    }

    public func close() async {
        if let closing { await closing.value; return }
        closed = true
        let accepted = Array(pending.values)
        for task in accepted { task.cancel() }
        let task = Task { for task in accepted { _ = await task.result } }
        closing = task
        await task.value
    }
}
