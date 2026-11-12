import MeowUI

struct MusicLyricColors: Equatable {
    static let artworkOpacity = 0.24
    let normal: PageColor
    let unsung: PageColor
    let highlight: PageColor

    init(backgrounds: [PageColor], accent: PageColor, artwork: Bool) {
        let black = PageColor(rgb: 0x000000), white = PageColor(rgb: 0xffffff)
        // Cover pixels and the selected row's translucent surface can darken
        // any part of the viewport. Check the composed bounds, not the cover hue.
        let bounds = backgrounds.flatMap { color -> [PageColor] in
            let covered = artwork ? color.mixed(with: black, ratio: Self.artworkOpacity) : color
            return [color, covered, covered.mixed(with: black, ratio: 0.08)]
        }
        func contrast(_ color: PageColor) -> Double {
            bounds.map { color.contrastRatio(with: $0) }.min() ?? 1
        }
        let ink = contrast(black) >= contrast(white) ? black : white
        func readable(_ color: PageColor, minimum: Double) -> PageColor {
            for step in 0...32 {
                let candidate = color.mixed(with: ink, ratio: Double(step) / 32)
                if contrast(candidate) >= minimum { return candidate }
            }
            return ink
        }
        normal = readable(ink.mixed(with: white, ratio: 0.38), minimum: 4.5)
        unsung = normal
        highlight = readable(accent, minimum: max(4.5, min(7, contrast(ink) - 0.1)))
    }
}
