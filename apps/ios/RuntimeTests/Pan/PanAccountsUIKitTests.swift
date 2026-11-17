#if os(iOS)
import XCTest
import UIKit
import SwiftUI
import CoreImage
import Combine
import MeowRuntime
import MeowStorage
import MeowUI
@testable import MeowFilmFeature

@MainActor
final class PanAccountsUIKitTests: XCTestCase {
    private weak var previousKeyWindow: UIWindow?

    func testPageEntryHasNoRPCValidationOrReadIndicators() async throws {
        let runtime = try PanUIKitRuntime()
        try await runtime.set(.baidu, status: .signedIn)
        let model = PanAccountsModel(service: runtime.service)
        model.start()
        let page = PanAccountsViewController(model: model, themeProvider: PanAccountsTheme())
        page.loadViewIfNeeded()
        defer { PageViewController.invalidatePages(in: page); model.close() }
        XCTAssertNil(page.table.backgroundView)
        XCTAssertEqual(model.rows.map(\.id), [.baidu, .quark, .uc, .tianyi, .yidong])
        XCTAssertEqual(model.rows[0].account?.status, .unverified)
        for index in 1...5 {
            let cell = page.tableView(page.table, cellForRowAt: IndexPath(row: index, section: 0))
            XCTAssertFalse(descendants(cell).contains { $0 is UIActivityIndicatorView })
        }
        model.close(); model.start()
        let calls = await runtime.operations
        XCTAssertTrue(calls.isEmpty)
    }

    func testTableHasExactlyThreeColumnsAtPhoneWidthsWithThreeStateActionsAndTheme() async throws {
        let runtime = try PanUIKitRuntime()
        try await runtime.set(.baidu, status: .signedIn)
        try await runtime.set(.quark, status: .expired)
        let model = PanAccountsModel(service: runtime.service)
        model.start()
        await model.refresh(.baidu)?.value
        await model.refresh(.quark)?.value
        let theme = PanAccountsTheme()
        let page = PanAccountsViewController(model: model, themeProvider: theme)
        page.loadViewIfNeeded()
        defer { PageViewController.invalidatePages(in: page); model.close() }
        XCTAssertTrue(page.table is SettingsTableView)
        XCTAssertEqual(page.table.separatorStyle, .none)
        XCTAssertEqual(page.tableView(page.table, numberOfRowsInSection: 0), 6)
        let header = page.tableView(page.table, cellForRowAt: IndexPath(row: 0, section: 0))
        XCTAssertEqual(descendants(header).compactMap { ($0 as? UILabel)?.text }, ["平台", "状态", "操作"])
        XCTAssertTrue(descendants(header).compactMap { $0 as? UILabel }.allSatisfy(\.adjustsFontForContentSizeCategory))
        XCTAssertTrue((header.backgroundView as? SettingsRowBackgroundView)?.drawsSeparator == true)
        for (index, status, titles) in [(1, "已登录", ["刷新", "退出登录"]), (2, "失效", ["重新登录", "退出登录"]), (3, "未登录", ["登录"])] {
            let cell = page.tableView(page.table, cellForRowAt: IndexPath(row: index, section: 0))
            let columns = try XCTUnwrap(descendants(cell).compactMap { $0 as? PanAccountColumns }.first)
            XCTAssertEqual(columns.arrangedSubviews.count, 3)
            XCTAssertEqual(descendants(columns.columns[1]).compactMap { ($0 as? UILabel)?.text }, [status])
            XCTAssertTrue(descendants(columns.columns[1]).compactMap { $0 as? UILabel }.allSatisfy(\.adjustsFontForContentSizeCategory))
            XCTAssertEqual(descendants(columns.columns[2]).compactMap { ($0 as? UIButton)?.configuration?.title }, titles)
            for width in [CGFloat(320), 390, 430] {
                cell.frame = CGRect(x: 0, y: 0, width: width - 32, height: 120)
                cell.setNeedsLayout(); cell.layoutIfNeeded(); cell.contentView.layoutIfNeeded()
                XCTAssertFalse(columns.hasAmbiguousLayout)
                XCTAssertEqual(columns.columns[0].frame.minX, 0, accuracy: 0.5)
                XCTAssertGreaterThan(columns.columns[1].frame.minX, columns.columns[0].frame.maxX)
                XCTAssertGreaterThan(columns.columns[2].frame.minX, columns.columns[1].frame.maxX)
                XCTAssertEqual(columns.columns[2].frame.maxX, columns.bounds.width, accuracy: 0.5)
                XCTAssertGreaterThan(columns.columns[2].bounds.width, 60)
                for button in descendants(columns.columns[2]).compactMap({ $0 as? UIButton }) {
                    XCTAssertGreaterThanOrEqual(button.bounds.height, 44)
                    XCTAssertEqual(button.titleLabel?.font, .preferredFont(forTextStyle: .body))
                }
            }
        }
        page.overrideUserInterfaceStyle = .dark
        theme.setColorway(.sky); page.applyTheme()
        let darkCell = page.tableView(page.table, cellForRowAt: IndexPath(row: 1, section: 0))
        let darkBackground = try XCTUnwrap(darkCell.backgroundView as? SettingsRowBackgroundView)
        let fill = try XCTUnwrap(darkBackground.layer.sublayers?.compactMap { $0 as? CAShapeLayer }.first?.fillColor)
        XCTAssertTrue(UIColor(cgColor: fill).isEqual(UIColor(page.theme.settings.surface)))
        let status = try XCTUnwrap(descendants(darkCell).first { $0.accessibilityIdentifier == "pan.accounts.status" } as? UILabel)
        XCTAssertEqual(status.textColor, UIColor(page.theme.settings.muted))
    }

