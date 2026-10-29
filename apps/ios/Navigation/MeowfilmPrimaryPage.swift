enum MeowfilmPrimaryPage: String, CaseIterable, Identifiable {
    case recommend, channels, library
    var id: String { rawValue }
    var title: String {
        switch self {
        case .recommend: return "推荐"
        case .channels: return "频道"
        case .library: return "我的"
        }
    }
}
