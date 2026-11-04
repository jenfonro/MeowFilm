// SMS intent/limits ported from songloft-plugin-iwebplus b900c259 src/{netease,kugou}.ts.
import Foundation
import CryptoKit
import MeowAccounts
import MeowMusicFeature

protocol MusicAccountSMSProvider: MusicAccountProvider {
    func sendSMS(countryCode: String, phone: String, attemptID: UUID) async throws
    func loginSMS(countryCode: String, phone: String, code: String, attemptID: UUID) async throws -> MusicQQVerified
    func cancelSMS(_ id: UUID) async
}

enum MusicAccountSMSInput {
    static func validate(platform: MusicAccountPlatform, countryCode: String, phone: String, code: String? = nil) throws {
        func digits(_ value: String, _ count: ClosedRange<Int>) -> Bool {
            count.contains(value.utf8.count) && value.utf8.allSatisfy { (48...57).contains($0) }
        }
        guard platform != .qq,
              digits(countryCode, 1...4), countryCode.first != "0", digits(phone, 5...15),
              countryCode != "86" || phone.utf8.count == 11 && phone.first == "1",
              platform != .kugou || countryCode == "86",
              code == nil || digits(code!, 4...8) else {
            throw MusicServiceError.invalidRequest
        }
    }
}

/// Memory only, hashed phone keys. Sending keeps its own cooldown and single
/// flight; there is no cross-login attempt quota. An uncertain send is never replayed.
struct MusicAccountSMSLimits {
    struct Entry { var until: Date; var sendAt = Date.distantPast; var busy = false }
    var entries: [String: Entry] = [:]
    mutating func reserve(platform: MusicAccountPlatform, country: String, phone: String, send: Bool, at: Date) throws -> String {
        entries = entries.filter { $0.value.until > at || $0.value.busy }
        let key = Data(SHA256.hash(data: Data("\(platform.rawValue):\(country):\(phone)".utf8)))
            .map { String(format: "%02x", $0) }.joined()
        var entry = entries[key] ?? Entry(until: at.addingTimeInterval(600))
        guard !entry.busy, send ? entry.sendAt <= at : true else {
            throw MusicSMSFailure(reason: .rateLimited, retryAt: max(at.addingTimeInterval(1), entry.sendAt))
        }
        entry.busy = true
        if send { entry.sendAt = at.addingTimeInterval(60) }
        entries[key] = entry
        return key
    }
    mutating func release(_ key: String) { entries[key]?.busy = false }
}

extension NeteaseMusicAccountAdapter: MusicAccountSMSProvider {
    func cancelSMS(_ id: UUID) { sms.removeValue(forKey: id) }
    private func smsContext(_ id: UUID) throws -> MusicNeteaseContext {
        if let context = sms[id] { return context }
        guard sms.count < 1 else { throw MusicServiceError.busy }
        let context = MusicNeteaseContext(); sms[id] = context; return context
    }
    func sendSMS(countryCode: String, phone: String, attemptID: UUID) async throws {
        try MusicAccountSMSInput.validate(platform: .netease, countryCode: countryCode, phone: phone)
        let own = epoch, context = try smsContext(attemptID)
        context.cookies = [:]; context.accountID = ""; context.permitsCredentialUpdate = false
        _ = try await request("/api/sms/captcha/sent", data: [
            "ctcode": countryCode, "secrete": "music_middleuser_pclogin", "cellphone": phone
        ], crypto: "weapi", context: context)
        try check(own)
        guard sms[attemptID] === context else { throw MusicServiceError.staleResult }
    }
    func loginSMS(countryCode: String, phone: String, code: String, attemptID: UUID) async throws -> MusicQQVerified {
        try MusicAccountSMSInput.validate(platform: .netease, countryCode: countryCode, phone: phone, code: code)
        let own = epoch, context = try smsContext(attemptID)
        context.cookies = [:]; context.accountID = ""; context.permitsCredentialUpdate = false
        let root = try await request("/api/w/login/cellphone", data: [
            "type": "1", "https": "true", "phone": phone, "countrycode": countryCode,
            "captcha": code, "remember": "true", "secureCaptcha": ""
        ], crypto: "weapi", context: context)
        try check(own)
        guard sms[attemptID] === context else { throw MusicServiceError.staleResult }
        guard context.cookies["MUSIC_U"]?.isEmpty == false else { throw MusicServiceError.authRequired }
        if let row = root["profile"] as? [String: Any] { context.accountID = try MusicNeteaseWire.id(row["userId"]) }
        if let account = root["account"] as? [String: Any], let raw = account["id"] {
            let id = try MusicNeteaseWire.id(raw)
            guard context.accountID.isEmpty || context.accountID == id else { throw MusicServiceError.targetChanged }
            context.accountID = id
        }
        context.permitsCredentialUpdate = true
        let value = try await verifiedUpdate(context)
        try check(own)
        guard sms[attemptID] === context else { throw MusicServiceError.staleResult }
        return value
    }
}

