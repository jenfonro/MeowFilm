#if os(iOS)
import Foundation
import Combine
import MeowRuntime

public enum FilmDoubanDataRoute: String, CaseIterable {
    case direct, cors, corsAnywhere, custom
    public var title: String {
        switch self {
        case .direct: return "直连"
        case .cors: return "CORS"
        case .corsAnywhere: return "cors-anywhere"
        case .custom: return "自定义"
        }
    }
    var addresses: (api: String, search: String, proxy: String)? {
        switch self {
        case .direct: return ("https://m.douban.com", "https://search.douban.com", "")
        case .cors: return ("https://m.douban.com", "https://search.douban.com", "https://ciao-cors.is-an.org/{encodedUrl}")
        case .corsAnywhere: return ("https://m.douban.com", "https://search.douban.com", "https://cors-anywhere.com/{url}")
        case .custom: return nil
        }
    }
}

public enum FilmDoubanImageRoute: String, CaseIterable {
    case direct, cdnTX, cdnAli, custom
    public var title: String {
        switch self {
        case .direct: return "直连"
        case .cdnTX: return "腾讯 CDN"
        case .cdnAli: return "阿里 CDN"
        case .custom: return "自定义"
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
    public var apiBase = "", searchBase = "", imageBase = "", apiProxy = "", imageProxy = ""
    public var cookie = "" { didSet { if !cookie.isEmpty { clearCookie = false } } }
    public private(set) var clearCookie = false
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
    public func clearCredential() { cookie = ""; clearCookie = true; objectWillChange.send() }
    public func save() async throws {
        guard !busy, saved != nil else { return }
        busy = true; defer { busy = false }
        let data = dataRoute.addresses
        var patch: RuntimeObject = [
            "apiBase": .string(data?.api ?? apiBase), "searchBase": .string(data?.search ?? searchBase),
            "apiProxy": .string(data?.proxy ?? apiProxy),
            "imageBase": .string(imageRoute.base ?? imageBase),
            "imageProxy": .string(imageRoute == .custom ? imageProxy : ""),
        ]
        if !cookie.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { patch["cookie"] = .string(cookie) }
        else if clearCookie { patch["cookie"] = .string("") }
        let value = try await service.updateSettings(patch)
        cookie = ""; clearCookie = false
        apply(value)
        didSave()
    }
    private func apply(_ value: FilmDoubanSettings) {
        apiBase = value.apiBase; searchBase = value.searchBase; imageBase = value.imageBase
        apiProxy = value.apiProxy; imageProxy = value.imageProxy
        dataRoute = FilmDoubanDataRoute.allCases.first {
            guard let addresses = $0.addresses else { return false }
            return addresses.api == value.apiBase && addresses.search == value.searchBase && addresses.proxy == value.apiProxy
        } ?? .custom
        imageRoute = FilmDoubanImageRoute.allCases.first { $0.base == value.imageBase && value.imageProxy.isEmpty } ?? .custom
        saved = value
    }
}
#endif
