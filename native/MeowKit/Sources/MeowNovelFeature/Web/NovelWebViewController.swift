#if os(iOS)
import CoreFoundation
import UIKit
import WebKit
import MeowUI
import MeowRuntime

@MainActor
public final class NovelWebViewController: PageViewController, WKNavigationDelegate, WKUIDelegate, UIAdaptivePresentationControllerDelegate {
    private let input: NovelWebRequest
    public private(set) var webView: WKWebView!
    private var completion: ((Result<RuntimeValue, Error>) -> Void)?
    private let code = UITextField()
    private var evaluation: Task<Void, Never>?
    private var resourceTasks: [UUID: Task<Void, Never>] = [:]
    private var javaScript: [UUID: CheckedContinuation<RuntimeValue, Error>] = [:]
    private let rules: NovelRuleProcessor
    private var resourceURL: String?
    private var loading: Task<Void, Never>?
    private var finished = false
    private var submission: UUID?
    private var baseline: [NovelWebCookie] = []
    private var cancelDialog: (() -> Void)?
    private var url: URL?
    private var html = "", script = "", sourceRegex = "", overrideRegex = ""
    private var automatic = false, verification = false
    private var delay: Double = 0
    private var statusCode = 200, responseHeaders = RuntimeObject()
    private var evaluatedValue: RuntimeValue?
    public init(request: NovelWebRequest, rules: NovelRuleProcessor, themeProvider: any PageThemeProviding) {
        input = request; self.rules = rules; super.init(themeProvider: themeProvider)
    }
    public func result(from presenter: UIViewController) async throws -> RuntimeValue {
        try Task.checkCancellation()
        guard !finished else { throw CancellationError() }
        let host = try await resolvePresenter(from: presenter)
        try Task.checkCancellation()
        guard !finished, host.viewIfLoaded?.window != nil else { throw CancellationError() }
        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                completion = { continuation.resume(with: $0) }
                modalPresentationStyle = .pageSheet
                let accepted: Bool
                if let page = host as? PageViewController {
                    accepted = page.presentPageModal(self, animated: true)
                } else if host.presentedViewController == nil, !host.isBeingPresented, !host.isBeingDismissed,
                          host.viewIfLoaded?.window != nil {
                    host.present(self, animated: true)
                    accepted = presentingViewController != nil || host.presentedViewController === self
                } else {
                    accepted = false
                }
                presentationController?.delegate = self
                if !accepted {
                    finish(.failure(CancellationError()))
                }
            }
        } onCancel: {
            Task { @MainActor [weak self] in self?.finish(.failure(CancellationError())) }
        }
    }
    private func resolvePresenter(from presenter: UIViewController) async throws -> UIViewController {
        for _ in 0..<40 {
            try Task.checkCancellation()
            guard !finished else { throw CancellationError() }
            var current = presenter
            while let child = current.presentedViewController {
                if child.isBeingDismissed { break }
                current = child
            }
            let transitioning = current.isBeingPresented || current.isBeingDismissed ||
                (current.presentedViewController?.isBeingDismissed == true)
            let blockedByAlert = current is UIAlertController
            if !transitioning, !blockedByAlert, current.presentedViewController == nil,
               current.viewIfLoaded?.window != nil {
                return current
            }
            try await Task.sleep(nanoseconds: 50_000_000)
        }
        var host = presenter
        while let child = host.presentedViewController, !child.isBeingDismissed {
            host = child
        }
        guard !(host is UIAlertController), host.presentedViewController == nil,
              host.viewIfLoaded?.window != nil else {
            throw CancellationError()
        }
        return host
    }
    public override func viewDidLoad() {
        pageTitle = input.request["title"].string ?? "书源网页"
        super.viewDidLoad(); view.accessibilityIdentifier = "novel.web"
        let configuration = WKWebViewConfiguration()
        // A fresh nonpersistent store cannot share cookies, localStorage or
        // credentials with another installation, Safari, or public artwork.
        configuration.websiteDataStore = .nonPersistent()
        configuration.userContentController.add(NovelWebResourceObserver(owner: self), name: "novelResource")
        configuration.userContentController.addUserScript(WKUserScript(source: """
            (() => {
              const report = url => { try { window.webkit.messageHandlers.novelResource.postMessage(String(url)); } catch (_) {} };
              new PerformanceObserver(list => { for (const entry of list.getEntries()) report(entry.name); }).observe({entryTypes:['resource']});
              addEventListener('load', () => { for (const entry of performance.getEntriesByType('resource')) report(entry.name); });
            })();
            """, injectionTime: .atDocumentStart, forMainFrameOnly: false))
        webView = WKWebView(frame: .zero, configuration: configuration)
        webView.navigationDelegate = self; webView.uiDelegate = self
        view.addSubview(webView); webView.translatesAutoresizingMaskIntoConstraints = false
        code.placeholder = "验证码"; code.borderStyle = .roundedRect; code.autocorrectionType = .no
        code.accessibilityIdentifier = "novel.web.code"
        view.addSubview(code); code.translatesAutoresizingMaskIntoConstraints = false
        do { try interpret() } catch { finish(.failure(error)); return }
        code.isHidden = !verification
        NSLayoutConstraint.activate([
            code.topAnchor.constraint(equalTo: contentTopAnchor, constant: 8),
            code.leadingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.leadingAnchor, constant: 16),
            code.trailingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.trailingAnchor, constant: -16),
            code.heightAnchor.constraint(equalToConstant: verification ? 44 : 0),
            webView.topAnchor.constraint(equalTo: code.bottomAnchor, constant: 8),
            webView.bottomAnchor.constraint(equalTo: view.safeAreaLayoutGuide.bottomAnchor),
            webView.leadingAnchor.constraint(equalTo: view.leadingAnchor), webView.trailingAnchor.constraint(equalTo: view.trailingAnchor)])
        let cancel = PageHeaderTextButton(title: "取消") { [weak self] in self?.finish(.failure(CancellationError())) }
        cancel.accessibilityIdentifier = "novel.web.cancel"; pageHeader.setLeading(cancel)
        let done = PageHeaderTextButton(title: "完成") { [weak self] in
            guard let self else { return }
            startCollection(explicit: true)
        }
        done.accessibilityIdentifier = "novel.web.done"; done.isEnabled = false; pageHeader.setTrailing([done])
        loading = Task { [weak self] in
            guard let self else { return }
            do {
                try await injectCookies(); try Task.checkCancellation()
                done.isEnabled = true
                guard !finished, let url else { return }
                if !html.isEmpty { webView.loadHTMLString(html, baseURL: url) }
                else {
                    var request = URLRequest(url: url)
                    request.httpMethod = input.request["method"].string ?? "GET"
                    if case .object(let fields) = input.request["headers"] {
                        for (name, value) in fields where name.lowercased() != "cookie" {
                            if let value = value.string { request.setValue(value, forHTTPHeaderField: name) }
                        }
                    }
                    let body = input.request["body"]
                    if let text = body.string { request.httpBody = Data(text.utf8) }
                    else if body != .null { request.httpBody = try RuntimeJSON.encode(body) }
                    webView.load(request)
                }
            } catch { if !Task.isCancelled { finish(.failure(error)) } }
        }
    }
    private func interpret() throws {
        let mode = input.mode.lowercased(), request = input.request, args = request["arguments"].array ?? []
        let supported = ["request", "login", "browser", "openurl", "verificationcode", "webview", "rule",
            "webviewajax", "webviewgetsource", "webviewgetoverrideurl", "startbrowser",
            "startbrowserwithshouldoverrideurlloading", "startbrowserdp", "startbrowserdpwithhtml",
            "startbrowserdpwithshouldoverrideurlloading", "getverificationcode"]
        guard supported.contains(mode) else { throw NovelError.invalidResponse }
        func argument(_ index: Int) -> RuntimeValue { args.indices.contains(index) ? args[index] : .null }
        verification = mode == "verificationcode" || mode == "getverificationcode"
        let htmlFirst = mode.hasPrefix("webview") && argument(0).string.map { Self.httpURL($0) == nil } == true
        let explicitURL = request["url"].string.flatMap { $0.isEmpty ? nil : $0 }
        let urlString = explicitURL ?? argument(htmlFirst ? 1 : 0).string ?? ""
        if urlString.isEmpty, verification { url = nil } else {
            guard let parsed = Self.httpURL(urlString) else { throw NovelError.invalidResponse }; url = parsed
        }
        let explicitHTML = request["html"].string.flatMap { $0.isEmpty ? nil : $0 }
        html = explicitHTML ?? (htmlFirst ? argument(0).string : nil) ?? ""
        let explicitScript = request["script"].string.flatMap { $0.isEmpty ? nil : $0 }
        script = explicitScript ?? (htmlFirst ? argument(2).string : nil) ?? ""
        sourceRegex = request["sourceRegex"].string ?? ""
        if htmlFirst, mode == "webviewgetsource", sourceRegex.isEmpty { sourceRegex = argument(3).string ?? "" }
        overrideRegex = request["overrideUrlRegex"].string ?? ""
        if htmlFirst, mode == "webviewgetoverrideurl", overrideRegex.isEmpty { overrideRegex = argument(3).string ?? "" }
        let positionalDelay = htmlFirst ? argument(mode == "webview" ? 3 : 4).number : nil
        delay = max(0, request["delayTime"].number ?? positionalDelay ?? 0) / 1000
        automatic = ["request", "rule", "webview", "webviewajax", "webviewgetsource", "webviewgetoverrideurl"].contains(mode)
    }
    private static func httpURL(_ string: String) -> URL? {
        guard let value = URL(string: string), ["http", "https"].contains(value.scheme?.lowercased() ?? ""),
              value.host != nil else { return nil }; return value
    }
    private func injectCookies() async throws {
        let store = webView.configuration.websiteDataStore.httpCookieStore
        let seeds = try (input.request["cookies"].array ?? []).map { try NovelWebCookie(seed: $0) }
        for seed in seeds {
            let cookie = try seed.nativeCookie()
            await withCheckedContinuation { continuation in store.setCookie(cookie) { continuation.resume() } }
            try Task.checkCancellation()
        }
        let cookies: [HTTPCookie] = await withCheckedContinuation { continuation in store.getAllCookies { continuation.resume(returning: $0) } }
        try Task.checkCancellation()
        baseline = NovelWebCookie.snapshot(cookies, preserving: seeds)
    }
    public func webView(_ webView: WKWebView, decidePolicyFor navigationAction: WKNavigationAction,
                        decisionHandler: @escaping (WKNavigationActionPolicy) -> Void) {
        if !finished, !html.isEmpty, navigationAction.request.url?.absoluteString == "about:blank" {
            decisionHandler(.allow); return
        }
        guard !finished, let target = navigationAction.request.url, Self.httpURL(target.absoluteString) != nil else {
            decisionHandler(.cancel); return
        }
        guard !navigationAction.shouldPerformDownload else { decisionHandler(.cancel); return }
        observedResource(target.absoluteString)
        guard !overrideRegex.isEmpty else { decisionHandler(.allow); return }
        let id = UUID()
        resourceTasks[id] = Task { [weak self] in
            guard let self else { decisionHandler(.cancel); return }; defer { resourceTasks[id] = nil }
            let matched: Bool
            do {
                matched = try await matches(target.absoluteString, pattern: overrideRegex)
                try Task.checkCancellation()
                guard !finished else { decisionHandler(.cancel); return }
            } catch {
                decisionHandler(.cancel)
                if !Task.isCancelled { finish(.failure(error)) }; return
            }
            decisionHandler(matched ? .cancel : .allow)
            if matched {
                do { _ = try await returnValue(.string(target.absoluteString), at: target) }
                catch { if !Task.isCancelled { finish(.failure(error)) } }
            }
        }
    }
    public func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) {
        guard automatic, !finished else { return }
        evaluatedValue = nil
        startCollection(explicit: false)
    }
    private func startCollection(explicit: Bool) {
        let previous = evaluation; previous?.cancel()
        evaluation = Task { [weak self] in
            // A cancelled automatic collection releases its submission claim
            // before a user's Done starts. No competing collector can strand it.
            await previous?.value
            guard let self else { return }
            do {
                try Task.checkCancellation()
                if !explicit, delay > 0 { try await Task.sleep(for: .seconds(delay)) }
                while !finished {
                    if try await collect(explicit: explicit) { return }
                    if explicit { return }
                    try await Task.sleep(nanoseconds: 300_000_000)
                }
            } catch { if !Task.isCancelled { finish(.failure(error)) } }
        }
    }
    @discardableResult
    private func collect(explicit: Bool) async throws -> Bool {
        try Task.checkCancellation()
        guard !finished else { return false }
        if verification {
            guard explicit, let text = code.text, !text.isEmpty else { return false }
            if let target = webView.url.flatMap({ Self.httpURL($0.absoluteString) }) ?? url {
                return try await returnValue(.string(text), at: target)
            } else { finish(.success(.string(text))) }
            return true
        }
        guard let target = webView.url.flatMap({ Self.httpURL($0.absoluteString) }) ?? url else { throw NovelError.invalidResponse }
        let mode = input.mode.lowercased()
        if mode == "login" || mode == "openurl" { return try await returnValue(.bool(true), at: target) }
        let value: RuntimeValue
        if let evaluatedValue { value = evaluatedValue }
        else {
            value = try await evaluate(script.isEmpty ? "document.documentElement.outerHTML" : script)
            try Task.checkCancellation()
            evaluatedValue = value
        }
        try Task.checkCancellation()
        if !overrideRegex.isEmpty, !explicit { return false }
        if !sourceRegex.isEmpty {
            if let resourceURL { return try await returnValue(.string(resourceURL), at: target) }
            if !explicit { return false }
        }
        let result: RuntimeValue
        if ["request", "browser"].contains(mode) {
            result = .object(["url": .string(target.absoluteString), "statusCode": .number(Double(statusCode)),
                "headers": .object(responseHeaders), "data": value])
        } else { result = value }
        return try await returnValue(result, at: target)
    }
    nonisolated private static func runtimeValue(from raw: Any?) -> RuntimeValue {
        guard let raw, !(raw is NSNull) else { return .null }
        if let text = raw as? String { return .string(text) }
        if let number = raw as? NSNumber {
            if CFGetTypeID(number) == CFBooleanGetTypeID() { return .bool(number.boolValue) }
            let double = number.doubleValue
            return double.isFinite ? .number(double) : .null
        }
        if let array = raw as? [Any] { return .array(array.map(Self.runtimeValue(from:))) }
        if let dict = raw as? [AnyHashable: Any] {
            var object = RuntimeObject()
            for (key, entry) in dict {
                object[String(describing: key)] = Self.runtimeValue(from: entry)
            }
            return .object(object)
        }
        if let date = raw as? Date {
            return .string(ISO8601DateFormatter().string(from: date))
        }
        return .null
    }
    private func evaluate(_ script: String) async throws -> RuntimeValue {
        let id = UUID()
        return try await withTaskCancellationHandler {
            try Task.checkCancellation()
            return try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<RuntimeValue, Error>) in
                guard !finished, let webView else { continuation.resume(throwing: CancellationError()); return }
                javaScript[id] = continuation
                webView.callAsyncJavaScript("return await (0,eval)(script);", arguments: ["script": script],
                    in: nil, in: .page) { [weak self] result in
                    let converted: Result<RuntimeValue, Error> = result.map { Self.runtimeValue(from: $0) }
                    Task { @MainActor [weak self] in
                        guard let continuation = self?.javaScript.removeValue(forKey: id) else { return }
                        continuation.resume(with: converted)
                    }
                }
            }
        } onCancel: {
            Task { @MainActor [weak self] in self?.javaScript.removeValue(forKey: id)?.resume(throwing: CancellationError()) }
        }
    }
    private func matches(_ text: String, pattern: String) async throws -> Bool {
        try await rules.matchesURL(text, pattern: pattern)
    }
    fileprivate func observedResource(_ value: String) {
        guard !finished, !sourceRegex.isEmpty, resourceURL == nil, Self.httpURL(value) != nil else { return }
        let id = UUID()
        resourceTasks[id] = Task { [weak self] in
            guard let self else { return }; defer { resourceTasks[id] = nil }
            do {
                if try await matches(value, pattern: sourceRegex) {
                    try Task.checkCancellation(); guard !finished else { return }
                    resourceURL = value
                }
            } catch { if !Task.isCancelled { finish(.failure(error)) } }
        }
    }
    private func returnValue(_ value: RuntimeValue, at url: URL) async throws -> Bool {
        guard !finished, submission == nil else { return false }
        let token = UUID(); submission = token
        defer { if submission == token { submission = nil } }
        guard let webView else { throw CancellationError() }
        let cookies: [HTTPCookie] = await withCheckedContinuation { continuation in
            webView.configuration.websiteDataStore.httpCookieStore.getAllCookies { continuation.resume(returning: $0) }
        }
        try Task.checkCancellation(); guard !finished else { return false }
        let changes = NovelWebCookie.changes(baseline: baseline, final: NovelWebCookie.snapshot(cookies, preserving: baseline))
        finish(.success(.object(["value": value, "url": .string(url.absoluteString),
            "headers": .object(["set-cookie": .array([])]), "cookieChanges": .array(changes)])))
        return true
    }
    public func webView(_ webView: WKWebView, didFail navigation: WKNavigation!, withError error: Error) {
        if (error as NSError).code != NSURLErrorCancelled { finish(.failure(error)) }
    }
    public func webView(_ webView: WKWebView, didFailProvisionalNavigation navigation: WKNavigation!, withError error: Error) {
        if (error as NSError).code != NSURLErrorCancelled { finish(.failure(error)) }
    }
    public func webView(_ webView: WKWebView, createWebViewWith configuration: WKWebViewConfiguration,
                        for navigationAction: WKNavigationAction, windowFeatures: WKWindowFeatures) -> WKWebView? {
        if let target = navigationAction.request.url, Self.httpURL(target.absoluteString) != nil { webView.load(navigationAction.request) }
        return nil
    }
    public func webView(_ webView: WKWebView, decidePolicyFor navigationResponse: WKNavigationResponse,
                        decisionHandler: @escaping (WKNavigationResponsePolicy) -> Void) {
        if navigationResponse.isForMainFrame, let response = navigationResponse.response as? HTTPURLResponse {
            statusCode = response.statusCode
            responseHeaders = RuntimeObject(uniqueKeysWithValues: response.allHeaderFields.map { (String(describing: $0.key), .string(String(describing: $0.value))) })
        }
        decisionHandler(!finished && navigationResponse.canShowMIMEType ? .allow : .cancel)
    }
    public func webView(_ webView: WKWebView, requestMediaCapturePermissionFor origin: WKSecurityOrigin,
                        initiatedByFrame frame: WKFrameInfo, type: WKMediaCaptureType,
                        decisionHandler: @escaping (WKPermissionDecision) -> Void) { decisionHandler(.deny) }
    public func webView(_ webView: WKWebView, runJavaScriptAlertPanelWithMessage message: String,
                        initiatedByFrame frame: WKFrameInfo, completionHandler: @escaping () -> Void) {
        cancelDialog = completionHandler
        let presented = PageAlertPresenter.present(
            on: self, title: pageTitle, message: message,
            actions: [PageAlertAction(title: "确定", style: .default) { [weak self] in
                let done = self?.cancelDialog; self?.cancelDialog = nil; done?()
            }]
        )
        if presented == nil {
            let done = cancelDialog; cancelDialog = nil; done?()
        }
    }
    public func webView(_ webView: WKWebView, runJavaScriptConfirmPanelWithMessage message: String,
                        initiatedByFrame frame: WKFrameInfo, completionHandler: @escaping (Bool) -> Void) {
        cancelDialog = { completionHandler(false) }
        let presented = PageAlertPresenter.present(
            on: self, title: pageTitle, message: message,
            actions: [
                PageAlertAction(title: "取消", style: .cancel) { [weak self] in
                    let done = self?.cancelDialog; self?.cancelDialog = nil; done?()
                },
                PageAlertAction(title: "确定", style: .default) { [weak self] in
                    guard let self, self.cancelDialog != nil else { return }
                    self.cancelDialog = nil; completionHandler(true)
                },
            ]
        )
        if presented == nil {
            let done = cancelDialog; cancelDialog = nil; done?()
        }
    }
    public func webView(_ webView: WKWebView, runJavaScriptTextInputPanelWithPrompt prompt: String, defaultText: String?,
                        initiatedByFrame frame: WKFrameInfo, completionHandler: @escaping (String?) -> Void) {
        cancelDialog = { completionHandler(nil) }
        let presented = PageAlertPresenter.present(
            on: self, title: pageTitle, message: prompt,
            textFields: [PageAlertTextField(text: defaultText)],
            actions: [PageAlertAction(title: "取消", style: .cancel) { [weak self] in
                let done = self?.cancelDialog; self?.cancelDialog = nil; done?()
            }],
            inputSubmit: { [weak self] values in
                guard let self, self.cancelDialog != nil else { return }
                self.cancelDialog = nil; completionHandler(values.first)
            },
            inputSubmitTitle: "确定"
        )
        if presented == nil {
            let done = cancelDialog; cancelDialog = nil; done?()
        }
    }
    public func presentationControllerDidDismiss(_ presentationController: UIPresentationController) { finish(.failure(CancellationError())) }
    private func finish(_ result: Result<RuntimeValue, Error>) {
        guard !finished else { return }; finished = true
        cancelDialog?(); cancelDialog = nil
        let scripts = Array(javaScript.values); javaScript.removeAll()
        for continuation in scripts { continuation.resume(throwing: CancellationError()) }
        evaluation?.cancel(); loading?.cancel()
        for task in resourceTasks.values { task.cancel() }
        webView?.stopLoading(); webView?.navigationDelegate = nil; webView?.uiDelegate = nil
        webView?.configuration.userContentController.removeScriptMessageHandler(forName: "novelResource")
        webView?.removeFromSuperview(); webView = nil
        let callback = completion; completion = nil
        let tasks = Array(resourceTasks.values) + [evaluation, loading].compactMap { $0 }
        Task { [weak self] in
            for task in tasks { await task.value }
            guard let self else { callback?(result); return }
            self.dismissSelf(animated: true) { callback?(result) }
        }
    }
    public override func pageDidBecomeHidden() {
        if UIApplication.shared.applicationState != .active { finish(.failure(CancellationError())) }
    }
    public override func pageDidBecomeInactive() { finish(.failure(CancellationError())) }
}
@MainActor
private final class NovelWebResourceObserver: NSObject, WKScriptMessageHandler {
    weak var owner: NovelWebViewController?
    init(owner: NovelWebViewController) { self.owner = owner }
    func userContentController(_ userContentController: WKUserContentController, didReceive message: WKScriptMessage) {
        if let value = message.body as? String { owner?.observedResource(value) }
    }
}
#endif
