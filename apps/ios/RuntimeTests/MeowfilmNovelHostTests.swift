import XCTest
import UIKit
import Network
import MeowRuntime
import MeowStorage
@testable import MeowNovelFeature
@testable import Meowfilm

@MainActor
final class MeowfilmNovelHostTests: XCTestCase {
    func testRulesHTTPWaitsBeyondSixtySecondsAndDrainsOnClose() async throws {
        let server = try NovelDelayedRulesHTTPFixture()
        addTeardownBlock { await server.stop() }
        let baseURL = try await server.start()
        let client = try RuntimeHTTPClient(baseURL: baseURL)
        addTeardownBlock { await client.close() }
        let request: RuntimeValue = .object(["request": .object([
            "operation": .string("matchUrl"), "pattern": .string("https://fixture.invalid/.*"),
            "url": .string("https://fixture.invalid/book")
        ])])
        let started = ProcessInfo.processInfo.systemUptime
        let result = try await client.call(module: .source, operation: "rules.process", parameters: request)
        let elapsed = ProcessInfo.processInfo.systemUptime - started
        XCTAssertEqual(result, .bool(true))
        XCTAssertGreaterThanOrEqual(elapsed, 65, "The real iOS transport must not fall back to a 60-second deadline")
        XCTAssertEqual(server.requests, [request])
        await client.close()
        XCTAssertTrue(client.isClosed)
        await server.stop()
        XCTAssertEqual(server.activeConnections, 0)
        print("novel rules real HTTP elapsed=\(elapsed) seconds; client and socket drained")
    }

