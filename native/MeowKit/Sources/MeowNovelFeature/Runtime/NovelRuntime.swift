import Foundation
import MeowRuntime

public protocol NovelRuntimeServing: Sendable {
    func call(_ operation: String, _ parameters: RuntimeValue) async throws -> RuntimeValue
    func image(_ reference: String) async throws -> Data
    func hasActiveCalls() async -> Bool
    func setActivityHandler(_ handler: (@Sendable (Bool) -> Void)?) async
    func close() async throws
}

/// Borrows the App HTTP client. This owner drains its accepted operations,
/// but never shuts down the client used by music and film.
public actor NovelRuntime: NovelRuntimeServing {
    private let connection: @Sendable () async throws -> RuntimeHTTPClient
    private var pending: [UUID: (operation: String, task: Task<RuntimeValue, Error>)] = [:]
    private var images: [UUID: Task<Data, Error>] = [:]
    private var references: Set<String> = []
    private var releases: [String: Task<RuntimeValue, Error>] = [:]
    private var closing: Task<Void, Error>?
    private var activeCalls: Set<UUID> = []
    private var activityHandler: (@Sendable (Bool) -> Void)?
    private var closed = false
    public init(connection: @escaping @Sendable () async throws -> RuntimeHTTPClient) { self.connection = connection }
    public func hasActiveCalls() -> Bool { !activeCalls.isEmpty }
    public func setActivityHandler(_ handler: (@Sendable (Bool) -> Void)?) {
        activityHandler = handler; handler?(!activeCalls.isEmpty)
    }
    public func call(_ operation: String, _ parameters: RuntimeValue = .object([:])) async throws -> RuntimeValue {
        let releasing = operation == "library.releaseImage"
        let reference = parameters["reference"].string
        if releasing, let reference {
            if let task = releases[reference] { return try await task.value }
            if closed, !references.contains(reference) { throw NovelError.closed }
        }
        guard !closed || releasing else { throw NovelError.closed }
        try Task.checkCancellation()
        let id = UUID(), connection = connection
        let task = Task {
            let value = try await connection().call(module: .source, operation: operation, parameters: parameters)
            // Once HTTP has returned an accepted receipt, the lease must become
            // owned before cancellation or close can discard the caller.
            if operation == "library.image", let reference = value["reference"].string { references.insert(reference) }
            if releasing, value["released"] == .bool(true), let reference {
                references.remove(reference)
            }
            return value
        }
        pending[id] = (operation, task)
        if releasing, let reference { releases[reference] = task }
        if !operation.hasPrefix("web.") { activeCalls.insert(id); activityHandler?(true) }
        defer {
            pending[id] = nil
            // During shutdown, a late image consumer reuses this exact receipt,
            // including failure, rather than starting work after close returned.
            if releasing, !closed, let reference { releases[reference] = nil }
            if activeCalls.remove(id) != nil, activeCalls.isEmpty { activityHandler?(false) }
        }
        let value = try await withTaskCancellationHandler { try await task.value } onCancel: { task.cancel() }
        if operation == "library.image" || releasing { return value }
        try Task.checkCancellation()
        guard !closed else { throw NovelError.closed }
        return value
    }
    public func image(_ reference: String) async throws -> Data {
        guard !closed else { throw NovelError.closed }
        let id = UUID(), connection = connection
        let task = Task { try await connection().readSourceImage(reference) }
        images[id] = task; defer { images[id] = nil }
        let value = try await withTaskCancellationHandler { try await task.value } onCancel: { task.cancel() }
        try Task.checkCancellation()
        guard !closed else { throw NovelError.closed }
        return value
    }
    public func close() async throws {
        if let closing { try await closing.value; return }
        closed = true
        let accepted = Array(pending.values), acceptedImages = Array(images.values)
        let task = Task {
            for item in accepted where item.operation != "library.releaseImage" { item.task.cancel() }
            for task in acceptedImages { task.cancel() }
            for item in accepted { _ = await item.task.result }
            for task in acceptedImages { _ = await task.result }
            var failed = false
            for reference in Array(references) {
                do {
                    let value = try await call("library.releaseImage", .object(["reference": .string(reference)]))
                    guard value["released"] == .bool(true) else { throw NovelError.invalidResponse }
                } catch { failed = true }
            }
            pending.removeAll(); images.removeAll(); activeCalls.removeAll()
            activityHandler?(false); activityHandler = nil
            if failed { throw NovelError.imageReleaseFailed }
        }
        closing = task; try await task.value
    }
}

public struct NovelExploreKind: Equatable, Sendable {
    public var title: String
    public var url: String
}
public struct NovelImportFailure: Codable, Equatable, Sendable {
    public var index: Int
    public var name: String
    public var code: String
}
public struct NovelImportReceipt: Codable, Sendable {
    public var sources: [NovelSource]
    public var failures: [NovelImportFailure]
}
public struct NovelCatalog: Sendable {
    public var book: NovelBook
    public var chapters: [NovelChapter]
}
public struct NovelChapterResponse: Sendable {
    public var chapter: NovelChapter
    public var content: NovelContent
}

