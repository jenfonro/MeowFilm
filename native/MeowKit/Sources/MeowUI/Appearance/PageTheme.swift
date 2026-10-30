// SPDX-License-Identifier: GPL-3.0-only
// Palette and gradient rules from iWebPlus app-colors.js; rendering values only.
public struct PageGradientStop: Equatable, Sendable {
    public let location: Double
    public let color: PageColor
}

public struct PageTheme: Equatable, Sendable {
    public let palette: PageColorway
    public let dark: Bool
    public let top, bottom, mid, ink, muted, accent, surface, border, chip, headerDark: PageColor
    public var headerMuted: PageColor { ink.mixed(with: top, ratio: 0.2) }
    public var onAccent: PageColor { .init(rgb: dark ? 0x191724 : 0xffffff) }
    public var control: PageColor { .init(rgb: 0xffffff, alpha: dark ? 14 : 102) }
    public var buttonSurface: PageColor { .init(rgb: 0xffffff, alpha: dark ? 15 : 117) }
    public var buttonSelected: PageColor { .init(rgb: 0xffffff, alpha: dark ? 32 : 186) }
    public var panel: PageColor { .init(rgb: 0xffffff, alpha: dark ? 6 : 61) }

    public init(palette: PageColorway = .lilac, dark: Bool = false) {
        self.palette = palette; self.dark = dark
        top = dark ? .hsl(palette.hue, saturation: 25, lightness: 18) : palette.lightTop
        bottom = dark ? .hsl(palette.hue, saturation: 20, lightness: 11) : .init(rgb: 0xf6fbfe)
        mid = top.mixed(with: bottom, ratio: 0.68)
        accent = dark ? palette.lightAccent.mixed(with: .init(rgb: 0xffffff), ratio: 0.55) : palette.lightAccent
        ink = .init(rgb: dark ? 0xf3f1f8 : 0x39343e); muted = .init(rgb: dark ? 0xb4b2c0 : 0x7f7a7d)
        surface = top.mixed(with: .init(rgb: 0xffffff), ratio: dark ? 0.045 : 0.89)
        border = top.mixed(with: .init(rgb: dark ? 0xffffff : 0x75737d), ratio: dark ? 0.17 : 0.15)
        chip = dark ? top.mixed(with: .init(rgb: 0xffffff), ratio: 0.04) : top.mixed(with: bottom, ratio: 0.48)
        headerDark = .hsl(palette.hue, saturation: 28, lightness: 27)
    }

    /// app-colors.js replaces the CSS bootstrap's 32%/56% three-stop gradient.
    /// The safe top band stays the exact top color, never separately darkened.
    public func gradientStops(safeTopFraction: Double = 0) -> [PageGradientStop] {
        let safeTop = safeTopFraction.isFinite ? min(1, max(0, safeTopFraction)) : 0
        let stops: [(Double, Double)] = [(0, 0), (0.144, 0.42), (0.216, 0.62), (0.288, 0.75),
                                         (0.36, 0.85), (0.432, 0.95), (0.504, 1), (1, 1)]
        return stops.map { .init(location: max(safeTop, $0.0), color: top.mixed(with: bottom, ratio: $0.1)) }
    }
}
