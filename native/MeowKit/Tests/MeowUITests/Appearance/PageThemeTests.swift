// SPDX-License-Identifier: GPL-3.0-only
import XCTest
@testable import MeowUI

final class PageThemeTests: XCTestCase {
    func testRuntimeGradientUsesEightStopsAndExactTopSafeBand() {
        let tokens = PageTheme()
        let stops = tokens.gradientStops(safeTopFraction: 0.06)
        XCTAssertEqual(stops.map(\.location), [0.06, 0.144, 0.216, 0.288, 0.36, 0.432, 0.504, 1])
        XCTAssertEqual(stops[0].color, tokens.top)
        XCTAssertEqual(stops[1].color, tokens.top.mixed(with: tokens.bottom, ratio: 0.42))
        XCTAssertEqual(stops[6].color, tokens.bottom)
        XCTAssertEqual(stops[7].color, tokens.bottom)
    }

    func testAllLightAndDarkHeadersHaveReferenceContrast() {
        func luminance(_ color: PageColor) -> Double {
            let components = [color.red, color.green, color.blue].map { value -> Double in
                let v = Double(value) / 255
                return v <= 0.04045 ? v / 12.92 : pow((v + 0.055) / 1.055, 2.4)
            }
            return components[0] * 0.2126 + components[1] * 0.7152 + components[2] * 0.0722
        }
        func contrast(_ a: PageColor, _ b: PageColor) -> Double {
            let x = luminance(a), y = luminance(b)
            return (max(x, y) + 0.05) / (min(x, y) + 0.05)
        }
        for palette in PageColorway.allCases {
            for dark in [false, true] {
                let tokens = PageTheme(palette: palette, dark: dark)
                XCTAssertGreaterThanOrEqual(contrast(tokens.ink, tokens.top), 7, palette.rawValue)
                XCTAssertGreaterThanOrEqual(contrast(tokens.ink, tokens.bottom), 7, palette.rawValue)
                XCTAssertGreaterThanOrEqual(contrast(tokens.accent, tokens.bottom), 4.5, palette.rawValue)
                XCTAssertGreaterThanOrEqual(contrast(.init(rgb: 0xffffff), tokens.headerDark), 4.5, palette.rawValue)
            }
        }
    }
}