    func testTableSemanticColorsAndBusyActionsFollowStyleWithoutFollowingPalette() async throws {
        let runtime = try PanUIKitRuntime()
        try await runtime.set(.baidu, status: .signedIn)
        try await runtime.set(.quark, status: .expired)
        let model = PanAccountsModel(service: runtime.service)
        model.start()
        await model.refresh(.baidu)?.value
        await model.refresh(.quark)?.value
        let theme = PanAccountsTheme()
        let page = PanAccountsViewController(model: model, themeProvider: theme)
        page.loadViewIfNeeded()
        defer { PageViewController.invalidatePages(in: page); model.close() }
        for style in [UIUserInterfaceStyle.light, .dark] {
            page.overrideUserInterfaceStyle = style
            let settings = SettingsTheme(dark: style == .dark)
            for colorway in [PageColorway.lilac, .sky] {
                theme.setColorway(colorway); page.applyTheme()
                let header = page.tableView(page.table, cellForRowAt: IndexPath(row: 0, section: 0))
                let headings = descendants(header).compactMap { $0 as? UILabel }
                XCTAssertEqual(headings.count, 3)
                for heading in headings { XCTAssertEqual(heading.textColor, UIColor(settings.muted)) }
                for index in 1...3 {
                    let cell = page.tableView(page.table, cellForRowAt: IndexPath(row: index, section: 0))
                    let status = try XCTUnwrap(descendants(cell).first {
                        $0.accessibilityIdentifier == "pan.accounts.status"
                    } as? UILabel)
                    XCTAssertEqual(status.textColor, UIColor(index == 2 ? settings.danger : settings.muted))
                    let actions = descendants(cell).compactMap { $0 as? UIButton }
                    XCTAssertEqual(actions.count, index == 3 ? 1 : 2)
                    for action in actions {
                        XCTAssertTrue(action.isEnabled)
                        XCTAssertEqual(action.configuration?.baseForegroundColor, UIColor(settings.accent))
                        XCTAssertEqual(try titleColor(action), UIColor(settings.accent))
                    }
                }
                XCTAssertNotNil(model.openLogin(.baidu))
                let busyCell = page.tableView(page.table, cellForRowAt: IndexPath(row: 1, section: 0))
                let busyActions = descendants(busyCell).compactMap { $0 as? UIButton }
                XCTAssertEqual(busyActions.count, 2)
                for action in busyActions {
                    XCTAssertFalse(action.isEnabled)
                    XCTAssertEqual(action.configuration?.baseForegroundColor, UIColor(settings.muted))
                    XCTAssertEqual(try titleColor(action), UIColor(settings.muted))
                }
                model.closeLogin()
                let restoredCell = page.tableView(page.table, cellForRowAt: IndexPath(row: 1, section: 0))
                let restoredActions = descendants(restoredCell).compactMap { $0 as? UIButton }
                XCTAssertEqual(restoredActions.count, 2)
                for action in restoredActions {
                    XCTAssertTrue(action.isEnabled)
                    XCTAssertEqual(try titleColor(action), UIColor(settings.accent))
                }
            }
        }
    }