public struct NovelSourceClient: Sendable {
    public let runtime: any NovelRuntimeServing
    public init(runtime: any NovelRuntimeServing) { self.runtime = runtime }
    public func list() async throws -> [NovelSource] {
        try await runtime.call("library.list", .object([:]))["sources"].decoding([NovelSource].self)
    }
    public func invoke(_ source: NovelSource, action: String, payload: RuntimeValue = .object([:])) async throws -> RuntimeValue {
        guard source.enabled else { throw NovelError.noSource }
        return try await runtime.call("library.invoke", .object(["installationId": .string(source.id),
            "revision": .number(Double(source.revision)), "action": .string(action), "payload": payload]))
    }
    public func search(_ source: NovelSource, keyword: String, page: Int) async throws -> [NovelBook] {
        let value = try await invoke(source, action: "search", payload: .object(["keyword": .string(keyword), "page": .number(Double(page))]))
        guard let books = value["books"].array else { throw NovelError.invalidResponse }
        return try books.map { try NovelBook.parse($0, source: source) }
    }
    public func kinds(_ source: NovelSource) async throws -> [NovelExploreKind] {
        let value = try await invoke(source, action: "exploreKinds")
        guard let items = value["kinds"].array else { throw NovelError.invalidResponse }
        return items.map { .init(title: $0["title"].string ?? "", url: $0["url"].string ?? "") }
    }
    public func explore(_ source: NovelSource, url: String, page: Int) async throws -> [NovelBook] {
        let value = try await invoke(source, action: "explore", payload: .object(["url": .string(url), "page": .number(Double(page))]))
        guard let items = value["books"].array else { throw NovelError.invalidResponse }
        return try items.map { try NovelBook.parse($0, source: source) }
    }
    public func detail(_ book: NovelBook, source: NovelSource) async throws -> NovelBook {
        let value = try await invoke(source, action: "bookInfo", payload: payload(book))
        return try NovelBook.parse(value["book"], source: source, id: book.id, fallback: book)
    }
    public func chapters(_ book: NovelBook, source: NovelSource) async throws -> NovelCatalog {
        let value = try await invoke(source, action: "toc", payload: payload(book))
        guard let values = value["chapters"].array else { throw NovelError.invalidResponse }
        let updated = value["book"] == .null ? book : try NovelBook.parse(value["book"], source: source, id: book.id, fallback: book)
        return try .init(book: updated, chapters: values.enumerated().map { try NovelChapter.parse($0.element, index: $0.offset) })
    }
    public func content(_ book: NovelBook, source: NovelSource, chapter: NovelChapter, nextChapterURL: String?) async throws -> NovelChapterResponse {
        var fields = payload(book, chapter: chapter)
        if case .object(var object) = fields {
            object["nextChapterUrl"] = .string(nextChapterURL ?? ""); fields = .object(object)
        }
        let value = try await invoke(source, action: "content", payload: fields)
        guard let blocks = value["blocks"].array else { throw NovelError.invalidResponse }
        let resolved = value["chapter"] == .null ? chapter : try NovelChapter.parse(value["chapter"], index: chapter.index, fallback: chapter)
        return try .init(chapter: resolved, content: .init(title: value["title"].string ?? chapter.name,
            blocks: RuntimeValue.array(blocks).decoding([NovelBlock].self), imageStyle: value["imageStyle"].string ?? ""))
    }
    public func payload(_ book: NovelBook, chapter: NovelChapter? = nil) -> RuntimeValue {
        var fields: RuntimeObject = ["book": book.wire, "bookUrl": .string(book.bookUrl),
                                    "tocUrl": .string(book.tocUrl), "variable": book.variable]
        if let chapter { fields["chapter"] = chapter.wire; fields["chapterUrl"] = .string(chapter.chapterUrl) }
        return .object(fields)
    }
    public func definition(_ source: NovelSource) async throws -> String {
        let result = try await runtime.call("library.get", .object(["installationId": .string(source.id)]))
        guard let definition = result["definition"].string else { throw NovelError.invalidResponse }; return definition
    }
    public func importSources(text: String? = nil, url: String? = nil, fileName: String? = nil) async throws -> NovelImportReceipt {
        var fields = RuntimeObject()
        if let text { fields["text"] = .string(text) }; if let url { fields["url"] = .string(url) }
        if let fileName { fields["fileName"] = .string(fileName) }
        return try await runtime.call("library.import", .object(fields)).decoding(NovelImportReceipt.self)
    }
    public func save(_ source: NovelSource, definition: String) async throws {
        _ = try await runtime.call("library.save", .object(["installationId": .string(source.id),
            "expectedRevision": .number(Double(source.revision)), "definition": .string(definition)]))
    }
    public func update(_ source: NovelSource, patch: RuntimeObject) async throws {
        _ = try await runtime.call("library.update", .object(["installationId": .string(source.id),
            "expectedRevision": .number(Double(source.revision)), "patch": .object(patch)]))
    }
    public func remove(_ source: NovelSource) async throws {
        _ = try await runtime.call("library.remove", .object(["installationId": .string(source.id),
            "expectedRevision": .number(Double(source.revision))]))
    }
    public func export(_ sources: [NovelSource]) async throws -> String {
        let value = try await runtime.call("library.export", .object(["installationIds": .array(sources.map { .string($0.id) })]))
        guard let text = value["text"].string else { throw NovelError.invalidResponse }; return text
    }
    public func readImage(source: NovelSource, book: NovelBook, url: String, kind: String) async throws -> Data {
        let receipt = try await runtime.call("library.image", .object(["installationId": .string(source.id),
            "revision": .number(Double(source.revision)), "url": .string(url), "bookUrl": .string(book.bookUrl), "kind": .string(kind)]))
        guard let reference = receipt["reference"].string else { throw NovelError.invalidResponse }
        let result: Result<Data, Error>
        do { try Task.checkCancellation(); result = .success(try await runtime.image(reference)) }
        catch { result = .failure(error) }
        // Release is accepted work, not a child of the cancelled page. Its
        // receipt is checked and drained; a failure cannot be reported as success.
        do {
            let releaseReceipt = try await Task { try await runtime.call("library.releaseImage", .object(["reference": .string(reference)])) }.value
            guard releaseReceipt["released"] == .bool(true) else { throw NovelError.invalidResponse }
        } catch { throw NovelError.imageReleaseFailed }
        try Task.checkCancellation()
        return try result.get()
    }
}
