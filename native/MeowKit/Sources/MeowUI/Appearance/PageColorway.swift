// SPDX-License-Identifier: GPL-3.0-only
// Reference-stable visual choices. No song identity, sampling or storage.
public enum PageColorway: String, Codable, CaseIterable, Sendable {
    case lilac, coral, mint, silver, lemon, rose, periwinkle, peach, sky
    public var title: String {
        switch self {
        case .lilac: return "星紫"
        case .coral: return "桃雾"
        case .mint: return "薄荷"
        case .silver: return "银霜"
        case .lemon: return "暖柠"
        case .rose: return "蔷薇"
        case .periwinkle: return "雾蓝"
        case .peach: return "杏橙"
        case .sky: return "晴空"
        }
    }
    public var hue: Double {
        switch self {
        case .lilac: return 262
        case .coral: return 355
        case .mint: return 151
        case .silver: return 220
        case .lemon: return 52
        case .rose: return 317
        case .periwinkle: return 229
        case .peach: return 32
        case .sky: return 208
        }
    }
    public var lightTop: PageColor {
        let values: [UInt32] = [0xd7d1f7, 0xefcfd0, 0xe1fced, 0xdad8d9, 0xfcf7d7, 0xf0d0e7, 0xdae1fb, 0xf6e7d4, 0xd7e8fa]
        return .init(rgb: values[index])
    }
    public var lightAccent: PageColor {
        let values: [UInt32] = [0x7864b5, 0x99535f, 0x377860, 0x646976, 0x806727, 0x995d88, 0x5d6fa8, 0x926840, 0x4b739b]
        return .init(rgb: values[index])
    }
    private var index: Int { Self.allCases.firstIndex(of: self)! }
}