    func testTableRefreshCallsRuntimeButLogoutIsNativeAndLoginOpensSharedFloatingCard() async throws {
        let runtime = try PanUIKitRuntime()
        try await runtime.set(.baidu, status: .signedIn)
        let model = PanAccountsModel(service: runtime.service)
        model.start()
        await model.refresh(.baidu)?.value
        let page = PanAccountsViewController(model: model, themeProvider: PanAccountsTheme())
        let window = mount(page)
        defer { unmount(page, window: window); model.close() }
        await eventually { page.canPresentPageAlert && !model.rows.contains(where: \.busy) }
        let before = await runtime.count("refresh")
        let signedIn = page.tableView(page.table, cellForRowAt: IndexPath(row: 1, section: 0))
        let refresh = try button("pan.accounts.baidu.refresh", in: signedIn)
        refresh.sendActions(for: .touchUpInside)
        await eventually { await runtime.count("refresh") == before + 1 && !model.rows[0].busy }
        let fresh = page.tableView(page.table, cellForRowAt: IndexPath(row: 1, section: 0))
        try button("pan.accounts.baidu.logout", in: fresh).sendActions(for: .touchUpInside)
        await eventually { model.rows[0].account?.status == .signedOut }
        let signedOut = page.tableView(page.table, cellForRowAt: IndexPath(row: 1, section: 0))
        try button("pan.accounts.baidu.login", in: signedOut).sendActions(for: .touchUpInside)
        await eventually { page.presentedViewController is PanLoginViewController }
        let card = try XCTUnwrap(page.presentedViewController as? PanLoginViewController)
        await waitForPresentedComponent(card)
        XCTAssertTrue(card.view is AccountFloatingBackdrop)
        XCTAssertEqual(card.modalPresentationStyle, .overFullScreen)
        XCTAssertEqual(card.model.method, .qr)
        XCTAssertFalse(card.hasOwnPageHeader)
        await eventually { card.model.challenge != nil }
        NotificationCenter.default.post(name: UIApplication.willResignActiveNotification, object: nil)
        await eventually { await runtime.count("login.cancel") == 1 }
        XCTAssertNil(card.model.challenge)
        let count = await runtime.count("settings.clear")
        XCTAssertEqual(count, 0, "Even an explicit clear never calls the runtime")
        XCTAssertEqual(try runtime.storage.read().panField("accounts")?.panField("baidu")?.panField("credentials"), .object([:]))
    }

