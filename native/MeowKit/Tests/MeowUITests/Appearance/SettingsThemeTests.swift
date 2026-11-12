import XCTest
@testable import MeowUI

final class SettingsThemeTests: XCTestCase {
    func testSettingsColorsAreIndependentOfPresetAndCustomPalettes() {
        let palettes = PageColorway.allCases.map(PagePalette.preset) +
            [0, 0xffffff, 0xff0000, 0x00ff00, 0x0000ff, 0xffff00].map(PagePalette.custom)
        for dark in [false, true] {
            let expected = SettingsTheme(dark: dark)
            for palette in palettes {
                let page = PageTheme(colors: palette, dark: dark)
                XCTAssertEqual(page.settings, expected)
                XCTAssertNotEqual(page.settings.accent, page.settings.danger)
                XCTAssertEqual(page.settings.surface.alpha, 255)
                XCTAssertEqual(page.settings.input.alpha, 255)
                XCTAssertGreaterThanOrEqual(expected.muted.contrastRatio(with: page.top), 4.5)
                XCTAssertGreaterThanOrEqual(expected.muted.contrastRatio(with: page.bottom), 4.5)
            }
        }
        XCTAssertNotEqual(PageTheme(palette: .coral).top, PageTheme(palette: .mint).top)
        XCTAssertNotEqual(SettingsTheme(dark: false), SettingsTheme(dark: true))
    }

    func testSettingsTextControlsAndStatusColorsRemainReadable() {
        for dark in [false, true] {
            let colors = SettingsTheme(dark: dark)
            for surface in [colors.surface, colors.input, colors.selected] {
                XCTAssertGreaterThanOrEqual(colors.ink.contrastRatio(with: surface), 7)
                for ink in [colors.muted, colors.accent, colors.danger, colors.success, colors.warning] {
                    XCTAssertGreaterThanOrEqual(ink.contrastRatio(with: surface), 4.5)
                }
            }
            XCTAssertGreaterThanOrEqual(colors.onAccent.contrastRatio(with: colors.accent), 4.5)
            XCTAssertGreaterThanOrEqual(colors.inputBorder.contrastRatio(with: colors.input), 3)
            for color in [colors.surface, colors.input, colors.separator, colors.border] {
                XCTAssertLessThanOrEqual(Int(max(color.red, color.green, color.blue)) -
                    Int(min(color.red, color.green, color.blue)), 17, "Structural colors stay neutral")
            }
        }
    }
}
