#if os(iOS)
import XCTest
import UIKit
import WebKit
import Network
import MeowUI
import MeowRuntime
@testable import MeowNovelFeature
@testable import Meowfilm

@MainActor
final class NovelWebUIKitTests: XCTestCase {
    private var window: UIWindow?
    private var previousWindow: UIWindow?
    private var tasks: [Task<Void, Never>] = []
    private var servers: [NovelWebHTTPFixture] = []
    private var rules: NovelRuleProcessor!
    override func setUp() async throws {
        try await super.setUp()
        let resources = try await hostResources()
        rules = NovelRuleProcessor(runtime: resources.novelRuntime)
    }
    override func tearDown() async throws {
        for task in tasks { task.cancel() }
        for task in tasks { await task.value }
        await rules?.close(); rules = nil
        if let root = window?.rootViewController { PageViewController.invalidatePages(in: root) }
        window?.isHidden = true; window?.rootViewController = nil; window = nil
        previousWindow?.makeKey(); previousWindow = nil
        for server in servers { server.stop() }; servers = []; tasks = []
        try await super.tearDown()
    }
    private func presenter() throws -> UIViewController {
        let scene = try XCTUnwrap(UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first { $0.activationState == .foregroundActive })
        previousWindow = scene.windows.first { $0.isKeyWindow }
        let window = UIWindow(windowScene: scene); window.frame = scene.coordinateSpace.bounds; window.windowLevel = .normal + 1
        let page = PageViewController(themeProvider: NovelTestPageTheme())
        window.rootViewController = page; window.makeKeyAndVisible(); window.layoutIfNeeded(); self.window = window
        return page
    }
    private func wait(_ condition: @escaping @MainActor () -> Bool) async {
        let done = expectation(for: NSPredicate { _, _ in MainActor.assumeIsolated { condition() } }, evaluatedWith: nil)
        await fulfillment(of: [done], timeout: 8); XCTAssertTrue(condition())
    }
    private func views(_ view: UIView) -> [UIView] { [view] + view.subviews.flatMap(views) }
    private func button(_ id: String, _ page: NovelWebViewController) throws -> UIButton {
        try XCTUnwrap(views(page.view).first { $0.accessibilityIdentifier == id } as? UIButton)
    }
    private func store(_ page: NovelWebViewController) async throws -> [HTTPCookie] {
        let store = try XCTUnwrap(page.webView).configuration.websiteDataStore.httpCookieStore
        return await withCheckedContinuation { continuation in store.getAllCookies { continuation.resume(returning: $0) } }
    }
    private func run(_ page: NovelWebViewController, on presenter: UIViewController,
                     action: (@MainActor () async throws -> Void)? = nil) async throws -> RuntimeValue {
        var outcome: Result<RuntimeValue, Error>?
        let task = Task {
            do { outcome = .success(try await page.result(from: presenter)) }
            catch { outcome = .failure(error) }
        }; tasks.append(task)
        do { try await action?() } catch { task.cancel(); await task.value; throw error }
        await wait { outcome != nil }
        if outcome == nil { task.cancel() }
        await task.value
        return try XCTUnwrap(outcome).get()
    }
    private func server(_ handler: @escaping @Sendable (String, String) -> NovelWebHTTPFixture.Response) async throws -> NovelWebHTTPFixture {
        let value = try NovelWebHTTPFixture(handler: handler); servers.append(value); try await value.start(); return value
    }
    private func page(mode: String, request: RuntimeObject, installation: String = "fixture") -> NovelWebViewController {
        return NovelWebViewController(request: .init(id: UUID().uuidString, installationId: installation, mode: mode, request: .object(request)), rules: rules, themeProvider: NovelTestPageTheme())
    }
    private func cookie(_ name: String, value: String = "one", domain: String, hostOnly: Bool = true,
                        path: String = "/", secure: Bool = false, httpOnly: Bool = false,
                        sameSite: String? = nil, expires: Double? = nil) -> RuntimeValue {
        .object(["name": .string(name), "value": .string(value), "domain": .string(domain), "hostOnly": .bool(hostOnly),
            "path": .string(path), "secure": .bool(secure), "httpOnly": .bool(httpOnly),
            "sameSite": sameSite.map(RuntimeValue.string) ?? .null, "expires": expires.map(RuntimeValue.number) ?? .null])
    }
    func testRealWebKitSeedsKeepPrivateHostOnlyDomainPrefixAndUnchangedCookiesDoNotWriteBack() async throws {
        let server = try await server { _, _ in .init(html: "<html><title>Cookie fixture</title></html>") }
        let presenter = try presenter()
        let expires = Date().timeIntervalSince1970.rounded(.down) + 30 * 24 * 60 * 60
        let seeds = [cookie("private", domain: "127.0.0.1", path: "/private", httpOnly: true, sameSite: "Lax", expires: expires * 1000),
            cookie("domain", domain: "books.invalid", hostOnly: false, path: "/private", sameSite: "Strict"),
            cookie("__Host-key", domain: "secure.books.invalid", secure: true, httpOnly: true, sameSite: "None")]
        let page = page(mode: "login", request: ["url": .string(server.base + "/private/plain"), "cookies": .array(seeds),
            "headers": .object(["COOKIE": .string("phantom=must-not-seed")])])
        let result = try await run(page, on: presenter) {
            await self.wait { page.webView?.url?.path == "/private/plain" && page.webView?.isLoading == false }
            let actual = NovelWebCookie.snapshot(try await self.store(page),
                preserving: try seeds.map { try NovelWebCookie(seed: $0) })
            XCTAssertEqual(actual.count, 3)
            let privateCookie = try XCTUnwrap(actual.first { $0.key.name == "private" })
            XCTAssertEqual(privateCookie.key.path, "/private"); XCTAssertTrue(privateCookie.key.hostOnly)
            XCTAssertTrue(privateCookie.httpOnly); XCTAssertEqual(privateCookie.sameSite, "Lax")
            XCTAssertEqual(privateCookie.expires?.timeIntervalSince1970, expires)
            let domainCookie = try XCTUnwrap(actual.first { $0.key.name == "domain" })
            XCTAssertFalse(domainCookie.key.hostOnly); XCTAssertEqual(domainCookie.sameSite, "Strict")
            let prefix = try XCTUnwrap(actual.first { $0.key.name == "__Host-key" })
            XCTAssertTrue(prefix.key.hostOnly); XCTAssertTrue(prefix.secure); XCTAssertTrue(prefix.httpOnly)
            XCTAssertEqual(prefix.sameSite, "None")
            let visible = try await page.webView.evaluateJavaScript("document.cookie") as? String
            XCTAssertFalse(visible?.contains("private=") == true)
            try self.button("novel.web.done", page).sendActions(for: .touchUpInside)
        }
        XCTAssertEqual(result["cookieChanges"], .array([])); XCTAssertEqual(result["headers"]["set-cookie"], .array([]))
        XCTAssertNil(page.webView)
        let isolated = self.page(mode: "login", request: ["url": .string(server.base + "/plain")], installation: "other-source")
        _ = try await run(isolated, on: presenter) {
            await self.wait { isolated.webView?.url?.path == "/plain" && isolated.webView?.isLoading == false }
            let cookies = try await self.store(isolated); XCTAssertTrue(cookies.isEmpty)
            try self.button("novel.web.done", isolated).sendActions(for: .touchUpInside)
        }
    }
    func testRealRedirectCookieChangesKeepBothOriginsDeletionAndHostPrefixScope() async throws {
        let server = try await server { path, base in
            if path == "/private/start" {
                return .init(status: 302, headers: [("Location", base.replacingOccurrences(of: "127.0.0.1", with: "localhost") + "/landing"),
                    ("Set-Cookie", "private=; Max-Age=0; Path=/private; HttpOnly; SameSite=Lax"),
                    ("Set-Cookie", "a=new; Path=/private; HttpOnly; SameSite=Strict")])
            }
            if path == "/landing" { return .init(headers: [("Set-Cookie", "b=landed; Path=/; SameSite=Lax")], html: "<html><title>landing</title></html>") }
            return .init(html: "<html>ready</html>")
        }
        let presenter = try presenter(), page = page(mode: "request", request: ["url": .string(server.base + "/private/start"),
            "cookies": .array([cookie("private", domain: "127.0.0.1", path: "/private", httpOnly: true, sameSite: "Lax")])])
        let result = try await run(page, on: presenter)
        XCTAssertEqual(URL(string: result["url"].string ?? "")?.host, "localhost")
        XCTAssertEqual(result["value"]["statusCode"], .number(200))
        let changes = try XCTUnwrap(result["cookieChanges"].array)
        let deleted = try XCTUnwrap(changes.first { $0["setCookie"].string?.hasPrefix("private=") == true })
        XCTAssertEqual(URL(string: deleted["url"].string!)?.host, "127.0.0.1")
        XCTAssertTrue(deleted["setCookie"].string!.contains("Max-Age=0")); XCTAssertTrue(deleted["setCookie"].string!.contains("Path=/private"))
        XCTAssertFalse(deleted["setCookie"].string!.contains("Domain="))
        for (name, host) in [("a", "127.0.0.1"), ("b", "localhost")] {
            let change = try XCTUnwrap(changes.first { $0["setCookie"].string?.hasPrefix(name + "=") == true })
            XCTAssertEqual(URL(string: change["url"].string!)?.host, host)
            XCTAssertFalse(change["setCookie"].string!.contains("Domain="))
        }
        let secure = self.page(mode: "login", request: ["url": .string(server.base + "/plain"),
            "cookies": .array([cookie("__Host-key", domain: "secure.books.invalid", secure: true, httpOnly: true, sameSite: "None")])])
        let changed = try await run(secure, on: presenter) {
            await self.wait { secure.webView?.url?.path == "/plain" && secure.webView?.isLoading == false }
            let replacement = try NovelWebCookie(seed: self.cookie("__Host-key", value: "two", domain: "secure.books.invalid", secure: true, httpOnly: true, sameSite: "None")).nativeCookie()
            let store = try XCTUnwrap(secure.webView).configuration.websiteDataStore.httpCookieStore
            await withCheckedContinuation { continuation in store.setCookie(replacement) { continuation.resume() } }
            try self.button("novel.web.done", secure).sendActions(for: .touchUpInside)
        }
        let prefix = try XCTUnwrap(changed["cookieChanges"].array?.first)
        XCTAssertEqual(prefix["url"], .string("https://secure.books.invalid/"))
        let text = try XCTUnwrap(prefix["setCookie"].string)
        XCTAssertTrue(text.hasPrefix("__Host-key=two; Path=/")); XCTAssertFalse(text.contains("Domain="))
        XCTAssertTrue(text.contains("Secure")); XCTAssertTrue(text.contains("HttpOnly")); XCTAssertTrue(text.contains("SameSite=None"))
        let rescope = self.page(mode: "login", request: ["url": .string(server.base + "/plain"),
            "cookies": .array([cookie("scope", domain: "books.invalid", hostOnly: false)])])
        let rescoped = try await run(rescope, on: presenter) {
            await self.wait { rescope.webView?.url?.path == "/plain" && rescope.webView?.isLoading == false }
            let store = try XCTUnwrap(rescope.webView).configuration.websiteDataStore.httpCookieStore
            let cookies = try await self.store(rescope)
            let existing = try XCTUnwrap(cookies.first { $0.name == "scope" })
            await withCheckedContinuation { continuation in store.delete(existing) { continuation.resume() } }
            let replacement = try NovelWebCookie(seed: self.cookie("scope", value: "host", domain: "books.invalid")).nativeCookie()
            await withCheckedContinuation { continuation in store.setCookie(replacement) { continuation.resume() } }
            try self.button("novel.web.done", rescope).sendActions(for: .touchUpInside)
        }
        let scopeChanges = try XCTUnwrap(rescoped["cookieChanges"].array)
        XCTAssertEqual(scopeChanges.count, 2)
        XCTAssertTrue(scopeChanges.first?["setCookie"].string?.contains("Max-Age=0") == true)
        XCTAssertTrue(scopeChanges.first?["setCookie"].string?.contains("Domain=books.invalid") == true)
        XCTAssertTrue(scopeChanges.last?["setCookie"].string?.hasPrefix("scope=host;") == true)
        XCTAssertFalse(scopeChanges.last?["setCookie"].string?.contains("Domain=") == true,
            "The new scope must be written after removal so the owner cannot delete the replacement")
    }
    func testRealWebKitKnownNoneDoesNotOverrideVisiblePoliciesOrLeakAcrossCookieScopes() async throws {
        let server = try await server { _, _ in .init(html: "<html>ready</html>") }
        let presenter = try presenter()
        let domain = "books.invalid"
        let seeds = ["known", "lax", "strict", "deleted"].map {
            cookie($0, domain: domain, secure: true, sameSite: "None")
        } + [
            cookie("hostscope", domain: domain, hostOnly: false, secure: true, sameSite: "None"),
            cookie("pathscope", domain: domain, path: "/private", secure: true, sameSite: "None"),
            cookie("originscope", domain: domain, secure: true, sameSite: "None"),
            cookie("unspecified", domain: domain, secure: true)
        ]
        let page = page(mode: "login", request: ["url": .string(server.base + "/plain"), "cookies": .array(seeds)])
        let result = try await run(page, on: presenter) {
            await self.wait { page.webView?.url?.path == "/plain" && page.webView?.isLoading == false }
            let store = try XCTUnwrap(page.webView).configuration.websiteDataStore.httpCookieStore
            for cookie in try await self.store(page) where ["deleted", "hostscope", "pathscope", "originscope"].contains(cookie.name) {
                await withCheckedContinuation { continuation in store.delete(cookie) { continuation.resume() } }
            }
            let replacements = [
                self.cookie("known", value: "two", domain: domain, secure: true, sameSite: "None"),
                self.cookie("lax", value: "two", domain: domain, secure: true, sameSite: "Lax"),
                self.cookie("strict", value: "two", domain: domain, secure: true, sameSite: "Strict"),
                self.cookie("hostscope", value: "two", domain: domain, secure: true),
                self.cookie("pathscope", value: "two", domain: domain, path: "/other", secure: true),
                self.cookie("originscope", value: "two", domain: "other.books.invalid", secure: true),
                self.cookie("unspecified", value: "two", domain: domain, secure: true),
                self.cookie("new", value: "two", domain: domain, secure: true)
            ]
            for seed in replacements {
                let cookie = try NovelWebCookie(seed: seed).nativeCookie()
                await withCheckedContinuation { continuation in store.setCookie(cookie) { continuation.resume() } }
            }
            try self.button("novel.web.done", page).sendActions(for: .touchUpInside)
        }
        let changes = try XCTUnwrap(result["cookieChanges"].array)
        XCTAssertEqual(changes.count, 12)
        let deletions = changes.filter { $0["setCookie"].string?.contains("Max-Age=0") == true }
        XCTAssertEqual(deletions.count, 4)
        XCTAssertTrue(deletions.contains { $0["setCookie"].string?.hasPrefix("deleted=;") == true })
        let updates = changes.filter { $0["setCookie"].string?.contains("Max-Age=0") != true }
        for (name, expected) in [("known", "None"), ("lax", "Lax"), ("strict", "Strict")] {
            let text = try XCTUnwrap(updates.first { $0["setCookie"].string?.hasPrefix(name + "=two;") == true }?["setCookie"].string)
            XCTAssertTrue(text.contains("SameSite=" + expected))
        }
        for name in ["hostscope", "pathscope", "originscope", "unspecified", "new"] {
            let change = try XCTUnwrap(updates.first { $0["setCookie"].string?.hasPrefix(name + "=two;") == true })
            let text = try XCTUnwrap(change["setCookie"].string)
            XCTAssertFalse(text.contains("SameSite="), "Unknown policy must not inherit another cookie's None")
            XCTAssertFalse(text.contains("Domain="))
            let origin = try XCTUnwrap(URL(string: try XCTUnwrap(change["url"].string)))
            XCTAssertEqual(origin.host, name == "originscope" ? "other.books.invalid" : domain)
            XCTAssertEqual(origin.path, name == "pathscope" ? "/other" : "/")
        }
    }
    func testWebModesKeepScalarTypesOriginalArgumentPositionsAndResourceOverrideNavigation() async throws {
        let server = try await server { path, base in
            if path == "/override" { return .init(html: "<html><script>location.href='\(base)/intercept';</script></html>") }
            return .init(html: "<html><title>合成章节</title></html>")
        }
        let presenter = try presenter()
        for (mode, script, expected) in [("request", "document.title", RuntimeValue.string("合成章节")),
            ("rule", "null", .null), ("webview", "false", .bool(false)), ("webViewGetSource", "42", .number(42)),
            ("webviewajax", "false", .bool(false)), ("webviewajax", "null", .null), ("webviewajax", "42", .number(42))] {
            let page = page(mode: mode, request: ["url": .string(server.base + "/plain"), "script": .string(script)])
            let result = try await run(page, on: presenter)
            XCTAssertEqual(mode == "request" ? result["value"]["data"] : result["value"], expected)
            XCTAssertNil(page.webView)
        }
        let browser = page(mode: "browser", request: ["url": .string(server.base + "/plain")])
        let response = try await run(browser, on: presenter) {
            await self.wait { browser.webView?.url?.path == "/plain" && browser.webView?.isLoading == false }
            try self.button("novel.web.done", browser).sendActions(for: .touchUpInside)
        }
        XCTAssertEqual(response["value"]["url"], .string(server.base + "/plain"))
        XCTAssertEqual(response["value"]["statusCode"], .number(200))
        XCTAssertTrue(response["value"]["data"].string?.contains("合成章节") == true, "Legado browser returns a StrResponse-compatible page, never true")
        let asset = server.base + "/asset"
        let html = "<html><script>fetch('\(asset)');</script></html>"
        let positional = page(mode: "webViewGetSource", request: ["arguments": .array([.string(html), .string(server.base + "/plain"), .null,
            .string(NSRegularExpression.escapedPattern(for: asset)), .number(1)])])
        let result = try await run(positional, on: presenter)
        XCTAssertEqual(result["value"], .string(asset), "A null script at index 2 cannot shift sourceRegex into the script slot")
        let override = page(mode: "webview", request: ["url": .string(server.base + "/override"),
            "overrideUrlRegex": .string(NSRegularExpression.escapedPattern(for: server.base + "/intercept"))])
        let intercepted = try await run(override, on: presenter)
        XCTAssertEqual(intercepted["value"], .string(server.base + "/intercept"))
        XCTAssertFalse(server.paths.contains("/intercept"), "The overridden navigation must be cancelled before the network request")
    }
    func testCancellationDuringURLRulesRPCWaitsForTheMatchAndNeverReturnsCookies() async throws {
        let entered = expectation(description: "Web URL rule reached the public RPC consumer")
        let cancelled = expectation(description: "Web URL rule request was cancelled")
        let matcher = NovelWebMatchRuntime(entered: { entered.fulfill() }, cancelled: { cancelled.fulfill() })
        let rules = NovelRuleProcessor(runtime: matcher)
        let server = try await server { _, _ in .init(html: "<html>must not navigate</html>") }
        let presenter = try presenter()
        let request = NovelWebRequest(id: UUID().uuidString, installationId: "fixture", mode: "webview",
            request: .object(["url": .string(server.base + "/late"), "overrideUrlRegex": .string(".*"),
                "cookies": .array([cookie("fixture", domain: "127.0.0.1", path: "/")])]))
        let page = NovelWebViewController(request: request, rules: rules, themeProvider: NovelTestPageTheme())
        var outcome: Result<RuntimeValue, Error>?
        let task = Task {
            do { outcome = .success(try await page.result(from: presenter)) }
            catch { outcome = .failure(error) }
        }; tasks.append(task)
        await fulfillment(of: [entered], timeout: 8)
        task.cancel()
        await fulfillment(of: [cancelled], timeout: 3)
        XCTAssertNil(outcome, "Page completion must wait for the accepted RPC to settle")
        await matcher.release()
        await wait { outcome != nil }; await task.value
        guard case .failure(let error) = try XCTUnwrap(outcome) else { return XCTFail("Cancellation cannot return a Cookie-bearing result") }
        XCTAssertTrue(error is CancellationError)
        XCTAssertNil(page.webView); XCTAssertFalse(server.paths.contains("/late"))
        await rules.close()
    }