    func testManualFloatingFormBindsFieldsHidesSecretsAndNeverLoadsOldValues() async throws {
        let runtime = try PanUIKitRuntime()
        let model = PanAccountsModel(service: runtime.service)
        model.start()
        let login = try XCTUnwrap(model.openLogin(.tianyi))
        let card = PanLoginViewController(model: login, themeProvider: PanAccountsTheme(), close: { model.closeLogin() })
        let window = mount(card)
        defer { unmount(card, window: window); model.close() }
        await eventually { self.descendants(card.view).compactMap { $0 as? UITextField }.count == 3 }
        let fields = descendants(card.view).compactMap { $0 as? UITextField }
            .sorted { $0.convert(.zero, to: card.view).y < $1.convert(.zero, to: card.view).y }
        XCTAssertEqual(fields.count, 3)
        XCTAssertEqual(fields.map(\.isSecureTextEntry), [true, false, true])
        XCTAssertTrue(fields.allSatisfy { ($0.text ?? "").isEmpty })
        fields[2].text = "  synthetic password  "; fields[2].sendActions(for: .editingChanged)
        await eventually { login.draft["password"] == "  synthetic password  " }
        XCTAssertEqual(login.draft["cookie"], nil)
        let calls = await runtime.operations
        XCTAssertTrue(calls.isEmpty, "Page entry and manual login must never request runtime settings or old secrets")
        NotificationCenter.default.post(name: UIApplication.willResignActiveNotification, object: nil)
        XCTAssertFalse(login.confirmed)
    }

    func testManualNativeSaveClosesCardWithoutClaimingOnlineLogin() async throws {
        let runtime = try PanUIKitRuntime()
        let owner = PanAccountsModel(service: runtime.service)
        owner.start()
        let login = try XCTUnwrap(owner.openLogin(.tianyi))
        var closed = false
        let card = PanLoginViewController(model: login, themeProvider: PanAccountsTheme(), close: { closed = true })
        let window = mount(card)
        defer { unmount(card, window: window); owner.close() }
        await eventually { card.isPageVisible }
        login.draft = ["cookie": "synthetic-offline-cookie"]
        login.save()
        await eventually { closed }
        XCTAssertTrue(login.saved); XCTAssertFalse(login.confirmed)
        XCTAssertEqual(owner.rows[3].account?.status, .unverified)
        XCTAssertTrue(login.draft.isEmpty)
        let calls = await runtime.operations
        XCTAssertTrue(calls.isEmpty)
        owner.closeLogin(); owner.close(); owner.start()
        XCTAssertEqual(owner.rows[3].account?.status, .unverified)
        XCTAssertEqual(owner.rows[3].account?.revision, 1)
    }

    func testQRMethodPickerBindsManualCookieAndCancelsQR() async throws {
        let runtime = try PanUIKitRuntime()
        let provider = try PanProvider(panUIProvider(.quark))
        let model = PanLoginModel(provider: provider, service: runtime.service, update: { _ in })
        let card = PanLoginViewController(model: model, themeProvider: PanAccountsTheme(), close: { model.close() })
        let window = mount(card)
        defer { unmount(card, window: window); model.close() }
        await eventually { model.challenge != nil }
        XCTAssertEqual(UIApplication.shared.applicationState, .active)
        let picker = try XCTUnwrap(descendants(card.view).compactMap { $0 as? UISegmentedControl }.first)
        XCTAssertEqual(picker.numberOfSegments, 2)
        picker.selectedSegmentIndex = 1; picker.sendActions(for: .valueChanged)
        XCTAssertEqual(model.method, .manual)
        await eventually {
            card.view.setNeedsLayout(); card.view.layoutIfNeeded()
            return model.method == .manual && !self.descendants(card.view).compactMap { $0 as? UITextField }.isEmpty
        }
        let field = try XCTUnwrap(descendants(card.view).compactMap { $0 as? UITextField }.first)
        XCTAssertTrue(field.isSecureTextEntry)
        field.text = "synthetic-cookie"; field.sendActions(for: .editingChanged)
        await eventually { model.draft["cookie"] == "synthetic-cookie" }
        await eventually { await runtime.count("login.cancel") == 1 }
        XCTAssertNil(model.challenge)
    }

