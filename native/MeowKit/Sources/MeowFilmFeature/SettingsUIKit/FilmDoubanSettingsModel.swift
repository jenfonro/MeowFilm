#if os(iOS)
import Foundation
import Combine
import MeowRuntime

public enum FilmDoubanDataRoute: String, CaseIterable {
    case direct, cors, cdnTX, cdnAli, custom
    public var title: String {
        switch self {
        case .direct: return "无"
        case .cors: return "Cors Proxy By Zwei"
        case .cdnTX: return "豆瓣 CDN By CMLiussss（腾讯云）"
        case .cdnAli: return "豆瓣 CDN By CMLiussss（阿里云）"
        case .custom: return "自定义代理"
        }
    }
    var addresses: (api: String, search: String, proxy: String)? {
        switch self {
        case .direct: return ("https://m.douban.com", "https://search.douban.com", "")
        case .cors: return ("https://m.douban.com", "https://search.douban.com", "https://ciao-cors.is-an.org/")
        case .cdnTX: return ("https://m.douban.cmliussss.net", "https://search.douban.com", "")
        case .cdnAli: return ("https://m.douban.cmliussss.com", "https://search.douban.com", "")
        case .custom: return nil
        }
    }
}

public enum FilmDoubanImageRoute: String, CaseIterable {
    case direct, cdnTX, cdnAli, custom
    public var title: String {
        switch self {
        case .direct: return "无"
        case .cdnTX: return "豆瓣 CDN By CMLiussss（腾讯云）"
        case .cdnAli: return "豆瓣 CDN By CMLiussss（阿里云）"
        case .custom: return "自定义代理"
        }
    }
    var base: String? {
        switch self {
        case .direct: return "https://img3.doubanio.com"
        case .cdnTX: return "https://img.doubanio.cmliussss.net"
        case .cdnAli: return "https://img.doubanio.cmliussss.com"
        case .custom: return nil
        }
    }
}

@MainActor
public final class FilmDoubanSettingsModel: ObservableObject {
    @Published public private(set) var saved: FilmDoubanSettings?
    @Published public private(set) var busy = false
    @Published public var dataRoute: FilmDoubanDataRoute = .direct
    @Published public var imageRoute: FilmDoubanImageRoute = .direct
    public var apiProxy = "", imageProxy = "", cookie = ""
    private let service: FilmDoubanSettingsService
    private let didSave: @MainActor () -> Void

    public init(service: FilmDoubanSettingsService, didSave: @escaping @MainActor () -> Void) {
        self.service = service; self.didSave = didSave
    }
    public func load() async throws {
        guard saved == nil, !busy else { return }
        busy = true; defer { busy = false }
        apply(try await service.settings())
    }
    public func save() async throws {
        guard !busy, saved != nil else { return }
        busy = true; defer { busy = false }
        let data = dataRoute.addresses
        let patch: RuntimeObject = [
            "apiBase": .string(data?.api ?? "https://m.douban.com"), "searchBase": .string("https://search.douban.com"),
            "apiProxy": .string(data?.proxy ?? apiProxy.trimmingCharacters(in: .whitespacesAndNewlines)),
            "imageBase": .string(imageRoute.base ?? "https://img3.doubanio.com"),
            "imageProxy": .string(imageRoute == .custom ? imageProxy.trimmingCharacters(in: .whitespacesAndNewlines) : ""),
            "cookie": .string(cookie),
        ]
        let value = try await service.updateSettings(patch)
        apply(value)
        didSave()
    }
    private func apply(_ value: FilmDoubanSettings) {
        apiProxy = value.apiProxy; imageProxy = value.imageProxy; cookie = value.cookie
        dataRoute = FilmDoubanDataRoute.allCases.first {
            guard let addresses = $0.addresses else { return false }
            return addresses.api == value.apiBase && addresses.search == value.searchBase && addresses.proxy == value.apiProxy
        } ?? .custom
        imageRoute = FilmDoubanImageRoute.allCases.first { $0.base == value.imageBase && value.imageProxy.isEmpty } ?? .custom
        saved = value
    }
}
#endif
