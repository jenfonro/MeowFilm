// SPDX-License-Identifier: GPL-3.0-only
// Fixed iWebPlus b900c259 recommendation.js mode names and native spellings.
import Foundation
import MeowUI

public struct MusicFMModeChoice: Identifiable, Equatable, Sendable {
    public let title: String
    public let mode: MusicHomeAction.FMMode
    public let submode: String
    public var id: String { mode.rawValue + ":" + submode }
    public var action: MusicHomeAction { .fm(mode: mode, submode: submode, entry: nil) }
    public static let main: [MusicFMModeChoice] = [
        .init(title: "默认模式", mode: .default, submode: ""),
        .init(title: "熟悉模式", mode: .familiar, submode: ""),
        .init(title: "探索模式", mode: .explore, submode: ""),
        .init(title: "拼图模式", mode: .puzzle, submode: "")
    ]
    public static let scenes: [MusicFMModeChoice] = {
        let names = "伤感|运动|助眠|放松|欢快|抒情|治愈|专注|情歌|R&B|下雨天|打游戏|说唱|K-Pop|宝藏原创|电音|出行|洗澡|咖啡馆|摇滚|励志|华语|欧美|粤语|慢摇DJ|经典|轻音乐|国风|民谣|二次元|古典|爵士|日语|全球|法语|蓝调|舞蹈|拉丁|放克|乡村乐|音乐剧|影视|热歌".components(separatedBy: "|")
        let codes = "NIGHT_EMO EXERCISE SLEEP_HELP RELAX CHEERFUL LYRICAL CURE FOCUS SWEET RHYTHM_BLUES RAINY GAMES RAP K_POP ORIGINAL_MUSICIAL ELECTRONIC COMMUTE TAKE_SHOWER COFFEE_SHOP ROCK INSPIRATIONAL CHINESE ENGLISH YUEYU MANYAO JINGDIAN LIGHT GUOFENG FOLK ACG GUDIAN JAZZ JAPANESE GLOBAL FRANCH BLUE DANCE LATIN PUNK COUNTRY MUSICAL YINGSHI HOT_SONG".components(separatedBy: " ")
        return zip(names, codes).map { .init(title: $0.0, mode: .scene, submode: $0.1) }
    }()
    public static func title(for action: MusicHomeAction?) -> String {
        switch action {
        case .fm(let mode, let submode, _):
            return (main + scenes).first { $0.mode == mode && $0.submode == submode }?.title ?? "私人漫游"
        case .ruleFM(_, _, let name): return name.isEmpty ? "频道 FM" : name
        default: return "私人 FM"
        }
    }
    public func matches(_ action: MusicHomeAction?) -> Bool {
        guard case .fm(let mode, let submode, _) = action else { return false }
        return mode == self.mode && submode == self.submode
    }
}
