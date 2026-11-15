#if os(iOS)
import XCTest
import UIKit
import SwiftUI
@testable import MeowUI

@MainActor
final class SettingsBackgroundContinuityTests: XCTestCase {
    func testGroupedCardOutlineAndFillStayContinuousAcrossUnequalRows() throws {
        let heights = [SettingsVisualMetrics.rowHeight, SettingsVisualMetrics.rowHeight + 32,
                       SettingsVisualMetrics.rowHeight + 8]
        for dark in [false, true] {
            let theme = PageTheme(palette: .lilac, dark: dark)
            for settings in [false, true] {
                for selected in [false, true] {
                    let card = UIView(frame: CGRect(x: 0, y: 0, width: 240,
                                                   height: heights.reduce(0, +)))
                    card.backgroundColor = UIColor(theme.bottom)
                    var rowTop: CGFloat = 0
                    var joins: [CGFloat] = []
                    for (index, height) in heights.enumerated() {
                        let row = SettingsRowBackgroundView(
                            theme: theme, first: index == 0, last: index == heights.count - 1,
                            selected: selected, settings: settings, showsSeparator: index < heights.count - 1
                        )
                        row.frame = CGRect(x: 0, y: rowTop, width: card.bounds.width, height: height)
                        card.addSubview(row)
                        row.layoutIfNeeded()
                        rowTop += height
                        if index < heights.count - 1 { joins.append(rowTop) }
                    }
                    let image = render(card)
                    XCTAssertNotEqual(try pixel(image, x: 0.25, y: 12),
                                      try pixel(image, x: 0.25, y: heights[0] - 4),
                        "The group's upper edge must retain its existing fade")
                    for join in joins {
                        for x in [CGFloat(0.25), card.bounds.width - 0.25] {
                            let above = try pixel(image, x: x, y: join - 0.25)
                            let below = try pixel(image, x: x, y: join + 0.25)
                            assertMatchingPixels(above, below,
                                "The grouped outline must not restart at an internal row boundary")
                        }
                    }
                    let surface = settings ? (selected ? theme.settings.selected : theme.settings.surface) : theme.surface
                    let expected: [UInt8] = [surface.red, surface.green, surface.blue, surface.alpha]
                    var top: CGFloat = 0
                    for height in heights {
                        // Stay inside the outline and before the row's inset divider.
                        for y in [top + 2, top + height / 2, top + height - 2] {
                            assertMatchingPixels(try pixel(image, x: 12, y: y), expected,
                                "Every row of the card must retain the same opaque semantic fill")
                        }
                        top += height
                    }
                }
            }
        }
    }

    func testHostingPageKeepsTheWindowCanvasAndConsumesTheTopSafeAreaOnce() async throws {
        let provider = TestPageTheme()
        let page = HostingPageViewController(themeProvider: provider, content: Color.clear)
        page.pageTitle = "平台账号"
        let navigation = PageNavigationController(themeProvider: provider)
        navigation.setViewControllers([page], animated: false)
        let scene = try XCTUnwrap(UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first)
        let previousKeyWindow = scene.windows.first { $0.isKeyWindow }
        let window = UIWindow(windowScene: scene)
        window.frame = scene.coordinateSpace.bounds
        window.rootViewController = navigation
        window.makeKeyAndVisible()
        let reference = WindowGradientView(themeProvider: provider)
        reference.frame = window.bounds
        window.insertSubview(reference, at: 0)
        defer {
            PageViewController.invalidatePages(in: navigation)
            window.isHidden = true
            window.rootViewController = nil
            previousKeyWindow?.makeKey()
        }
        for dark in [false, true] {
            window.overrideUserInterfaceStyle = dark ? .dark : .light
            let flushed = expectation(description: "hosting appearance delivered")
            RunLoop.main.perform { flushed.fulfill() }
            await fulfillment(of: [flushed], timeout: 2)
            window.layoutIfNeeded()
            page.view.layoutIfNeeded()
            reference.layoutIfNeeded()
            let host = try XCTUnwrap(page.children.first as? UIHostingController<Color>)
            XCTAssertEqual(window.bounds.size, scene.coordinateSpace.bounds.size)
            XCTAssertEqual(page.theme.dark, dark)
            XCTAssertEqual(reference.theme, page.theme)
            XCTAssertTrue(page.hasOwnPageHeader)
            XCTAssertEqual(page.pageHeader.frame.height,
                page.view.safeAreaInsets.top + PageLayoutMetrics.navigation, accuracy: 0.5)
            XCTAssertEqual(host.view.frame.minY, page.pageHeader.frame.maxY, accuracy: 0.5)
            XCTAssertEqual(host.view.frame.maxY, page.view.bounds.maxY, accuracy: 0.5)
            XCTAssertEqual(host.view.safeAreaInsets.top, 0, accuracy: 0.5)
            XCTAssertEqual(host.view.safeAreaInsets.bottom, page.view.safeAreaInsets.bottom, accuracy: 0.5)
            let actual = render(page.view)
            let expected = render(reference)
            let safeTop = page.view.safeAreaInsets.top
            let headerBottom = page.pageHeader.frame.maxY
            for y in [max(0, safeTop - 1), safeTop + 1, headerBottom - 1,
                      headerBottom + 1, headerBottom + 24, page.view.bounds.midY] {
                assertMatchingPixels(try pixel(actual, x: 4, y: y), try pixel(expected, x: 4, y: y),
                    "The page header and hosted content must expose the same window-coordinate gradient")
            }
        }
    }

    private func render(_ view: UIView) -> UIImage {
        let format = UIGraphicsImageRendererFormat()
        format.scale = 2
        format.preferredRange = .standard
        return UIGraphicsImageRenderer(bounds: view.bounds, format: format).image {
            view.layer.render(in: $0.cgContext)
        }
    }

    private func pixel(_ image: UIImage, x: CGFloat, y: CGFloat) throws -> [UInt8] {
        let source = try XCTUnwrap(image.cgImage)
        let sample = try XCTUnwrap(source.cropping(to: CGRect(
            x: (x * image.scale).rounded(.down), y: (y * image.scale).rounded(.down),
            width: 1, height: 1)))
        var bytes = [UInt8](repeating: 0, count: 4)
        try bytes.withUnsafeMutableBytes { buffer in
            let context = try XCTUnwrap(CGContext(data: buffer.baseAddress, width: 1, height: 1,
                bitsPerComponent: 8, bytesPerRow: 4, space: CGColorSpaceCreateDeviceRGB(),
                bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
            context.draw(sample, in: CGRect(x: 0, y: 0, width: 1, height: 1))
        }
        return bytes
    }

    private func assertMatchingPixels(
        _ first: [UInt8], _ second: [UInt8], _ message: String,
        file: StaticString = #filePath, line: UInt = #line
    ) {
        for (a, b) in zip(first, second) {
            XCTAssertLessThanOrEqual(abs(Int(a) - Int(b)), 2, message, file: file, line: line)
        }
    }
}
#endif