    func testBackgroundCancelsQRAndConfirmedReceiptClosesCardWithoutClearingAccount() async throws {
        let runtime = try PanUIKitRuntime()
        let owner = PanAccountsModel(service: runtime.service)
        owner.start()
        let page = PanAccountsViewController(model: owner, themeProvider: PanAccountsTheme())
        let window = mount(page)
        defer { unmount(page, window: window); owner.close() }
        await eventually { page.canPresentPageAlert }
        let cell = page.tableView(page.table, cellForRowAt: IndexPath(row: 1, section: 0))
        try button("pan.accounts.baidu.login", in: cell).sendActions(for: .touchUpInside)
        await eventually { page.presentedViewController is PanLoginViewController }
        let card = try XCTUnwrap(page.presentedViewController as? PanLoginViewController)
        await waitForPresentedComponent(card)
        await eventually { card.model.challenge != nil }
        NotificationCenter.default.post(name: UIApplication.willResignActiveNotification, object: nil)
        await eventually { await runtime.count("login.cancel") == 1 }
        await runtime.confirmLogin()
        NotificationCenter.default.post(name: UIApplication.didBecomeActiveNotification, object: nil)
        await eventually { card.model.confirmed && page.presentedViewController == nil }
        XCTAssertEqual(owner.rows[0].account?.status, .signedIn)
        let clears = await runtime.count("settings.clear")
        let cancels = await runtime.count("login.cancel")
        XCTAssertEqual(clears, 0); XCTAssertEqual(cancels, 1)
    }

    func testQRCodeEncodesArbitraryURLAndDecodesPNGAndJPEGByActualType() throws {
        for text in ["https://scan.example.test/login?key=synthetic", "custom-login://local/q?synthetic=1"] {
            let image = try PanQRCode.image(.url(text))
            XCTAssertGreaterThan(image.size.width, 100)
            let detector = try XCTUnwrap(CIDetector(ofType: CIDetectorTypeQRCode, context: CIContext(), options: [:]))
            let code = try XCTUnwrap(CIImage(image: image))
            XCTAssertEqual((detector.features(in: code).first as? CIQRCodeFeature)?.messageString, text)
        }
        let image = UIGraphicsImageRenderer(size: CGSize(width: 12, height: 12)).image { context in
            UIColor.black.setFill(); context.fill(CGRect(x: 0, y: 0, width: 12, height: 12))
        }
        for (bytes, type) in [(try XCTUnwrap(image.pngData()), "image/png"),
                              (try XCTUnwrap(image.jpegData(compressionQuality: 0.8)), "image/jpeg")] {
            let decoded = try PanQRCode.image(.image(bytes, mediaType: type))
            XCTAssertEqual(decoded.size.width, 12 * image.scale)
            XCTAssertNotNil(decoded.cgImage)
        }
        XCTAssertThrowsError(try PanQRCode.image(.image(Data("synthetic-invalid-image".utf8), mediaType: "image/jpeg")))
    }

    private func button(_ id: String, in view: UIView) throws -> UIButton {
        try XCTUnwrap(descendants(view).first { $0.accessibilityIdentifier == id } as? UIButton)
    }
    private func titleColor(_ button: UIButton) throws -> UIColor {
        button.setNeedsLayout(); button.layoutIfNeeded()
        let label = try XCTUnwrap(button.titleLabel)
        if let text = label.attributedText, text.length > 0,
           let color = text.attribute(.foregroundColor, at: 0, effectiveRange: nil) as? UIColor {
            return color
        }
        return label.textColor
    }
    private func descendants(_ view: UIView) -> [UIView] { [view] + view.subviews.flatMap(descendants) }
    private func mount(_ page: UIViewController) -> UIWindow {
        let scene = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first
        previousKeyWindow = scene?.windows.first { $0.isKeyWindow }
        let window = scene.map { UIWindow(windowScene: $0) } ?? UIWindow(frame: .zero)
        window.frame = CGRect(x: 0, y: 0, width: 390, height: 844)
        window.rootViewController = page; window.makeKeyAndVisible()
        page.view.layoutIfNeeded()
        return window
    }
    private func waitForPresentedComponent(_ page: PageViewController) async {
        await eventually { page.isPageActive && page.viewIfLoaded?.window != nil && !page.isBeingPresented }
        await eventually { page.canPresentPageAlert }
    }
    private func unmount(_ page: UIViewController, window: UIWindow) {
        if let modal = page.presentedViewController { PageViewController.invalidatePages(in: modal) }
        PageViewController.invalidatePages(in: page)
        window.isHidden = true; window.rootViewController = nil
        previousKeyWindow?.makeKey(); previousKeyWindow = nil
    }
    private func eventually(_ predicate: @escaping @MainActor () async -> Bool,
                            file: StaticString = #filePath, line: UInt = #line) async {
        let deadline = Date().addingTimeInterval(3)
        while Date() < deadline {
            if await predicate() { return }
            try? await Task.sleep(for: .milliseconds(10))
        }
        XCTFail("Pan UIKit did not settle", file: file, line: line)
    }
}

