#if os(iOS)
import XCTest
import UIKit
import SwiftUI
@testable import MeowUI

@MainActor
final class HostingPageViewControllerTests: XCTestCase {
    func testContentUpdatesKeepOneTransparentHostingChild() throws {
        let page = HostingLifecycleTestPage(
            themeProvider: PageLifecycleTestTheme(), content: Text("first")
        )
        page.setContent(Text("before loading"))
        XCTAssertFalse(page.isViewLoaded)
        XCTAssertTrue(page.children.isEmpty)
        page.loadViewIfNeeded()
        let host = try XCTUnwrap(page.children.first as? UIHostingController<Text>)
        let contentView = try XCTUnwrap(host.view)
        XCTAssertEqual(page.children.count, 1)
        XCTAssertTrue(host.parent === page)
        let background = try XCTUnwrap(page.view as? WindowGradientView)
        XCTAssertTrue(background.isOpaque)
        XCTAssertEqual(background.backgroundColor, UIColor(page.theme.bottom))
        XCTAssertTrue(background.themeProvider === page.themeProvider)
        XCTAssertEqual(contentView.backgroundColor, .clear)
        page.setContent(Text("updated"))
        page.beginAppearanceTransition(true, animated: false)
        page.endAppearanceTransition()
        page.beginAppearanceTransition(false, animated: false)
        page.endAppearanceTransition()
        page.beginAppearanceTransition(true, animated: false)
        page.endAppearanceTransition()
        XCTAssertTrue(page.children.first === host)
        XCTAssertTrue(host.view === contentView)
        XCTAssertEqual(page.children.count, 1)
        XCTAssertEqual(page.activeCount, 1)
        XCTAssertEqual(page.inactiveCount, 0)
    }

    func testSettingContentPreservesEditedTextAndFocus() async throws {
        let page = HostingPageViewController(
            themeProvider: PageLifecycleTestTheme(), content: HostingInputTestContent(initialText: "initial")
        )
        let scene = try XCTUnwrap(UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first)
        let previousKeyWindow = scene.windows.first { $0.isKeyWindow }
        let window = UIWindow(windowScene: scene)
        window.rootViewController = page
        window.makeKeyAndVisible()
        defer {
            window.isHidden = true
            window.rootViewController = nil
            previousKeyWindow?.makeKey()
        }
        page.view.layoutIfNeeded()
        await flushMainRunLoop()
        let field = try XCTUnwrap(textField(in: page.view))
        XCTAssertEqual(field.text, "initial")
        XCTAssertTrue(field.becomeFirstResponder())
        field.text = "edited"
        field.sendActions(for: .editingChanged)
        await flushMainRunLoop()
        let host = try XCTUnwrap(page.children.first)
        page.setContent(HostingInputTestContent(initialText: "replacement"))
        page.view.setNeedsLayout()
        page.view.layoutIfNeeded()
        await flushMainRunLoop()
        XCTAssertTrue(page.children.first === host)
        XCTAssertTrue(textField(in: page.view) === field)
        XCTAssertEqual(field.text, "edited")
        XCTAssertTrue(field.isFirstResponder)
    }

    private func textField(in view: UIView) -> UITextField? {
        if let field = view as? UITextField { return field }
        for child in view.subviews {
            if let field = textField(in: child) { return field }
        }
        return nil
    }

    private func flushMainRunLoop() async {
        let flushed = expectation(description: "hosting update completed")
        RunLoop.main.perform { flushed.fulfill() }
        await fulfillment(of: [flushed], timeout: 2)
    }
}

@MainActor
private final class HostingLifecycleTestPage: HostingPageViewController<Text> {
    var activeCount = 0, inactiveCount = 0
    override func pageDidBecomeActive() { activeCount += 1 }
    override func pageDidBecomeInactive() { inactiveCount += 1 }
}

private struct HostingInputTestContent: SwiftUI.View {
    @State private var text: String

    init(initialText: String) {
        _text = State(initialValue: initialText)
    }

    var body: some SwiftUI.View {
        TextField("input", text: $text)
            .accessibilityIdentifier("hosting.test.input")
    }
}
#endif
