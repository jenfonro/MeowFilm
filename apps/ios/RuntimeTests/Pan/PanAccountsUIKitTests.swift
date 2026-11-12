#if os(iOS)
import XCTest
import UIKit
import SwiftUI
import CoreImage
import Combine
import MeowRuntime
import MeowUI
@testable import MeowFilmFeature

@MainActor
final class PanAccountsUIKitTests: XCTestCase {
    private weak var previousKeyWindow: UIWindow?

    func testTableHasExactlyThreeColumnsAtPhoneWidthsWithThreeStateActionsAndTheme() async throws {
        let runtime = PanUIKitRuntime()
        await runtime.set(.baidu, status: .signedIn)
        await runtime.set(.quark, status: .expired)
        let model = PanAccountsModel(service: runtime.service)
        await model.start()?.value
        let theme = PanAccountsTheme()
        let page = PanAccountsViewController(model: model, themeProvider: theme)
        page.loadViewIfNeeded()
        defer { PageViewController.invalidatePages(in: page); model.close() }
        XCTAssertEqual(page.tableView(page.table, numberOfRowsInSection: 0), 8)
        let header = page.tableView(page.table, cellForRowAt: IndexPath(row: 0, section: 0))
        XCTAssertEqual(descendants(header).compactMap { ($0 as? UILabel)?.text }, ["平台", "状态", "操作"])
        for (index, status, titles) in [(1, "已登录", ["刷新", "退出登录"]), (2, "失效", ["重新登录", "退出登录"]), (3, "未登录", ["登录"])] {
            let cell = page.tableView(page.table, cellForRowAt: IndexPath(row: index, section: 0))
            let columns = try XCTUnwrap(descendants(cell).compactMap { $0 as? PanAccountColumns }.first)
            XCTAssertEqual(columns.arrangedSubviews.count, 3)
            XCTAssertEqual(descendants(columns.columns[1]).compactMap { ($0 as? UILabel)?.text }, [status])
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
                }
            }
        }
        page.overrideUserInterfaceStyle = .dark
        theme.setColorway(.sky); page.applyTheme()
        let darkCell = page.tableView(page.table, cellForRowAt: IndexPath(row: 1, section: 0))
        XCTAssertEqual(darkCell.backgroundColor, UIColor(page.theme.surface))
        let status = try XCTUnwrap(descendants(darkCell).first { $0.accessibilityIdentifier == "pan.accounts.status" } as? UILabel)
        XCTAssertEqual(status.textColor, UIColor(page.theme.muted))
    }

    func testTableRefreshAndLogoutButtonsCallRuntimeAndLoginOpensSharedFloatingCard() async throws {
        let runtime = PanUIKitRuntime()
        await runtime.set(.baidu, status: .signedIn)
        let model = PanAccountsModel(service: runtime.service)
        await model.start()?.value
        let page = PanAccountsViewController(model: model, themeProvider: PanAccountsTheme())
        let window = mount(page)
        defer { unmount(page, window: window); model.close() }
        await eventually { page.canPresentPageAlert && !model.loading && !model.rows.contains(where: \.busy) }
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
        XCTAssertEqual(count, 1, "Closing/background never repeats the explicit account clear")
    }

    func testManualFloatingFormBindsFieldsHidesSecretsAndNeverLoadsOldValues() async throws {
        let runtime = PanUIKitRuntime()
        let model = PanAccountsModel(service: runtime.service)
        await model.start()?.value
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
        XCTAssertEqual(calls.filter { $0 == "settings.get" }.count, 1, "Opening login must not read old secrets")
        NotificationCenter.default.post(name: UIApplication.willResignActiveNotification, object: nil)
        XCTAssertFalse(login.confirmed)
    }

    func testQRMethodPickerBindsManualCookieAndCancelsQR() async throws {
        let runtime = PanUIKitRuntime()
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
        let runtime = PanUIKitRuntime()
        let owner = PanAccountsModel(service: runtime.service)
        await owner.start()?.value
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
    @Published private(set) var pageColorway: PageColorway = .lilac
    let animatesPageColorway = false
    var pageThemeChanges: AnyPublisher<Void, Never> { objectWillChange.eraseToAnyPublisher() }
    func setColorway(_ value: PageColorway) { pageColorway = value }
}

private func panUIProvider(_ id: PanProviderID) -> RuntimeValue {
    let fields: [String]
    switch id {
    case .baidu, .quark, .uc: fields = ["cookie"]
    case .tianyi: fields = ["cookie", "username", "password"]
    case .yidong: fields = ["authorization", "refreshedAt"]
    case .quarkTV, .ucTV: fields = ["refreshToken", "deviceId", "accessToken", "expiresAt"]
    }
    return .object(["provider": .string(id.rawValue), "credentialFields": .array(fields.map(RuntimeValue.string)),
                    "login": .bool([PanProviderID.baidu, .quark, .uc].contains(id)), "refresh": .bool(true)])
}
private func panUIAccount(_ id: PanProviderID, _ status: PanAccountStatus = .signedOut, revision: Int = 0) -> RuntimeValue {
    .object(["provider": .string(id.rawValue), "revision": .number(Double(revision)),
             "configured": .bool(status != .signedOut), "fields": .array(status == .signedOut ? [] : [.string("cookie")]),
             "status": .string(status.rawValue)])
}
private actor PanUIKitRuntime {
    private(set) var operations: [String] = []
    private var states = Dictionary(uniqueKeysWithValues: PanProviderID.allCases.map { ($0, panUIAccount($0)) })
    private var sessionProvider = PanProviderID.baidu
    private var confirmed = false
    nonisolated var service: PanAccountService { .init(request: { try await self.call($0, $1, $2) }) }
    func set(_ id: PanProviderID, status: PanAccountStatus) { states[id] = panUIAccount(id, status, revision: 1) }
    func count(_ operation: String) -> Int { operations.filter { $0 == operation }.count }
    func confirmLogin() { confirmed = true }
    func call(_ operation: String, _ parameters: RuntimeValue, _ timeout: TimeInterval?) throws -> RuntimeValue {
        operations.append(operation)
        let id = parameters.panField("provider")?.panString.flatMap(PanProviderID.init(rawValue:)) ?? sessionProvider
        switch operation {
        case "providers": return .object(["providers": .array(PanProviderID.allCases.map(panUIProvider))])
        case "settings.get": return .object(["accounts": .array(PanProviderID.allCases.compactMap { states[$0] })])
        case "refresh":
            if states[id]?.panField("status") == .string("expired") { throw RuntimeHTTPError.remote("AUTH_FAILED") }
            return .object(["refreshed": .bool(true), "account": states[id]!])
        case "settings.clear":
            let revision = Int(states[id]?.panField("revision")?.panInteger ?? 0) + 1
            states[id] = panUIAccount(id, revision: revision); return states[id]!
        case "login.start":
            sessionProvider = id
            return .object(["sessionId": .string("synthetic-ui-session"), "provider": .string(id.rawValue),
                "status": .string("pending"), "expiresAt": .number((Date().timeIntervalSince1970 * 1000).rounded() + 180_000),
                "challenge": .object(["type": .string("url"), "url": .string("https://scan.example.test/synthetic")])])
        case "login.poll":
            var value: RuntimeObject = ["sessionId": .string("synthetic-ui-session"),
                "status": .string(confirmed ? "confirmed" : "pending")]
            if confirmed { states[id] = panUIAccount(id, .signedIn, revision: 2); value["account"] = states[id] }
            return .object(value)
        case "login.cancel": return .object(["cancelled": .bool(true)])
        default: throw RuntimeHTTPError.invalidRequest
        }
    }
}
#endif
