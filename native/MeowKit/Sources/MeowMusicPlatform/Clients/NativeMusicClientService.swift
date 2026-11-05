import Foundation
import MeowMusicFeature
import MeowStorage

/// Installation-client state for WY/KG. QQ retains its existing protected
/// document and protocol owner. No construction/admission starts platform I/O.
public final class NativeMusicClientService: MusicNativeClientContextProviding, @unchecked Sendable {
    private let states: [MusicAccountPlatform: MusicClientContextState<[String: String]>]
    private let initialization = NSLock()
    private let clock: @Sendable () -> Date

    public static func namespace(_ platform: MusicAccountPlatform) -> String {
        "music.client-context." + platform.rawValue + ".v1"
    }
    public init(storeFactory: @escaping @Sendable (MusicAccountPlatform) throws -> EncryptedDocumentStore,
                clock: @escaping @Sendable () -> Date = { Date() }) {
        self.clock = clock
        states = Dictionary(uniqueKeysWithValues: [MusicAccountPlatform.netease, .kugou].map { platform in
            (platform, Self.state(platform, document: MusicClientContextDocument(key: "client-device-v1",
                storeFactory: { try storeFactory(platform) })))
        })
    }
    /// Explicit volatile-only construction for standalone consumers/tests.
    /// Production supplies protected stores; their failures never select this.
    public init(clock: @escaping @Sendable () -> Date = { Date() }) {
        self.clock = clock
        states = Dictionary(uniqueKeysWithValues: [MusicAccountPlatform.netease, .kugou].map {
            ($0, Self.state($0, document: nil, available: true))
        })
    }
    init(documents: [MusicAccountPlatform: any MusicClientContextPersisting],
         clock: @escaping @Sendable () -> Date = { Date() }) {
        self.clock = clock
        states = Dictionary(uniqueKeysWithValues: [MusicAccountPlatform.netease, .kugou].map {
            ($0, Self.state($0, document: documents[$0]))
        })
    }
    private static func state(_ platform: MusicAccountPlatform, document: (any MusicClientContextPersisting)?,
                              available: Bool = false) -> MusicClientContextState<[String: String]> {
        .init(document: document, compatibilityOpen: available, decode: { bytes in
            do {
                guard bytes.count <= 32_768 else { throw MusicServiceError.corruptDocument }
                let root = try MusicQQWire.decode(bytes)
                guard Set(root.keys) == ["schema", "platform", "device"],
                      try MusicQQWire.integer(root["schema"]) == 1, root["platform"] as? String == platform.rawValue else {
                    throw MusicServiceError.corruptDocument
                }
                let raw = try MusicQQWire.object(root["device"])
                let value = try raw.mapValues { try MusicQQWire.string($0, maximum: 4096) }
                try validate(value, platform: platform)
                return value
            } catch {
                throw MusicServiceError.corruptDocument
            }
        }, encode: { value in
            try validate(value, platform: platform)
            return try MusicQQWire.json(["schema": 1, "platform": platform.rawValue, "device": value])
        })
    }
    private static func validate(_ fields: [String: String], platform: MusicAccountPlatform) throws {
        if platform == .netease {
            guard Set(fields.keys).isSubset(of: MusicNeteaseWire.deviceKeys),
                  ["deviceId", "_ntes_nuid", "_ntes_nnid", "WNMCID"].allSatisfy({ fields[$0]?.isEmpty == false }) else {
                throw MusicServiceError.corruptDocument
            }
            for value in fields.values { _ = try MusicNeteaseWire.cookieValue(value) }
        } else {
            guard platform == .kugou, fields["seed"] != nil, fields["KUGOU_API_MID"] != nil else {
                throw MusicServiceError.corruptDocument
            }
            _ = try MusicKugouDevice(saved: fields)
        }
    }
    private func state(_ platform: MusicAccountPlatform) throws -> MusicClientContextState<[String: String]> {
        guard let state = states[platform] else { throw MusicServiceError.unsupported }
        return state
    }
    public func setAccess(available: Bool, generation: UInt64) throws {
        for state in states.values { _ = try state.setAccess(available: available, generation: generation) }
    }
    public func revokeAccess(generation: UInt64) {
        for state in states.values { state.revoke(generation: generation) }
    }
    public func close() { for state in states.values { state.close() } }

