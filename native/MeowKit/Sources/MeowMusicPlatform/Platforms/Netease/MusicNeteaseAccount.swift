// SPDX-License-Identifier: GPL-3.0-only
// Fixed meowmusic cc22a7e netease/auth.go. QR rendering is local CoreImage.
import Foundation
import CoreImage
import CoreGraphics
import CryptoKit
import MeowAccounts
import MeowMusicFeature

extension NeteaseMusicAccountAdapter {
    func context(_ credential: AccountCredential) throws -> MusicNeteaseContext {
        let bytes = credential.dataRepresentation()
        guard bytes.count <= 32_768 else { throw MusicServiceError.limit }
        let raw = try MusicQQWire.decode(bytes)
        guard Set(raw.keys) == ["version", "platform", "accountID", "cookies", "device"],
              try MusicQQWire.integer(raw["version"]) == 1, raw["platform"] as? String == "wy" else {
            throw MusicServiceError.invalidResponse
        }
        let cookies = try strings(raw["cookies"], allowed: MusicNeteaseWire.credentialKeys)
        let device = try strings(raw["device"], allowed: MusicNeteaseWire.deviceKeys)
        guard cookies["MUSIC_U"]?.isEmpty == false else { throw MusicServiceError.authRequired }
        return .init(cookies: cookies, device: device, accountID: try MusicNeteaseWire.id(raw["accountID"]))
    }
    func strings(_ raw: Any?, allowed: Set<String>) throws -> [String: String] {
        let values = try MusicQQWire.object(raw)
        guard Set(values.keys).isSubset(of: allowed) else { throw MusicServiceError.invalidResponse }
        return try values.mapValues { try MusicNeteaseWire.cookieValue($0, empty: true) }
    }
    func credential(_ context: MusicNeteaseContext) throws -> AccountCredential {
        let id = try MusicNeteaseWire.id(context.accountID)
        let cookies = context.cookies.filter { MusicNeteaseWire.credentialKeys.contains($0.key) }
        guard cookies["MUSIC_U"]?.isEmpty == false else { throw MusicServiceError.authRequired }
        let bytes = try MusicQQWire.json(["version": 1, "platform": "wy", "accountID": id,
                                          "cookies": cookies, "device": context.device])
        guard bytes.count <= 32_768 else { throw MusicServiceError.limit }
        return try .init(data: bytes)
    }
    /// Display caches do not establish identity. Every restore and directory
    /// read uses this exact profile endpoint, with UID/account.id binding.
    func profile(_ context: MusicNeteaseContext) async throws -> String {
        try await displayProfile(context).nickname
    }
    func displayProfile(_ context: MusicNeteaseContext) async throws -> MusicAccountProfile {
        let own = epoch
        let root = try await request("/api/w/nuser/account/get", crypto: "weapi", context: context)
        try check(context, epoch: own)
        let data = root["data"] as? [String: Any] ?? root
        guard let raw = data["profile"] else { throw MusicServiceError.upstreamInvalid }
        if raw is NSNull { throw MusicServiceError.authRequired }
        let profile = try MusicQQWire.object(raw), id = try MusicNeteaseWire.id(profile["userId"])
        guard context.accountID.isEmpty || context.accountID == id else { throw MusicServiceError.targetChanged }
        if let account = data["account"], !(account is NSNull) {
            let record = try MusicQQWire.object(account)
            if let value = record["id"], try MusicNeteaseWire.id(value) != id { throw MusicServiceError.targetChanged }
        }
        let name = try MusicQQWire.string(profile["nickname"] ?? "", maximum: 512, empty: true)
        context.accountID = id
        let secrets = Array(context.cookies.values)
        return .init(nickname: try MusicAccountPublicText.profile(name, secrets: secrets),
                     avatarURL: try MusicAccountProfileWire.avatar(profile["avatarUrl"], platform: .netease, secrets: secrets))
    }
    func nativeID(_ credential: AccountCredential) throws -> String { try context(credential).accountID }
    func verifiedUpdate(_ context: MusicNeteaseContext) async throws -> MusicQQVerified {
        let own = epoch, originalCookies = context.cookies
        var nickname = try await profile(context)
        try check(own)
        if context.cookies != originalCookies {
            // Verify using the returned session, not just the old session that
            // received Set-Cookie. Never loop on continually rotating credentials.
            let expected = context.cookies
            nickname = try await profile(context)
            try check(own)
            guard context.cookies == expected else { throw MusicServiceError.verificationRequired }
        }
        return .init(credential: try credential(context), nickname: nickname, nativeID: context.accountID)
    }
    func verify(_ credential: AccountCredential) async throws -> MusicQQVerified {
        let context = try context(credential)
        context.permitsCredentialUpdate = true
        return try await verifiedUpdate(context)
    }
    func refresh(_ credential: AccountCredential) async throws -> MusicQQVerified {
        let own = epoch, context = try context(credential)
        context.permitsCredentialUpdate = true
        _ = try await request("/api/login/token/refresh", context: context)
        try check(own)
        return try await verifiedUpdate(context)
    }
    func bootstrap(_ context: MusicNeteaseContext) async throws {
        try ensureDevice(context)
        let id = context.device["deviceId"]!, key = Array("3go8&$8*3*3h0k(2)2".utf8)
        let xored = Data(id.utf8.enumerated().map { $0.element ^ key[$0.offset % key.count] })
        let hash = Data(Insecure.MD5.hash(data: xored)).base64EncodedString()
        let username = Data((id + " " + hash).utf8).base64EncodedString()
        _ = try await request("/api/register/anonimous", data: ["username": username], crypto: "xeapi", context: context)
        guard context.cookies["MUSIC_A"]?.isEmpty == false, context.cookies["MUSIC_U"] == nil else {
            throw MusicServiceError.upstreamInvalid
        }
        context.cookies.removeValue(forKey: "__csrf")
    }
    static func qrImage(_ key: String) throws -> Data {
        let url = try MusicQQWire.query("https://music.163.com/login", ["codekey": key])
        guard let filter = CIFilter(name: "CIQRCodeGenerator") else { throw MusicServiceError.unavailable }
        filter.setValue(Data(url.absoluteString.utf8), forKey: "inputMessage")
        filter.setValue("M", forKey: "inputCorrectionLevel")
        guard let image = filter.outputImage, let color = CGColorSpace(name: CGColorSpace.sRGB) else {
            throw MusicServiceError.unavailable
        }
        let background = CIImage(color: CIColor(red: 1, green: 1, blue: 1))
            .cropped(to: image.extent.insetBy(dx: -4, dy: -4))
        let padded = image.composited(over: background).transformed(by: CGAffineTransform(scaleX: 6, y: 6))
        guard let bytes = CIContext(options: [.useSoftwareRenderer: true])
            .pngRepresentation(of: padded, format: .RGBA8, colorSpace: color),
              bytes.count <= 1024 * 1024 else { throw MusicServiceError.unavailable }
        return bytes
    }
    func createQR(id: UUID) async throws -> MusicLoginChallenge {
        try check(epoch)
        qr = qr.filter { $0.value.expires > now() }
        guard qr[id] == nil else { throw MusicServiceError.busy }
        let context = MusicNeteaseContext(), expires = now().addingTimeInterval(180)
        context.qrID = id; qr[id] = .init(expires: expires, context: context)
        do {
            try await bootstrap(context)
            let root = try await request("/api/login/qrcode/unikey", data: ["type": 3], context: context)
            let key = try MusicQQWire.string(root["unikey"] ?? (root["data"] as? [String: Any])?["unikey"], maximum: 512)
            _ = try currentQR(id)
            let image = try Self.qrImage(key)
            qr[id]?.key = key
            return .init(id: id, platform: .netease, image: image, expiresAt: expires)
        } catch { qr.removeValue(forKey: id); throw error }
    }
    func pollQR(id: UUID) async throws -> MusicQQLoginResult {
        let current = try currentQR(id), own = epoch
        guard let key = current.key else { throw MusicServiceError.busy }
        if let last = current.lastPoll, now().timeIntervalSince(last) < 3 { throw MusicServiceError.rateLimited }
        qr[id]?.lastPoll = now()
        let context = current.context
        let root = try await request("/api/login/qrcode/client/login", data: ["key": key, "type": 3], context: context, qr: true)
        try check(context, epoch: own)
        switch try MusicQQWire.integer(root["code"]) {
        case 800:
            qr.removeValue(forKey: id); return .init(progress: .expired, verified: nil)
        case 801: return .init(progress: .waiting, verified: nil)
        case 802: return .init(progress: .scanned, verified: nil)
        case 803:
            guard context.cookies["MUSIC_U"]?.isEmpty == false else { throw MusicServiceError.authRequired }
            // A supplied QR profile may constrain identity but never replaces
            // a fresh authenticated profile verification.
            if let raw = root["profile"], !(raw is NSNull) {
                context.accountID = try MusicNeteaseWire.id(MusicQQWire.object(raw)["userId"])
            }
            let nickname = try await profile(context)
            try check(context, epoch: own)
            if let account = root["account"], !(account is NSNull),
               let value = try MusicQQWire.object(account)["id"],
               try MusicNeteaseWire.id(value) != context.accountID { throw MusicServiceError.targetChanged }
            let verified = MusicQQVerified(credential: try credential(context), nickname: nickname, nativeID: context.accountID)
            qr.removeValue(forKey: id)
            return .init(progress: .confirmed, verified: verified)
        default: throw MusicServiceError.upstreamInvalid
        }
    }
}