    func testActualAppRulesRPCUsesCurrentSourceAndFilmMethodsAndKeepsNativeContentMetadata() async throws {
        let resources = try await hostResources(), client = try await resources.scriptSession.connection()
        let description = try await client.describe()
        let modules = try XCTUnwrap(description["modules"].array)
        let sourceModule = try XCTUnwrap(modules.first { $0["id"] == .string("source") })
        XCTAssertEqual(sourceModule["version"], .string("0.3.0"))
        XCTAssertEqual(sourceModule["contract"], .string("source.runtime.v2"))
        let sourceMethods = ["load", "unload", "invoke", "status", "close",
            "library.list", "library.get", "library.import", "library.save", "library.update", "library.remove",
            "library.export", "library.invoke", "library.image", "library.releaseImage", "web.pending", "web.respond",
            "rules.process"].map { "source.runtime." + $0 }
        XCTAssertEqual(sourceModule["methods"].array?.compactMap(\.string), sourceMethods.sorted())
        let film = try XCTUnwrap(modules.first { $0["id"] == .string("film") })
        let filmMethods = ["session.open", "session.command", "session.snapshot", "session.release", "session.attempt",
            "session.media.release", "settings.get", "settings.set", "library.get", "library.remove", "manual.update",
            "data.catalog", "data.search", "data.detail", "data.metadata", "data.metadata.entry", "data.cache.entry", "data.invalidate",
            "rules.get", "rules.set", "rules.restore", "rules.preview", "rules.normalize", "rules.clear",
            "search.open", "search.snapshot", "search.command", "search.release", "search.handoff"].map { "film." + $0 }
        XCTAssertEqual(film["methods"].array?.compactMap(\.string), filmMethods.sorted())
        let source = NovelSource(installationId: "rules-fixture", revision: 1, name: "fixture",
            sourceUrl: "https://fixture.invalid/", group: "", order: 0, enabled: true, enabledExplore: false,
            protocol: "legado", actions: [])
        let book = NovelBook(installationId: source.id, bookUrl: "book", name: "猫", author: "林")
        let original = NovelContent(title: "广告发", blocks: [.init(text: "广告发"),
            .init(src: "https://fixture.invalid/发.png", alt: "发")], imageStyle: "full")
        var rule = NovelRule(); rule.pattern = "广告"; rule.isRegex = false; rule.scopeTitle = true
        let result = try await resources.novelRules.transform(original, book: book, source: source, rules: [rule], conversion: "traditional")
        XCTAssertEqual(result.title, "發"); XCTAssertEqual(result.blocks, [.init(text: "發"), original.blocks[1]])
        XCTAssertEqual(result.imageStyle, "full"); XCTAssertEqual(original.title, "广告发")
        let matched = try await resources.novelRules.matchesURL("https://fixture.invalid/book", pattern: #"https://fixture\.invalid/.*"#)
        XCTAssertTrue(matched)
        let partial = try await resources.novelRules.matchesURL("https://fixture.invalid/book", pattern: #"fixture\.invalid"#)
        XCTAssertFalse(partial, "URL matching remains a full-string match")
        _ = try await resources.novelRules.identity(book)
        _ = try await client.describe()
        XCTAssertFalse(client.isClosed)
    }

    func testActualAppLibrarySearchDetailCatalogContentPrivateValuesAndRevisionRetirement() async throws {
        let resources = try await hostResources(), client = resources.novelRuntime
        let sources = NovelSourceClient(runtime: client)
        let suffix = UUID().uuidString.lowercased(), url = "https://ios-novel-\(suffix).invalid/"
        let receipt = try await sources.importSources(text: novelHostDefinition(url: url))
        XCTAssertTrue(receipt.failures.isEmpty)
        let source = try XCTUnwrap(receipt.sources.first)
        do {
            let books = try await sources.search(source, keyword: "雨夜来信", page: 1)
            let book = try XCTUnwrap(books.first)
            XCTAssertEqual(book.bookUrl, url + "book")
            let info = try await sources.detail(book, source: source)
            let catalog = try await sources.chapters(info, source: source)
            XCTAssertEqual(catalog.chapters.count, 2)
            let body = try await sources.content(catalog.book, source: source, chapter: catalog.chapters[0], nextChapterURL: catalog.chapters[1].chapterUrl)
            XCTAssertEqual(body.content.blocks.first?.text, "正文 1")
            let second = try await sources.content(catalog.book, source: source, chapter: catalog.chapters[0], nextChapterURL: catalog.chapters[1].chapterUrl)
            XCTAssertEqual(second.content.blocks.first?.text, "正文 2")
            let definition = try await sources.definition(source)
            XCTAssertEqual(try RuntimeJSON.decode(Data(definition.utf8)),
                try RuntimeJSON.decode(Data(novelHostDefinition(url: url).utf8)), "Private runtime state cannot rewrite the installed definition")
            try await sources.update(source, patch: ["enabled": .bool(false)])
            do { _ = try await sources.search(source, keyword: "迟到", page: 1); XCTFail("An old revision must not remain callable") }
            catch { XCTAssertEqual(error as? RuntimeHTTPError, .remote("STALE_SOURCE")) }
            let installed = try await sources.list()
            let latest = try XCTUnwrap(installed.first { $0.id == source.id })
            try await sources.remove(latest)
        } catch {
            if let latest = try? await sources.list().first(where: { $0.id == source.id }) { try? await sources.remove(latest) }
            throw error
        }
    }

    func testActualAppWebBrokerScalarCookieIsolationAndRetiredPendingResponse() async throws {
        let resources = try await hostResources(), sources = NovelSourceClient(runtime: resources.novelRuntime)
        // Use a separate borrowing client, so the UI broker cannot consume this
        // deterministic fixture's pending request before the host test sees it.
        let client = try await resources.scriptSession.connection()
        let url = "https://ios-web-\(UUID().uuidString.lowercased()).invalid/"
        let definition = #"{"bookSourceName":"Web fixture","bookSourceUrl":"\#(url)","loginUrl":"\#(url)login","ruleContent":{"content":"article@text"}}"#
        let receipt = try await sources.importSources(text: definition)
        let source = try XCTUnwrap(receipt.sources.first)
        var call: Task<RuntimeValue, Error>?
        var outcome: Result<RuntimeValue, Error>?
        func login() -> Task<RuntimeValue, Error> {
            outcome = nil
            return Task {
                do {
                    let value = try await client.call(module: .source, operation: "library.invoke", parameters: .object([
                        "installationId": .string(source.id), "revision": .number(Double(source.revision)), "action": .string("login"), "payload": .object([:])]))
                    outcome = .success(value)
                    return value
                } catch {
                    outcome = .failure(error)
                    throw error
                }
            }
        }
        do {
            call = login()
            let request = try await pending(client, outcome: { outcome })
            XCTAssertEqual(request["installationId"], .string(source.id))
            _ = try await client.call(module: .source, operation: "web.respond", parameters: .object(["id": request["id"], "result": .object([
                "value": .string("accepted"), "url": .string(url + "login"), "headers": .object(["set-cookie": .array([])]),
                "cookieChanges": .array([.object(["url": .string(url + "private"), "setCookie": .string("fixture=private; HttpOnly; Path=/private; SameSite=Lax")])])])]))
            let returned = try await call!.value
            XCTAssertEqual(returned["value"], .string("accepted"))
            call = login()
            let next = try await pending(client, outcome: { outcome })
            let seed = try XCTUnwrap(next["request"]["cookies"].array?.first { $0["name"] == .string("fixture") })
            XCTAssertEqual(seed["domain"], .string(URL(string: url)!.host!))
            XCTAssertEqual(seed["hostOnly"], .bool(true)); XCTAssertEqual(seed["path"], .string("/private"))
            XCTAssertEqual(seed["httpOnly"], .bool(true)); XCTAssertEqual(seed["sameSite"], .string("Lax"))
            XCTAssertFalse(next["request"]["headers"]["Cookie"].string?.contains("fixture=private") == true)
            try await sources.update(source, patch: ["enabled": .bool(false)])
            do { _ = try await call!.value; XCTFail("Retiring a source must cancel its Web invocation") } catch { }
            do {
                _ = try await client.call(module: .source, operation: "web.respond", parameters: .object(["id": next["id"], "result": .object([
                    "value": .bool(true), "url": .string(url + "login"), "headers": .object(["set-cookie": .array([.string("late=discarded; Path=/")])])])]))
                XCTFail("A retired pending response cannot write a Cookie")
            } catch { XCTAssertEqual(error as? RuntimeHTTPError, .remote("STALE_SOURCE")) }
            let installed = try await sources.list()
            let latest = try XCTUnwrap(installed.first { $0.id == source.id }); try await sources.remove(latest)
        } catch {
            call?.cancel(); _ = await call?.result
            if let latest = try? await sources.list().first(where: { $0.id == source.id }) { try? await sources.remove(latest) }
            throw error
        }
    }
    private func pending(_ client: RuntimeHTTPClient, outcome: () -> Result<RuntimeValue, Error>?) async throws -> RuntimeValue {
        let deadline = ContinuousClock.now.advanced(by: .seconds(5))
        while ContinuousClock.now < deadline {
            let result = try await client.call(module: .source, operation: "web.pending")
            if let request = result["requests"].array?.first { return request }
            if let completed = outcome() {
                _ = try completed.get()
                XCTFail("The source completed before its pending interaction was answered")
                throw NovelError.invalidResponse
            }
            try await Task.sleep(nanoseconds: 20_000_000)
        }
        XCTFail("The actual source action did not publish Web pending"); throw NovelError.invalidResponse
    }
}

// Run these two classes in separate App processes on the same simulator. No
// in-process client recreation can prove the product's dataRoot assembly.
@MainActor
final class MeowfilmNovelColdStartPrepareTests: XCTestCase {
    func testPrepareLibraryAndPrivateStateForNextAppProcess() async throws {
        guard ProcessInfo.processInfo.environment["MEOWFILM_NOVEL_COLD_PHASE"] == "prepare" else {
            throw XCTSkip("Requires the documented two-process novel cold-start procedure")
        }
        let resources = try await hostResources(), client = NovelSourceClient(runtime: resources.novelRuntime)
        for (index, url) in novelColdURLs.enumerated() {
            if let old = try await client.list().first(where: { $0.sourceUrl == url }) { try await client.remove(old) }
            let receipt = try await client.importSources(text: novelHostDefinition(url: url))
            XCTAssertTrue(receipt.failures.isEmpty)
            let source = try XCTUnwrap(receipt.sources.first)
            let books = try await client.search(source, keyword: "雨夜来信", page: 1)
            let book = try XCTUnwrap(books.first)
            let detail = try await client.detail(book, source: source), catalog = try await client.chapters(detail, source: source)
            let body = try await client.content(catalog.book, source: source, chapter: catalog.chapters[0], nextChapterURL: catalog.chapters[1].chapterUrl)
            XCTAssertEqual(body.content.blocks.first?.text, "正文 1")
            try await client.update(source, patch: ["enabled": .bool(index == 0), "enabledExplore": .bool(false), "group": .string("cold-fixture"), "order": .number(Double(-9 - index))])
            if index == 0 {
                let key = try await resources.novelRules.identity(detail)
                let local = try await resources.novelLibrary.retain(detail, identity: key, shelf: true)
                try await resources.novelLibrary.record(local, identity: key, anchor: .init(chapterUrl: catalog.chapters[0].chapterUrl, chapterTitle: "第一章 雨声", chapterIndex: 0, utf16Offset: 2, ratio: 0.4))
            }
        }
        let process = NovelColdProcessMarker(uuid: novelHostProcessUUID, pid: ProcessInfo.processInfo.processIdentifier)
        try JSONEncoder().encode(process).write(to: novelColdMarkerURL(), options: .atomic)
        print("novel cold phase=prepare pid=\(process.pid)")
    }
}
@MainActor
final class MeowfilmNovelColdStartRestoreTests: XCTestCase {
    func testActualAppColdStartRestoresLibraryPrivateValuesMembershipAndAnchor() async throws {
        guard ProcessInfo.processInfo.environment["MEOWFILM_NOVEL_COLD_PHASE"] == "restore" else {
            throw XCTSkip("Requires the preceding prepare process on this test simulator")
        }
        let markerURL = try novelColdMarkerURL()
        let previous = try JSONDecoder().decode(NovelColdProcessMarker.self, from: Data(contentsOf: markerURL))
        defer { try? FileManager.default.removeItem(at: markerURL) }
        XCTAssertNotEqual(previous.uuid, novelHostProcessUUID, "Restore must run in a different actual App process, not another client")
        XCTAssertNotEqual(previous.pid, ProcessInfo.processInfo.processIdentifier)
        print("novel cold phase=restore pid=\(ProcessInfo.processInfo.processIdentifier) preparePid=\(previous.pid)")
        let resources = try await hostResources(), client = NovelSourceClient(runtime: resources.novelRuntime)
        for (index, url) in novelColdURLs.enumerated() {
            let installed = try await client.list()
            var source = try XCTUnwrap(installed.first { $0.sourceUrl == url })
            XCTAssertEqual(source.enabled, index == 0)
            XCTAssertFalse(source.enabledExplore); XCTAssertEqual(source.group, "cold-fixture"); XCTAssertEqual(source.order, Double(-9 - index))
            if !source.enabled {
                try await client.update(source, patch: ["enabled": .bool(true)])
                let updated = try await client.list()
                source = try XCTUnwrap(updated.first { $0.id == source.id })
            }
            let books = try await client.search(source, keyword: "雨夜来信", page: 1)
            let book = try XCTUnwrap(books.first)
            let catalog = try await client.chapters(book, source: source)
            let body = try await client.content(book, source: source, chapter: catalog.chapters[0], nextChapterURL: catalog.chapters[1].chapterUrl)
            XCTAssertEqual(body.content.blocks.first?.text, "正文 2", "The source-private value must survive a real process boundary")
            try await client.remove(source)
        }
        let state = try await resources.novelLibrary.snapshot()
        let entry = try XCTUnwrap(state.books(history: true).first { $0.book.bookUrl == novelColdURLs[0] + "book" })
        XCTAssertTrue(entry.onShelf); XCTAssertEqual(entry.anchor?.utf16Offset, 2); XCTAssertEqual(entry.anchor?.ratio, 0.4)
        XCTAssertEqual(state.books(history: false).first { $0.id == entry.id }?.book.id, entry.book.id)
        try await resources.novelLibrary.setShelf(entry.id, present: false)
        try await resources.novelLibrary.deleteHistory(entry.id)
    }
}
/// A silent HTTP peer models shared Node queue wait, not expensive rule execution.
private final class NovelDelayedRulesHTTPFixture: @unchecked Sendable {
    private let queue = DispatchQueue(label: "meowfilm.novel-delayed-rules-http")
    private let listener: NWListener
    private let drain = DispatchGroup()
    private var connections: [ObjectIdentifier: NWConnection] = [:]
    private var responses: [ObjectIdentifier: DispatchWorkItem] = [:]
    private var recorded: [RuntimeValue] = []
    private var stopping = false
    var requests: [RuntimeValue] { queue.sync { recorded } }
    var activeConnections: Int { queue.sync { connections.count } }

    init() throws {
        let parameters = NWParameters.tcp
        parameters.requiredLocalEndpoint = .hostPort(host: "127.0.0.1", port: .any)
        listener = try NWListener(using: parameters)
    }

    func start() async throws -> URL {
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<URL, Error>) in
            var starting: CheckedContinuation<URL, Error>? = continuation
            drain.enter()
            listener.stateUpdateHandler = { [weak self] state in
                guard let self else { return }
                switch state {
                case .ready:
                    starting?.resume(returning: URL(string: "http://127.0.0.1:\(self.listener.port!.rawValue)")!)
                    starting = nil
                case .failed(let error):
                    starting?.resume(throwing: error); starting = nil; self.listener.cancel()
                case .cancelled:
                    starting?.resume(throwing: CancellationError()); starting = nil; self.drain.leave()
                default: break
                }
            }
            listener.newConnectionHandler = { [weak self] in self?.accept($0) }
            listener.start(queue: queue)
        }
    }