    func testVerificationWithoutURLAndExplicitCollectionCancellationCannotStallOrCompleteTwice() async throws {
        let presenter = try presenter(), verification = page(mode: "verificationCode", request: [:])
        let code = try await run(verification, on: presenter) {
            await self.wait { verification.webView != nil && (try? self.button("novel.web.done", verification).isEnabled) == true }
            let field = try XCTUnwrap(self.views(verification.view).first { $0.accessibilityIdentifier == "novel.web.code" } as? UITextField)
            XCTAssertFalse(field.isHidden); field.text = "fixture-code"
            try self.button("novel.web.done", verification).sendActions(for: .touchUpInside)
        }
        XCTAssertEqual(code, .string("fixture-code"))
        let server = try await server { _, _ in .init(html: "<html><title>ready</title></html>") }
        let repeated = page(mode: "rule", request: ["url": .string(server.base + "/plain"),
            "script": .string("window.calls=(window.calls||0)+1;document.title='collecting';window.calls===1?new Promise(()=>{}):false")])
        let value = try await run(repeated, on: presenter) {
            await self.wait { repeated.webView?.url?.path == "/plain" && repeated.webView?.isLoading == false && repeated.webView?.title == "collecting" }
            let calls = try await repeated.webView.evaluateJavaScript("window.calls") as? Int
            XCTAssertEqual(calls, 1)
            try self.button("novel.web.done", repeated).sendActions(for: .touchUpInside)
            try self.button("novel.web.done", repeated).sendActions(for: .touchUpInside)
        }
        XCTAssertEqual(value["value"], .bool(false)); XCTAssertNil(repeated.webView)
        let blocked = page(mode: "rule", request: ["url": .string(server.base + "/plain"), "script": .string("new Promise(()=>{})")])
        var result: Result<RuntimeValue, Error>?
        let task = Task {
            do { result = .success(try await blocked.result(from: presenter)) } catch { result = .failure(error) }
        }; tasks.append(task)
        await wait { blocked.webView?.url?.path == "/plain" && blocked.webView?.isLoading == false }
        try button("novel.web.done", blocked).sendActions(for: .touchUpInside)
        task.cancel(); await wait { result != nil }; await task.value
        guard case .failure(let error) = try XCTUnwrap(result) else { return XCTFail("Cancellation cannot return cookies or a value") }
        XCTAssertTrue(error is CancellationError); XCTAssertNil(blocked.webView)
    }
}

