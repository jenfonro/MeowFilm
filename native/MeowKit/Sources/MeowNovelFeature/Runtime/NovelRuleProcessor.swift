import Foundation
import MeowRuntime

/// Borrows the novel runtime owner. Node executes pure rules; closing this
/// consumer cancels and drains only its accepted calls.
public actor NovelRuleProcessor {
    private let runtime: any NovelRuntimeServing
    private var pending: [UUID: Task<RuntimeValue, Error>] = [:]
    private var closed = false
    private var closing: Task<Void, Never>?
    public init(runtime: any NovelRuntimeServing) { self.runtime = runtime }
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
        try await identityDetails(book).key
    }
    public func identityDetails(_ book: NovelBook) async throws -> (name: String, author: String, key: String) {
        let output = try await invoke(["operation": .string("identity"), "book": book.wire])
        guard let key = output["key"].string, let name = output["name"].string, let author = output["author"].string else { throw NovelError.invalidResponse }
        return (name, author, key)
    }
    public func sourceCandidates(_ results: [NovelSearchBook], for current: NovelBook) async throws -> [NovelSearchBook] {
        let identity = try await identityDetails(current)
        var candidates: [NovelSearchBook] = []
        for result in results {
            let next = try await identityDetails(result.book)
            if identity.name == next.name, identity.author.isEmpty || next.author.isEmpty || identity.author == next.author {
                candidates.append(result)
            }
        }
        return candidates
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
    public func validate(_ rule: NovelRule) async throws {
        let output = try await invoke(["operation": .string("validateRule"), "rule": try .encoding(rule)])
        guard output["valid"].bool == true else {
            throw NovelError.invalidRule(output["errors"].array?.compactMap(\.string) ?? ["净化规则无效"])
        }
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