    func stop() async {
        await withCheckedContinuation { continuation in
            queue.async {
                self.stopping = true
                for item in self.responses.values { item.cancel() }
                self.responses.removeAll()
                for connection in self.connections.values { connection.cancel() }
                self.listener.cancel()
                self.drain.notify(queue: self.queue) { continuation.resume() }
            }
        }
    }

    private func accept(_ connection: NWConnection) {
        let id = ObjectIdentifier(connection)
        connections[id] = connection; drain.enter()
        connection.stateUpdateHandler = { [weak self] state in
            guard let self else { return }
            switch state {
            case .failed: connection.cancel()
            case .cancelled:
                self.responses.removeValue(forKey: id)?.cancel()
                self.connections[id] = nil
                connection.stateUpdateHandler = nil
                self.drain.leave()
            default: break
            }
        }
        connection.start(queue: queue)
        if stopping { connection.cancel() }
        else { receive(connection, data: Data()) }
    }

    private func receive(_ connection: NWConnection, data: Data) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 16 * 1024) { [weak self] chunk, _, ended, error in
            guard let self else { return }
            var bytes = data; if let chunk { bytes.append(chunk) }
            if let separator = bytes.range(of: Data("\r\n\r\n".utf8)),
               let head = String(data: bytes[..<separator.lowerBound], encoding: .utf8) {
                let lines = head.components(separatedBy: "\r\n")
                let fields = lines.dropFirst().compactMap { line -> (String, String)? in
                    guard let colon = line.firstIndex(of: ":") else { return nil }
                    return (String(line[..<colon]).lowercased(),
                            String(line[line.index(after: colon)...]).trimmingCharacters(in: .whitespaces))
                }
                let headers = Dictionary(fields, uniquingKeysWith: { _, last in last })
                guard lines.first?.hasPrefix("POST /v1/source/rules.process HTTP/") == true,
                      let requestID = headers["x-meow-request-id"],
                      let length = headers["content-length"].flatMap(Int.init) else {
                    connection.cancel(); return
                }
                if bytes.count - separator.upperBound >= length {
                    guard let request = try? RuntimeJSON.decode(Data(bytes[separator.upperBound..<(separator.upperBound + length)])) else {
                        connection.cancel(); return
                    }
                    self.recorded.append(request)
                    let response = DispatchWorkItem { [weak self, weak connection] in
                        guard let self, let connection, !self.stopping else { return }
                        let body = Data(#"{"ok":true,"contract":"source.runtime.v2","value":true}"#.utf8)
                        let headers = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: \(body.count)\r\nX-Meow-Request-Id: \(requestID)\r\nConnection: close\r\n\r\n"
                        var output = Data(headers.utf8); output.append(body)
                        connection.send(content: output, contentContext: .finalMessage, isComplete: true,
                                        completion: .contentProcessed { _ in connection.cancel() })
                    }
                    self.responses[ObjectIdentifier(connection)] = response
                    self.queue.asyncAfter(deadline: .now() + 68, execute: response)
                    return
                }
            }
            if ended || error != nil { connection.cancel() }
            else { self.receive(connection, data: bytes) }
        }
    }
}

