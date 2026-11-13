import XCTest
import MeowUI
@testable import MeowMusicFeature

final class MusicSettingsPresentationContractTests: XCTestCase {
    func testAppearanceModesExposeDynamicAndFixedColorChoices() {
        XCTAssertEqual(MusicColorMode.song.title, "灵动模式")
        XCTAssertEqual(MusicColorMode.fixed.title, "固定颜色")
        XCTAssertEqual(MusicColorMode.allCases, [.song, .fixed])
    }

    func testSettingsTextAndActionsStayReadableAcrossAppearanceAndPaletteChanges() {
        for dark in [false, true] {
            let settings = PageTheme(palette: .sky, dark: dark).settings
            for palette in [PagePalette.preset(.coral), .preset(.mint), .custom(0xffff00)] {
                XCTAssertEqual(PageTheme(colors: palette, dark: dark).settings, settings)
            }
            for foreground in [settings.ink, settings.muted, settings.accent, settings.danger] {
                XCTAssertGreaterThanOrEqual(foreground.contrastRatio(with: settings.surface), 4.5)
            }
            XCTAssertGreaterThanOrEqual(settings.ink.contrastRatio(with: settings.input), 4.5)
            XCTAssertGreaterThanOrEqual(settings.ink.contrastRatio(with: settings.selected), 4.5)
        }
        XCTAssertNotEqual(PageTheme(palette: .sky).settings.surface,
                          PageTheme(palette: .sky, dark: true).settings.surface)
    }
}
