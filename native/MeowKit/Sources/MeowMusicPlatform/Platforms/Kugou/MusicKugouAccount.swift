// SPDX-License-Identifier: GPL-3.0-only
// Fixed meowmusic cc22a7e kugou/account.go. SMS lives in MusicAccountSMS.
import Foundation
import CoreImage
import CoreGraphics
import MeowAccounts
import MeowMusicFeature

extension KugouMusicAccountAdapter {
    func newContext(qrID: UUID? = nil, qrNonce: UUID? = nil) throws -> MusicKugouContext {
        if let clientContexts {
            let client = try clientContexts.installationDevice(.kugou)
            return .init(epoch: epoch, device: try MusicKugouDevice(saved: client.fields),
                         qrID: qrID, qrNonce: qrNonce, clientDevice: client)
        }
        return .init(epoch: epoch, device: try MusicKugouDevice(), qrID: qrID, qrNonce: qrNonce)
    }
    func context(_ credential: AccountCredential) throws -> MusicKugouContext {
        let bytes = credential.dataRepresentation()
        guard bytes.count <= 32_768 else { throw MusicServiceError.limit }
        let root = try MusicQQWire.decode(bytes)
        guard Set(root.keys) == ["version", "platform", "accountID", "auth", "device"],
              try MusicKugouWire.integer(root["version"]) == 1, root["platform"] as? String == "kg" else {
            throw MusicServiceError.invalidResponse
        }
        let auth = try MusicKugouAuth(MusicQQWire.object(root["auth"]), strict: true)
        guard try MusicKugouWire.id(root["accountID"]) == auth.uid else { throw MusicServiceError.targetChanged }
        let rawDevice = try MusicQQWire.object(root["device"])
        // A restored credential must not silently invent a different device.
        guard rawDevice["seed"] != nil, rawDevice["KUGOU_API_MID"] != nil else { throw MusicServiceError.invalidResponse }
        let device = try MusicKugouDevice(saved: rawDevice)
        let client = try clientContexts?.matchingDevice(.kugou, identity: device.seed)
        return .init(epoch: epoch, device: try client.map { try MusicKugouDevice(saved: $0.fields) } ?? device,
                     auth: auth, clientDevice: client)
    }
    func credential(_ context: MusicKugouContext) throws -> AccountCredential {
        try check(context)
        guard let auth = context.auth else { throw MusicServiceError.authRequired }
        let data = try MusicKugouWire.json(["version": 1, "platform": "kg", "accountID": auth.uid,
                                          "auth": auth.fields, "device": context.device.fields])
        guard data.count <= 32_768 else { throw MusicServiceError.limit }
        return try .init(data: data)
    }
    func profile(_ context: MusicKugouContext) async throws -> String {
        try await displayProfile(context).nickname
    }
    func displayProfile(_ context: MusicKugouContext) async throws -> MusicAccountProfile {
        try check(context)
        guard let auth = context.auth else { throw MusicServiceError.authRequired }
        let seconds = Int(now().timeIntervalSince1970)
        let p = try MusicKugouCrypto.rsaRaw(MusicKugouWire.json(["clienttime": seconds, "token": auth.token]))
        let root = try await request(.profile, context: context, query: ["plat": "1"],
                                     body: ["visit_time": seconds, "usertype": 1, "p": p.uppercased(),
                                            "userid": Int(auth.uid)!])
        try check(context)
        let data = try MusicQQWire.object(root["data"])
        // b900c259 /user/detail legitimately omits userid. This authenticated
        // request is already pinned to auth.uid; a returned different UID is
        // never accepted, and the nickname is never used as an identity.
        if let uid = data["userid"], !(uid is NSNull),
           try MusicKugouWire.id(uid) != auth.uid { throw MusicServiceError.targetChanged }
        return .init(nickname: try MusicKugouWire.publicText(MusicKugouWire.text(data["nickname"], maximum: 512, empty: true), auth: auth),
                     avatarURL: try MusicAccountProfileWire.avatar(data["pic"], platform: .kugou,
                                                                  secrets: [auth.token, auth.t1?.secret ?? "", auth.vipToken ?? ""]))
    }
    func nativeID(_ credential: AccountCredential) throws -> String { try context(credential).auth!.uid }
    private func prepared(_ credential: AccountCredential) async throws -> MusicKugouContext {
        var context = try context(credential)
        if context.device.dfid == nil {
            var anonymous = context; anonymous.auth = nil
            let registered = try await register(anonymous)
            try check(context)
            context.device = registered.device
            context.clientDevice = registered.clientDevice
        }
        return context
    }
    func verify(_ credential: AccountCredential) async throws -> MusicQQVerified {
        let context = try await prepared(credential)
        let nickname = try await profile(context)
        try check(context)
        return .init(credential: try self.credential(context), nickname: nickname, nativeID: context.auth!.uid)
    }
    func refresh(_ credential: AccountCredential) async throws -> MusicQQVerified {
        let context = try await prepared(credential)
        let original = context.auth!, milliseconds = Int(now().timeIntervalSince1970 * 1000)
        let p3 = try MusicKugouCrypto.aes(MusicKugouWire.json(["clienttime": milliseconds / 1000, "token": original.token]),
            key: "90b8382a1bb4ccdcf063102053fd75b8", iv: "f063102053fd75b8")
        let seed = try MusicKugouCrypto.randomHex(8)
        let envelope = try MusicKugouCrypto.loginEnvelope(milliseconds: milliseconds, seed: seed)
        let root = try await request(.refresh, context: context, body: [
            "dfid": context.device.dfid ?? "-", "p3": MusicKugouCrypto.hex(p3), "plat": 1, "t1": 0, "t2": 0,
            "t3": "MCwwLDAsMCwwLDAsMCwwLDA=", "pk": envelope.pk, "params": envelope.params,
            "userid": Int(original.uid)!, "clienttime_ms": milliseconds
        ])
        return try await verifiedRefresh(MusicQQWire.object(root["data"]), seed: seed, context: context)
    }
    func verifiedRefresh(_ raw: [String: Any], seed: String, context originalContext: MusicKugouContext) async throws -> MusicQQVerified {
        var context = originalContext
        try check(context)
        guard let original = context.auth else { throw MusicServiceError.authRequired }
        if let uid = raw["userid"], try MusicKugouWire.id(uid) != original.uid { throw MusicServiceError.targetChanged }
        let auth = try MusicKugouAuth(MusicKugouCrypto.loginData(raw, seed: seed))
        guard auth.uid == original.uid else { throw MusicServiceError.targetChanged }
        context.auth = auth
        let nickname = try await profile(context)
        try check(context)
        return .init(credential: try self.credential(context), nickname: nickname, nativeID: auth.uid)
    }
    func register(_ context: MusicKugouContext) async throws -> MusicKugouContext {
        guard context.auth == nil else { throw MusicServiceError.invalidRequest }
        if context.device.dfid != nil { return context }
        var payload: [String: Any] = [
            "availableRamSize": 4_983_533_568, "availableRomSize": 48_114_719, "availableSDSize": 48_114_717,
            "basebandVer": "", "batteryLevel": 100, "batteryStatus": 3, "brand": "Redmi", "buildSerial": "unknown",
            "device": "marble", "imei": context.device.seed, "imsi": "", "manufacturer": "Xiaomi", "uuid": context.device.seed
        ]
        // Legacy protocol fixture fields; these are NOT measured iPhone hardware
        // attributes and no hardware identifier/sensor API is called.
        for name in ["accelerometer", "gravity", "gyroscope", "light", "magnetic", "orientation", "pressure", "step_counter", "temperature"] {
            payload[name] = false; payload[name + "Value"] = ""
        }
        let seed = try MusicKugouCrypto.randomHex(3), key = MusicKugouCrypto.md5(seed)
        let envelope = try MusicKugouCrypto.registration(payload, seed: seed)
        let response = try await raw(.register, context: context, query: ["part": "1", "platid": "1", "p": envelope.p],
                                     body: envelope.body, legacy: true)
        // Plaintext business rejections are an observed protocol alternative.
        let plain = (try? MusicKugouCrypto.aes(response.data, key: String(key.prefix(16)), iv: String(key.suffix(16)), decrypt: true)) ?? response.data
        let root = try MusicQQWire.decode(plain)
        try MusicKugouWire.business(root, route: .register)
        if let rows = root["data"] as? [Any], rows.isEmpty { return context }
        let dfid = try MusicKugouWire.text(MusicQQWire.object(root["data"])["dfid"], maximum: 128)
        guard dfid.range(of: "^[A-Za-z0-9_-]{8,128}$", options: .regularExpression) != nil else { throw MusicServiceError.upstreamInvalid }
        var next = context; next.device.dfid = dfid
        if let client = next.clientDevice, let clientContexts {
            let saved = try clientContexts.update(client, fields: ["dfid": dfid])
            next.clientDevice = saved; next.device = try MusicKugouDevice(saved: saved.fields)
        }
        return next
    }
    static func qrImage(_ key: String) throws -> Data {
        let url = try MusicQQWire.query("https://h5.kugou.com/apps/loginQRCode/html/index.html", ["qrcode": key])
        guard let filter = CIFilter(name: "CIQRCodeGenerator") else { throw MusicServiceError.unavailable }
        filter.setValue(Data(url.absoluteString.utf8), forKey: "inputMessage")
        filter.setValue("M", forKey: "inputCorrectionLevel")
        guard let image = filter.outputImage, let color = CGColorSpace(name: CGColorSpace.sRGB) else { throw MusicServiceError.unavailable }
        let background = CIImage(color: CIColor(red: 1, green: 1, blue: 1)).cropped(to: image.extent.insetBy(dx: -4, dy: -4))
        let padded = image.composited(over: background).transformed(by: CGAffineTransform(scaleX: 6, y: 6))
        guard let bytes = CIContext(options: [.useSoftwareRenderer: true]).pngRepresentation(of: padded, format: .RGBA8, colorSpace: color),
              bytes.count <= 1024 * 1024 else { throw MusicServiceError.unavailable }
        return bytes
    }
    func createQR(id: UUID) async throws -> MusicLoginChallenge {
        try Task.checkCancellation()
        guard now() >= nextCreate else { throw MusicServiceError.rateLimited }
        guard qr[id] == nil else { throw MusicServiceError.busy }
        for old in Array(qr.keys) { cancelQR(old) }
        nextCreate = now().addingTimeInterval(5)
        let context = try newContext(qrID: id, qrNonce: UUID())
        qr[id] = .init(expires: now().addingTimeInterval(180), context: context)
        do {
            let registered = try await register(context)
            let root = try await request(.qrCreate, context: registered, query: [
                "appid": "1001", "type": "1", "plat": "4", "srcappid": "2919",
                "qrcode_txt": "https://h5.kugou.com/apps/loginQRCode/html/index.html?appid=1005&"
            ])
            try check(registered)
            let key = try MusicKugouWire.text(MusicQQWire.object(root["data"])["qrcode"], maximum: 256)
            guard key.range(of: "^[A-Za-z0-9_-]{1,256}$", options: .regularExpression) != nil else { throw MusicServiceError.upstreamInvalid }
            let image = try Self.qrImage(key), expires = now().addingTimeInterval(180)
            try check(registered)
            qr[id] = .init(expires: expires, context: registered, key: key, busy: false)
            return .init(id: id, platform: .kugou, image: image, expiresAt: expires)
        } catch {
            if qr[id]?.context.qrNonce == context.qrNonce { cancelQR(id) }
            throw error
        }
    }
    func pollQR(id: UUID) async throws -> MusicQQLoginResult {
        try Task.checkCancellation()
        guard let current = qr[id] else { throw MusicServiceError.staleResult }
        if current.expires <= now() { cancelQR(id); return .init(progress: .expired, verified: nil) }
        guard !current.busy, let key = current.key else { throw MusicServiceError.busy }
        if let last = current.lastPoll, now().timeIntervalSince(last) < 3 { throw MusicServiceError.rateLimited }
        qr[id]?.busy = true; qr[id]?.lastPoll = now()
        defer {
            if qr[id]?.context.qrNonce == current.context.qrNonce { qr[id]?.busy = false }
        }
        let root = try await request(.qrPoll, context: current.context, query: [
            "plat": "4", "appid": "1005", "srcappid": "2919", "qrcode": key, "dev": current.context.device.dev
        ])
        let data = try MusicQQWire.object(root["data"])
        switch try MusicKugouWire.integer(data["status"]) {
        case 0: cancelQR(id); return .init(progress: .expired, verified: nil)
        case 1: return .init(progress: .waiting, verified: nil)
        case 2: return .init(progress: .scanned, verified: nil)
        case 4:
            var authenticated = current.context
            authenticated.auth = try MusicKugouAuth(data)
            let nickname = try await profile(authenticated)
            try check(authenticated)
            let verified = MusicQQVerified(credential: try credential(authenticated), nickname: nickname, nativeID: authenticated.auth!.uid)
            qr.removeValue(forKey: id)
            return .init(progress: .confirmed, verified: verified)
        default: throw MusicServiceError.upstreamInvalid
        }
    }
}