private final class NovelWebHTTPFixture: @unchecked Sendable {
    struct Response {
        var status = 200
        var headers: [(String, String)] = []
        var html = ""
    }
    private let queue = DispatchQueue(label: "meowfilm.novel-web-fixture")
    private let listener: NWListener
    private let handler: @Sendable (String, String) -> Response
    private var connections: [ObjectIdentifier: NWConnection] = [:]
    private var recorded: [String] = []
    var base: String { "http://127.0.0.1:\(listener.port!.rawValue)" }
    var paths: [String] { queue.sync { recorded } }
    init(handler: @escaping @Sendable (String, String) -> Response) throws {
        self.handler = handler
        let parameters = NWParameters.tcp; parameters.requiredLocalEndpoint = .hostPort(host: "127.0.0.1", port: .any)
        listener = try NWListener(using: parameters)
    }
    func start() async throws {
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            listener.stateUpdateHandler = { [weak self] state in
                switch state {
                case .ready: self?.listener.stateUpdateHandler = nil; continuation.resume()
                case .failed(let error): self?.listener.stateUpdateHandler = nil; continuation.resume(throwing: error)
                default: break
                }
            }
            listener.newConnectionHandler = { [weak self] in self?.accept($0) }; listener.start(queue: queue)
        }
    }
    func stop() { queue.sync { listener.cancel(); for item in connections.values { item.cancel() }; connections.removeAll() } }
    private func accept(_ connection: NWConnection) {
        connections[ObjectIdentifier(connection)] = connection; connection.start(queue: queue); receive(connection, data: Data())
    }
    private func receive(_ connection: NWConnection, data: Data) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 16 * 1024) { [weak self] chunk, _, ended, error in
            guard let self else { return }; var bytes = data; if let chunk { bytes.append(chunk) }
            if let separator = bytes.range(of: Data("\r\n\r\n".utf8)), let head = String(data: bytes[..<separator.lowerBound], encoding: .utf8),
               let line = head.components(separatedBy: "\r\n").first, line.split(separator: " ").count >= 2 {
                let path = String(line.split(separator: " ")[1]); self.recorded.append(path)
                let response = self.handler(path, self.base), body = Data(response.html.utf8)
                var header = "HTTP/1.1 \(response.status) Fixture\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: \(body.count)\r\nConnection: close\r\n"
                for (key, value) in response.headers { header += "\(key): \(value)\r\n" }
                var output = Data((header + "\r\n").utf8); output.append(body)
                connection.send(content: output, contentContext: .finalMessage, isComplete: true, completion: .contentProcessed { [weak self] _ in self?.remove(connection) })
            } else if ended || error != nil { self.remove(connection) }
            else { self.receive(connection, data: bytes) }
        }
    }
    private func remove(_ connection: NWConnection) { connections[ObjectIdentifier(connection)] = nil; connection.cancel() }
}

private actor NovelWebMatchRuntime: NovelRuntimeServing {
    private let entered: @Sendable () -> Void
    private let cancelled: @Sendable () -> Void
    private var response: CheckedContinuation<RuntimeValue, Never>?
    init(entered: @escaping @Sendable () -> Void, cancelled: @escaping @Sendable () -> Void) {
        self.entered = entered; self.cancelled = cancelled
    }
    func release() { response?.resume(returning: .bool(true)); response = nil }
    func call(_ operation: String, _ parameters: RuntimeValue) async throws -> RuntimeValue {
        XCTAssertEqual(operation, "rules.process")
        XCTAssertEqual(parameters["request"]["operation"], .string("matchUrl"))
        let cancelled = cancelled
        return await withTaskCancellationHandler {
            await withCheckedContinuation { response = $0; entered() }
        } onCancel: { cancelled() }
    }
    func hasActiveCalls() -> Bool { false }
    func setActivityHandler(_ handler: (@Sendable (Bool) -> Void)?) {}
    func image(_ reference: String) async throws -> Data { throw NovelError.invalidResponse }
    func close() {}
}
#endif
