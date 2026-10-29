// SPDX-License-Identifier: GPL-3.0-only
import Foundation

public struct PageColor: Equatable, Sendable {
    public let red: UInt8
    public let green: UInt8
    public let blue: UInt8
    public let alpha: UInt8
    public var rgb: UInt32 { UInt32(red) << 16 | UInt32(green) << 8 | UInt32(blue) }
    public var rgba: UInt32 { rgb << 8 | UInt32(alpha) }
    public init(rgb: UInt32, alpha: UInt8 = 255) {
        red = UInt8((rgb >> 16) & 255); green = UInt8((rgb >> 8) & 255); blue = UInt8(rgb & 255); self.alpha = alpha
    }
    public init(rgba: UInt32) { self.init(rgb: rgba >> 8, alpha: UInt8(rgba & 255)) }
    public func mixed(with other: Self, ratio: Double) -> Self {
        let amount = ratio.isFinite ? min(1, max(0, ratio)) : 0
        func blend(_ a: UInt8, _ b: UInt8) -> UInt32 {
            UInt32((Double(a) * (1 - amount) + Double(b) * amount).rounded())
        }
        return .init(rgba: blend(red, other.red) << 24 | blend(green, other.green) << 16 |
                     blend(blue, other.blue) << 8 | blend(alpha, other.alpha))
    }
    static func hsl(_ hue: Double, saturation: Double, lightness: Double) -> Self {
        let s = saturation / 100, l = lightness / 100, a = s * min(l, 1 - l)
        func component(_ n: Double) -> UInt32 {
            let k = (n + hue / 30).truncatingRemainder(dividingBy: 12)
            return UInt32(((l - a * max(-1, min(k - 3, 9 - k, 1))) * 255).rounded())
        }
        return .init(rgb: component(0) << 16 | component(8) << 8 | component(4))
    }
}