private let novelHostProcessUUID = UUID().uuidString
private struct NovelColdProcessMarker: Codable {
    var uuid: String
    var pid: Int32
}
private func novelColdMarkerURL() throws -> URL {
    let directory = try FileManager.default.url(for: .applicationSupportDirectory, in: .userDomainMask, appropriateFor: nil, create: true)
    return directory.appendingPathComponent("meowfilm-novel-cold-test-process.json")
}
private let novelColdURLs = ["https://ios-novel-cold-enabled.invalid/", "https://ios-novel-cold-disabled.invalid/"]
private func novelHostDefinition(url: String) throws -> String {
    let html = """
    <script>
    const base = '\(url)';
    function search(key, page) { return page === 1 ? [{ bookUrl:base+'book', tocUrl:base+'toc', name:'雨夜来信', author:'林舟' }] : []; }
    function info() { return { bookUrl:base+'book', tocUrl:base+'toc', name:'雨夜来信', author:'林舟', intro:'合成宿主正文' }; }
    function chapter() { return [{name:'第一章 雨声',chapterUrl:base+'1'},{name:'第二章 来信',chapterUrl:base+'2'}]; }
    async function content() {
      const bridge = window.flutter_inappwebview;
      const n = Number(await bridge.callHandler('cacheGet','private-counter') || 0) + 1;
      await bridge.callHandler('cacheSet','private-counter',n);
      return '正文 ' + n;
    }
    function getfinds() { return [{ title:'全部', url:base+'find' }]; }
    function find() { return search('雨夜来信',1); }
    </script>
    """
    let data = try JSONSerialization.data(withJSONObject: ["bookSourceName": "Native novel fixture", "bookSourceUrl": url, "enabledExplore": true, "html": html])
    return String(decoding: data, as: UTF8.self)
}