extension KugouMusicAccountAdapter: MusicAccountSMSProvider {
    // Upstream API pinned by b900c259: KuGouMusicApi 70c3154,
    // module/captcha_sent.js + login_cellphone.js (non-lite protocol).
    func cancelSMS(_ id: UUID) { sms.removeValue(forKey: id) }
    private func smsContext(_ id: UUID) async throws -> MusicKugouContext {
        if let context = sms[id] { try check(context); return context }
        guard sms.count < 1 else { throw MusicServiceError.busy }
        let context = try newContext()
        sms[id] = context
        do {
            let registered = try await register(context)
            guard sms[id]?.device.seed == context.device.seed else { throw MusicServiceError.staleResult }
            try check(registered)
            sms[id] = registered; return registered
        } catch {
            if sms[id]?.device.seed == context.device.seed { sms.removeValue(forKey: id) }
            throw error
        }
    }
    func sendSMS(countryCode: String, phone: String, attemptID: UUID) async throws {
        try MusicAccountSMSInput.validate(platform: .kugou, countryCode: countryCode, phone: phone)
        let context = try await smsContext(attemptID)
        _ = try await request(.smsSend, context: context, body: ["businessid": 5, "mobile": phone, "plat": 3])
        try check(context)
        guard sms[attemptID]?.device.seed == context.device.seed else { throw MusicServiceError.staleResult }
    }
    func loginSMS(countryCode: String, phone: String, code: String, attemptID: UUID) async throws -> MusicQQVerified {
        try MusicAccountSMSInput.validate(platform: .kugou, countryCode: countryCode, phone: phone, code: code)
        var context = try await smsContext(attemptID)
        let ms = Int(now().timeIntervalSince1970 * 1000), seed = try MusicKugouCrypto.randomHex(8)
        let key = MusicKugouCrypto.md5(seed)
        let encrypted = try MusicKugouCrypto.aes(MusicKugouWire.json(["mobile": phone, "code": code]),
                                                key: key, iv: String(key.suffix(16)))
        let pk = try MusicKugouCrypto.rsaRaw(MusicKugouWire.json(["clienttime_ms": ms, "key": seed])).uppercased()
        let root = try await request(.smsLogin, context: context, body: [
            "plat": 1, "support_multi": 1, "t1": 0, "t2": 0, "t3": "MCwwLDAsMCwwLDAsMCwwLDA=",
            "clienttime_ms": ms, "mobile": String(phone.prefix(2)) + "*****" + String(phone.suffix(1)),
            "key": MusicKugouCrypto.paramsKey(ms), "pk": pk, "params": MusicKugouCrypto.hex(encrypted)
        ])
        try check(context)
        guard sms[attemptID]?.device.seed == context.device.seed else { throw MusicServiceError.staleResult }
        let raw = try MusicQQWire.object(root["data"])
        let auth = try MusicKugouAuth(MusicKugouCrypto.loginData(raw, seed: seed))
        if let id = raw["userid"], try MusicKugouWire.id(id) != auth.uid { throw MusicServiceError.targetChanged }
        context.auth = auth
        let name = try await profile(context)
        try check(context)
        guard sms[attemptID]?.device.seed == context.device.seed else { throw MusicServiceError.staleResult }
        return .init(credential: try credential(context), nickname: name, nativeID: auth.uid)
    }
}