@MainActor
private final class PanAccountsTheme: ObservableObject, PageThemeProviding {
    @Published private(set) var pagePalette: PagePalette = .preset(.lilac)
    let animatesPageColorway = false
    var pageThemeChanges: AnyPublisher<Void, Never> { objectWillChange.eraseToAnyPublisher() }
    func setColorway(_ value: PageColorway) { pagePalette = .preset(value) }
}

private func panUIProvider(_ id: PanProviderID) -> RuntimeValue {
    let fields: [String]
    switch id {
    case .baidu, .quark, .uc: fields = ["cookie"]
    case .tianyi: fields = ["cookie", "username", "password"]
    case .yidong: fields = ["authorization", "refreshedAt"]
    }
    return .object(["provider": .string(id.rawValue), "credentialFields": .array(fields.map(RuntimeValue.string)),
                    "login": .bool([PanProviderID.baidu, .quark, .uc].contains(id)), "refresh": .bool(true)])
}
private func panUICredentials(_ id: PanProviderID) -> RuntimeValue {
    switch id {
    case .baidu, .quark, .uc, .tianyi: return .object(["cookie": .string("synthetic-ui-cookie")])
    case .yidong: return .object(["authorization": .string("synthetic-ui-authorization")])
    }
}
private func panUIAccount(_ id: PanProviderID, _ status: PanAccountStatus = .signedOut, revision: Int = 0,
                          fields: [String]? = nil) -> RuntimeValue {
    let fields = fields ?? (status == .signedOut ? [] : panUICredentials(id).panObject!.keys.sorted())
    return .object(["provider": .string(id.rawValue), "revision": .number(Double(revision)),
             "configured": .bool(!fields.isEmpty), "fields": .array(fields.map(RuntimeValue.string)),
             "status": .string(status.rawValue)])
}
private final class PanUIKitKeys: EncryptedDocumentKeyProvider, @unchecked Sendable {
    private let lock = NSLock()
    private var key: Data?
    func readKey(namespace: String) throws -> Data? { lock.lock(); defer { lock.unlock() }; return key }
    func createKeyIfAbsent(namespace: String) throws -> Data {
        lock.lock(); defer { lock.unlock() }
        if key == nil { key = Data(repeating: 0x57, count: 32) }
        return key!
    }
}
private final class PanUIKitStorage: @unchecked Sendable {
    let root: URL
    let store: EncryptedDocumentStore
    let document: FilmSettingsDocument
    init() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("pan-ui-native-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: false)
        let store = try EncryptedDocumentStore(baseURL: root, namespace: FilmSettingsBlock.panAccounts.namespace,
                                               keyProvider: PanUIKitKeys())
        self.root = root; self.store = store
        document = FilmSettingsDocument(block: .panAccounts, storage: .protected { store })
    }
    func read() throws -> RuntimeValue { try document.read() ?? .object(["version": .number(1), "accounts": .object([:])]) }
    func replace(_ id: PanProviderID, revision: Int, configured: Bool) throws -> RuntimeValue {
        var accounts = try read().panField("accounts")!.panObject!
        accounts[id.rawValue] = .object(["revision": .number(Double(revision)), "credentials":
            configured ? panUICredentials(id) : .object([:])])
        return .object(["version": .number(1), "accounts": .object(accounts)])
    }
    func snapshot(_ id: PanProviderID) throws -> RuntimeValue {
        let record = try read().panField("accounts")?.panField(id.rawValue)
        let fields = (record?.panField("credentials")?.panObject ?? RuntimeObject()).filter { _, value in
            if let text = value.panString { return !text.isEmpty }; return (value.panInteger ?? 0) > 0
        }.map(\.key).sorted()
        return panUIAccount(id, fields.isEmpty ? .signedOut : .signedIn,
                            revision: Int(record?.panField("revision")?.panInteger ?? 0), fields: fields)
    }
    deinit { try? FileManager.default.removeItem(at: root) }
}
private actor PanUIKitRuntime {
    nonisolated let storage: PanUIKitStorage
    private(set) var operations: [String] = []
    private var states: [PanProviderID: (record: RuntimeValue, status: PanAccountStatus)] = [:]
    private var sessionProvider = PanProviderID.baidu
    private var expected: RuntimeValue?
    private var confirmed = false
    init() throws { storage = try PanUIKitStorage() }
    nonisolated var service: PanAccountService {
        .init(document: storage.document, request: { try await self.call($0, $1, $2) })
    }
    func set(_ id: PanProviderID, status: PanAccountStatus) throws {
        let value = try storage.replace(id, revision: 1, configured: status != .signedOut)
        try storage.store.write(RuntimeJSON.encode(value), key: FilmSettingsDocument.key)
        states[id] = (value.panField("accounts")!.panField(id.rawValue)!, status)
    }
    func count(_ operation: String) -> Int { operations.filter { $0 == operation }.count }
    func confirmLogin() { confirmed = true }
    func call(_ operation: String, _ parameters: RuntimeValue, _ timeout: TimeInterval?) throws -> RuntimeValue {
        operations.append(operation)
        let id = parameters.panField("provider")?.panString.flatMap(PanProviderID.init(rawValue:)) ?? sessionProvider
        switch operation {
        case "refresh":
            let record = try storage.read().panField("accounts")?.panField(id.rawValue)
            if states[id]?.status == .expired, states[id]?.record == record { throw RuntimeHTTPError.remote("AUTH_FAILED") }
            return .object(["refreshed": .bool(true), "account": try storage.snapshot(id)])
        case "login.start":
            sessionProvider = id
            expected = try storage.document.snapshot().fingerprint
            return .object(["sessionId": .string("synthetic-ui-session"), "provider": .string(id.rawValue),
                "status": .string("pending"), "expiresAt": .number((Date().timeIntervalSince1970 * 1000).rounded() + 180_000),
                "challenge": .object(["type": .string("url"), "url": .string("https://scan.example.test/synthetic")])])
        case "login.poll":
            var value: RuntimeObject = ["sessionId": .string("synthetic-ui-session"),
                "status": .string(confirmed ? "confirmed" : "pending")]
            if confirmed {
                guard let expected else { throw RuntimeHTTPError.remote("LOGIN_EXPIRED") }
                let document = try storage.replace(id, revision: 2, configured: true)
                try PanAccountService.validateSettingsDocument(document)
                _ = try storage.document.compareAndSwap(expected: expected, document: document)
                self.expected = try storage.document.snapshot().fingerprint
                value["account"] = try storage.snapshot(id)
            }
            return .object(value)
        case "login.cancel": return .object(["cancelled": .bool(true)])
        default:
            XCTFail("Native account settings cannot send \(operation) to runtime")
            throw RuntimeHTTPError.invalidRequest
        }
    }
}
#endif
