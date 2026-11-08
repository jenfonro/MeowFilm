enum MeowfilmPrimaryPage: String, CaseIterable, Identifiable {
    case recommend, channels, film, library
    var id: String { rawValue }
    var title: String {
        switch self {
        case .recommend: return "首页"
        case .channels: return "频道"
        case .film: return "影视"
        case .library: return "我的"
        }
    }
}
