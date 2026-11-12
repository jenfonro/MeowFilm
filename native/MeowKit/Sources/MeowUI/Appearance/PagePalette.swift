import Foundation

/// A display palette is either a named preset or an opaque user-selected base
/// color. It contains no preference storage or song-sampling behavior.
public enum PagePalette: Equatable, Codable, Sendable {
    case preset(PageColorway)
    case custom(UInt32)

    public var title: String {
        if case .preset(let value) = self { return value.title }
        return "自定义颜色"
    }

    public var baseColor: PageColor {
        switch self {
        case .preset(let value): return value.lightAccent
        case .custom(let rgb): return PageColor(rgb: rgb)
        }
    }

    public var hue: Double {
        switch self {
        case .preset(let value): return value.hue
        case .custom: return components.hue
        }
    }

    public var lightTop: PageColor {
        switch self {
        case .preset(let value): return value.lightTop
        case .custom: return .hsl(hue, saturation: components.saturation * 0.42, lightness: 86)
        }
    }

    public var lightAccent: PageColor {
        switch self {
        case .preset(let value): return value.lightAccent
        case .custom:
            let color = PageColor.hsl(hue, saturation: components.saturation * 0.65, lightness: 36)
            // A bright yellow base must not turn text or a white-labelled
            // control into an unreadable yellow-on-white surface.
            var result = color
            while result.contrastRatio(with: .init(rgb: 0xffffff)) < 5 {
                result = result.mixed(with: .init(rgb: 0), ratio: 0.04)
            }
            return result
        }
    }

    var darkSaturation: Double {
        if case .preset = self { return 25 }
        return components.saturation * 0.35
    }

    private var components: (hue: Double, saturation: Double) {
        let color = baseColor
        let r = Double(color.red) / 255, g = Double(color.green) / 255, b = Double(color.blue) / 255
        let high = max(r, g, b), low = min(r, g, b), delta = high - low
        guard delta > 0 else { return (0, 0) }
        let lightness = (high + low) / 2
        let saturation = delta / (1 - abs(2 * lightness - 1))
        let sector: Double
        if high == r { sector = ((g - b) / delta).truncatingRemainder(dividingBy: 6) }
        else if high == g { sector = (b - r) / delta + 2 }
        else { sector = (r - g) / delta + 4 }
        let hue = sector * 60
        return (hue < 0 ? hue + 360 : hue, saturation * 100)
    }

    public init(from decoder: Decoder) throws {
        let container = try decoder.singleValueContainer()
        let text = try container.decode(String.self)
        if let preset = PageColorway(rawValue: text) {
            self = .preset(preset)
        } else if text.count == 7, text.first == "#", let rgb = UInt32(text.dropFirst(), radix: 16) {
            self = .custom(rgb)
        } else {
            throw DecodingError.dataCorruptedError(in: container, debugDescription: "Invalid page palette")
        }
    }

    public func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        switch self {
        case .preset(let value): try container.encode(value.rawValue)
        case .custom(let rgb): try container.encode(String(format: "#%06X", rgb & 0xffffff))
        }
    }
}
