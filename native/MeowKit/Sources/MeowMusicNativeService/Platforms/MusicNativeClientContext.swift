import Foundation
import MeowMusicFeature

extension MusicNativePlatforms {
    func clientDevice(_ platform: MusicAccountPlatform) async throws -> MusicNativeClientDevice {
        let own = epoch
        try musicRequire(platform == .netease || platform == .kugou, .unsupported)
        let value: MusicNativeClientDevice
        if let clients { value = try await clients.nativeClientDevice(platform: platform) }
        else {
            // Explicit standalone/volatile construction, not a fallback from
            // failed protected storage. Product injects the durable owner.
            if let saved = volatileClients[platform] { return saved }
            let fields: [String: String]
            switch platform {
            case .netease:
                fields = ["deviceId": (0..<26).map { _ in String(format: "%02X", UInt8.random(in: 0...255)) }.joined()]
            case .kugou: fields = ["KUGOU_API_MID": MusicPlatformProtocol.md5(UUID().uuidString.lowercased())]
            case .qq: throw MusicServiceError.unsupported
            }
            value = .init(platform: platform, fields: fields, admission: epoch, revision: UUID())
            volatileClients[platform] = value
        }
        try Task.checkCancellation()
        try musicRequire(own == epoch, .staleResult)
        let allowed: Set<String> = platform == .netease
            ? ["deviceId", "_ntes_nuid", "_ntes_nnid", "WNMCID", "NMTID"]
            : ["seed", "KUGOU_API_GUID", "KUGOU_API_MID", "KUGOU_API_DEV", "KUGOU_API_MAC", "dfid"]
        let required = platform == .netease ? "deviceId" : "KUGOU_API_MID"
        try musicRequire(value.platform == platform && Set(value.fields.keys).isSubset(of: allowed) &&
            value.fields[required]?.isEmpty == false && value.fields.values.allSatisfy {
                !$0.isEmpty && $0.utf8.count <= 4096 && $0.utf8.allSatisfy { $0 >= 33 && $0 <= 126 && $0 != 34 && $0 != 59 && $0 != 92 }
            }, .invalidResponse)
        return value
    }
}
