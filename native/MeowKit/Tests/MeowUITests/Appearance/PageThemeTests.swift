// SPDX-License-Identifier: GPL-3.0-only
import XCTest
@testable import MeowUI

final class PageThemeTests: XCTestCase {
    func testCustomBaseColorsProduceReadableLightAndDarkThemes() {
        let colors: [UInt32] = [0, 0xffffff, 0xff0000, 0x00ff00, 0x0000ff, 0xffff00, 0xff00ff, 0x35a4cd]
        for rgb in colors {
            for dark in [false, true] {
                let theme = PageTheme(colors: .custom(rgb), dark: dark)
                XCTAssertGreaterThanOrEqual(theme.ink.contrastRatio(with: theme.top), 7)
                XCTAssertGreaterThanOrEqual(theme.ink.contrastRatio(with: theme.bottom), 7)
                XCTAssertGreaterThanOrEqual(theme.accent.contrastRatio(with: theme.bottom), 4.5)
                XCTAssertGreaterThanOrEqual(theme.onAccent.contrastRatio(with: theme.accent), 4.5)
                XCTAssertEqual(theme.top.alpha, 255)
                XCTAssertEqual(theme.bottom.alpha, 255)
            }
        }
        XCTAssertNotEqual(PageTheme(colors: .custom(0xff0000)).top, PageTheme(colors: .custom(0x0000ff)).top)
        let neutral = PageTheme(colors: .custom(0x808080))
        for color in [neutral.top, neutral.accent, neutral.headerDark] {
            XCTAssertEqual(color.red, color.green)
            XCTAssertEqual(color.green, color.blue)
        }
    }

    func testPresetStorageRemainsANameAndCustomColorRoundTripsExactly() throws {
        let encoder = JSONEncoder(), decoder = JSONDecoder()
        XCTAssertEqual(try encoder.encode(PagePalette.preset(.mint)), Data(#""mint""#.utf8))
        XCTAssertEqual(try decoder.decode(PagePalette.self, from: Data(#""mint""#.utf8)), .preset(.mint))
        let value = PagePalette.custom(0x40ab93)
        XCTAssertEqual(try decoder.decode(PagePalette.self, from: encoder.encode(value)), value)
        XCTAssertThrowsError(try decoder.decode(PagePalette.self, from: Data("\"#oops\"".utf8)))
    }

    func testRuntimeGradientUsesEightStopsAndExactTopSafeBand() {
        let tokens = PageTheme()
        let stops = tokens.gradientStops(safeTopFraction: 0.06)
        XCTAssertEqual(stops.map(\.location), [0.06, 0.144, 0.216, 0.288, 0.36, 0.432, 0.504, 1])
        XCTAssertEqual(stops[0].color, tokens.top)
        XCTAssertEqual(stops[1].color, tokens.top.mixed(with: tokens.bottom, ratio: 0.42))
        XCTAssertEqual(stops[6].color, tokens.bottom)
        XCTAssertEqual(stops[7].color, tokens.bottom)
    }

    func testTextColorsAreSharedAcrossPalettesAndRespectAppearance() {
        for palette in PageColorway.allCases {
            let light = PageTheme(palette: palette)
            XCTAssertEqual(light.ink, .init(rgb: 0x39343e), palette.rawValue)
            XCTAssertEqual(light.muted, .init(rgb: 0x7f7a7d), palette.rawValue)
            let dark = PageTheme(palette: palette, dark: true)
            XCTAssertEqual(dark.ink, .init(rgb: 0xf3f1f8), palette.rawValue)
            XCTAssertEqual(dark.muted, .init(rgb: 0xb4b2c0), palette.rawValue)
        }
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