    private func snapshot(_ platform: MusicAccountPlatform, create: Bool) throws -> MusicNativeClientDevice? {
        let state = try state(platform), token = try state.capture()
        let initial = try state.snapshot(token)
        if let fields = initial.value {
            return .init(platform: platform, fields: fields, admission: token, revision: initial.revision)
        }
        guard create else { return nil }
        initialization.lock(); defer { initialization.unlock() }
        let loaded = try state.snapshot(token)
        if let fields = loaded.value {
            return .init(platform: platform, fields: fields, admission: token, revision: loaded.revision)
        }
        let fields: [String: String]
        if platform == .netease {
            let time = clock().timeIntervalSince1970
            guard time.isFinite, (0...253_402_300_799).contains(time) else { throw MusicServiceError.invalidRequest }
            let nuid = try MusicNeteaseCrypto.text(64, alphabet: "0123456789abcdef")
            let stamp = String(Int(time * 1000))
            fields = ["deviceId": try MusicNeteaseCrypto.text(52, alphabet: "0123456789ABCDEF"),
                "_ntes_nuid": nuid, "_ntes_nnid": nuid + "," + stamp,
                "WNMCID": try MusicNeteaseCrypto.text(6, alphabet: "abcdefghijklmnopqrstuvwxyz") + "." + stamp + ".01.0",
                "NMTID": "00O" + (try MusicQQWire.randomHex(19))]
        } else {
            fields = try MusicKugouDevice().fields.mapValues { try MusicQQWire.string($0, maximum: 4096) }
        }
        try state.save(fields, token: token, expectedRevision: loaded.revision)
        let saved = try state.snapshot(token)
        return .init(platform: platform, fields: fields, admission: token, revision: saved.revision)
    }
    public func nativeClientDevice(platform: MusicAccountPlatform) async throws -> MusicNativeClientDevice {
        try installationDevice(platform)
    }
    func installationDevice(_ platform: MusicAccountPlatform) throws -> MusicNativeClientDevice {
        guard let value = try snapshot(platform, create: true) else { throw MusicServiceError.storageFailed }
        return value
    }
    /// A credential imported from another client keeps its exact bound device.
    /// Matching is identity-based, not account-name or platform-wide guessing.
    func matchingDevice(_ platform: MusicAccountPlatform, identity: String) throws -> MusicNativeClientDevice? {
        guard let value = try snapshot(platform, create: false) else { return nil }
        let key = platform == .netease ? "deviceId" : "seed"
        return value.fields[key] == identity ? value : nil
    }
    func current(_ original: MusicNativeClientDevice) throws -> MusicNativeClientDevice {
        let state = try state(original.platform), saved = try state.snapshot(original.admission)
        guard let fields = saved.value else { throw MusicServiceError.storageFailed }
        let key = original.platform == .netease ? "deviceId" : "seed"
        guard fields[key] == original.fields[key] else { throw MusicServiceError.targetChanged }
        return .init(platform: original.platform, fields: fields, admission: original.admission, revision: saved.revision)
    }
    func update(_ original: MusicNativeClientDevice, fields: [String: String],
                removing: Set<String> = []) throws -> MusicNativeClientDevice {
        let state = try state(original.platform)
        let key = original.platform == .netease ? "deviceId" : "seed"
        let saved = try state.update(original.admission) { current in
            guard current[key] == original.fields[key] else { throw MusicServiceError.verificationRequired }
            guard fields[key] == nil || fields[key] == current[key] else { throw MusicServiceError.verificationRequired }
            guard !removing.contains(key) else { throw MusicServiceError.verificationRequired }
            var merged = current
            for name in removing { merged.removeValue(forKey: name) }
            for (name, value) in fields { merged[name] = value }
            do { try Self.validate(merged, platform: original.platform) }
            catch { throw MusicServiceError.upstreamInvalid }
            return merged
        }
        return .init(platform: original.platform, fields: saved.value, admission: original.admission,
                     revision: saved.revision)
    }
    public func acceptNativeClientResponse(_ device: MusicNativeClientDevice, url: URL,
                                          headers: [String: String]) async throws -> MusicNativeClientDevice {
        guard device.platform == .netease, url.scheme == "https",
              ["music.163.com", "interface.music.163.com", "interfacepc.music.163.com", "interface3.music.163.com"].contains(url.host ?? ""),
              url.user == nil, url.password == nil, url.fragment == nil, url.port == nil || url.port == 443,
              let host = url.host else {
            throw MusicServiceError.invalidRequest
        }
        let cookies = try MusicNeteaseWire.cookies(.init(status: 200, data: Data(), headers: headers), host: host)
        var fields: [String: String] = [:], removing = Set<String>()
        for (key, value) in cookies where MusicNeteaseWire.deviceKeys.contains(key) {
            if let value { fields[key] = value } else { removing.insert(key) }
        }
        if fields.isEmpty && removing.isEmpty { return try current(device) }
        return try update(device, fields: fields, removing: removing)
    }
}
