// SPDX-License-Identifier: GPL-3.0-only
// Fixed-source port: meowmusic cc22a7e platform/qq/account.go.
import Foundation
import MeowAccounts
import MeowMusicFeature

struct MusicQQVerified: Sendable {
    let credential: AccountCredential
    let nickname: String
    let nativeID: String
}
struct MusicQQLoginResult: Sendable {
    let progress: MusicLoginProgress
    let verified: MusicQQVerified?
}
extension QQMusicAccountAdapter {
    func fileCredential(_ credential: AccountCredential) async throws -> AccountCredential {
        let normalized = try auth(credential)
        let original = try MusicQQWire.object(MusicQQWire.decode(credential.dataRepresentation())["auth"])
        if let alias = original["str_musicid"] {
            guard try MusicQQWire.id(alias) == MusicQQWire.id(normalized["musicid"]) else {
                throw MusicServiceError.targetChanged
            }
        }
        let aliases = ["musickeyCreateTime": "musickey_create_time", "keyExpiresIn": "key_expires_in",
                       "loginType": "login_type", "encryptUin": "encrypt_uin"]
        let present = Set(original.keys.map { aliases[$0] ?? $0 })
        return try AccountCredential(data: MusicQQWire.json([
            "version": 1, "platform": "tx", "auth": normalized.filter { present.contains($0.key) }
        ]))
    }
    func auth(_ credential: AccountCredential) throws -> [String: Any] {
        let value = try MusicQQWire.decode(credential.dataRepresentation())
        guard try MusicQQWire.integer(value["version"]) == 1, value["platform"] as? String == "tx",
              Set(value.keys) == ["version", "platform", "auth"] else { throw MusicServiceError.invalidResponse }
        return try sanitizeAuth(MusicQQWire.object(value["auth"]))
    }
    func sanitizeAuth(_ raw: [String: Any]) throws -> [String: Any] {
        let uid = try MusicQQWire.id(raw["musicid"] ?? raw["str_musicid"])
        var result: [String: Any] = ["musicid": Int(uid)!, "str_musicid": uid]
        for key in ["musickey", "openid", "refresh_token", "access_token", "unionid", "refresh_key"] {
            let text = try MusicQQWire.string(raw[key] ?? "", maximum: 8192, empty: key != "musickey")
            guard text.utf8.allSatisfy({ $0 >= 33 && $0 <= 126 && ![59, 44, 34, 92].contains($0) }) else {
                throw MusicServiceError.upstreamInvalid
            }
            result[key] = text
        }
        for (key, alias) in [("expired_at", "expired_at"), ("musickey_create_time", "musickeyCreateTime"),
                             ("key_expires_in", "keyExpiresIn"), ("login_type", "loginType")] {
            if let value = raw[key] ?? raw[alias] {
                if let string = value as? String, string.range(of: "^(0|[1-9][0-9]{0,15})$", options: .regularExpression) != nil,
                   let number = Int(string), number <= MusicPlatformJSON.maximumInteger { result[key] = number }
                else { result[key] = try MusicQQWire.integer(value) }
            }
        }
        if result["login_type"] == nil { result["login_type"] = (result["musickey"] as? String ?? "").hasPrefix("W_X") ? 1 : 2 }
        if let value = raw["encrypt_uin"] ?? raw["encryptUin"] {
            let text = try MusicQQWire.string(value, maximum: 256)
            guard text.range(of: "^[a-zA-Z0-9=_-]{1,256}$", options: .regularExpression) != nil else { throw MusicServiceError.upstreamInvalid }
            result["encrypt_uin"] = text
        }
        return result
    }
    func verified(_ input: [String: Any]) async throws -> MusicQQVerified {
        var auth = try sanitizeAuth(input)
        let uid = try MusicQQWire.id(auth["musicid"])
        let check = try await rpc("music.UserInfo.userInfoServer", "GetLoginUserInfo", auth: auth)
        for row in [check, check["userInfo"] as? [String: Any] ?? [:], check["data"] as? [String: Any] ?? [:]] {
            for key in ["musicid", "str_musicid"] {
                if let value = row[key], try MusicQQWire.id(value) != uid { throw MusicServiceError.targetChanged }
            }
        }
        let nickname: String
        if let euin = auth["encrypt_uin"] as? String {
            let data = try await rpc("music.UnifiedHomepage.UnifiedHomepageSrv", "GetHomepageHeader",
                                     ["uin": euin, "IsQueryTabDetail": 1], auth: auth)
            let base = try MusicQQWire.object(MusicQQWire.object(data["Info"])["BaseInfo"])
            guard base["EncryptedUin"] as? String == euin else { throw MusicServiceError.targetChanged }
            nickname = try MusicQQWire.string(base["Name"] ?? "", maximum: 512, empty: true)
        } else {
            let key = try MusicQQWire.string(auth["musickey"], maximum: 8192)
            let url = try MusicQQWire.query("https://c6.y.qq.com/rsc/fcgi-bin/fcg_get_profile_homepage.fcg", [
                "g_tk": String(MusicQQWire.hash33(key, seed: 5381)), "format": "json", "inCharset": "utf-8",
                "outCharset": "utf-8", "notice": "0", "cid": "205360838", "needNewCode": "0",
                "loginUin": uid, "hostUin": "0", "userid": uid, "reqfrom": "1"
            ])
            let response = try await request(url, cookies: ["uin": uid, "qqmusic_uin": uid, "qm_keyst": key, "qqmusic_key": key])
            let root = try MusicQQWire.decode(response.data); try MusicQQWire.codes(root, requireCode: true)
            let creator = try MusicQQWire.object(MusicQQWire.object(root["data"])["creator"])
            guard try MusicQQWire.id(creator["uin"] ?? creator["musicid"]) == uid else { throw MusicServiceError.targetChanged }
            auth["encrypt_uin"] = creator["encrypt_uin"] ?? creator["encryptUin"]
            auth = try sanitizeAuth(auth)
            guard auth["encrypt_uin"] != nil else { throw MusicServiceError.upstreamInvalid }
            nickname = try MusicQQWire.string(creator["nick"] ?? creator["nickname"] ?? "", maximum: 512, empty: true)
        }
        // Additional profile identity verification is deliberate; the reference
        // QR path only calls checkLogin. Enrichment is saved before first accept.
        let credential = try AccountCredential(data: MusicQQWire.json(["version": 1, "platform": "tx", "auth": auth]))
        let secrets = ["musickey", "refresh_token", "refresh_key", "access_token", "openid", "unionid"]
            .compactMap { auth[$0] as? String }
        return MusicQQVerified(credential: credential, nickname: try MusicAccountPublicText.profile(nickname, secrets: secrets), nativeID: uid)
    }
    func nativeID(_ credential: AccountCredential) throws -> String { try MusicQQWire.id(auth(credential)["musicid"]) }
    func verify(_ credential: AccountCredential) async throws -> MusicQQVerified { try await verified(auth(credential)) }
    func refresh(_ credential: AccountCredential) async throws -> MusicQQVerified {
        let original = try auth(credential), uid = try MusicQQWire.id(original["musicid"]), own = deviceGeneration
        guard (original["refresh_token"] as? String)?.isEmpty == false ||
              (original["refresh_key"] as? String)?.isEmpty == false else { throw MusicServiceError.verificationRequired }
        let device = try await ensureDevice()
        guard own == deviceGeneration else { throw MusicServiceError.staleResult }
        var params: [String: Any] = ["loginMode": 2]
        for key in ["openid", "access_token", "refresh_token", "str_musicid", "musicid", "musickey", "unionid", "refresh_key"] {
            params[key] = original[key]
        }
        params["expired_in"] = original["expired_at"] ?? 0
        // signed=false in the reference selects musicu.fcg; it does not strip
        // this operation's original account cookies. Device GetSession stays anonymous.
        let raw = try await rpcWith("music.login.LoginServer", "Login", params, comm: comm(device, auth: original), auth: original)
        try Task.checkCancellation()
        guard own == deviceGeneration else { throw MusicServiceError.staleResult }
        var next = try sanitizeAuth(raw)
        guard try MusicQQWire.id(next["musicid"]) == uid else { throw MusicServiceError.targetChanged }
        if next["encrypt_uin"] == nil { next["encrypt_uin"] = original["encrypt_uin"] }
        let result = try await verified(next)
        guard own == deviceGeneration else { throw MusicServiceError.staleResult }
        return result
    }
    func createQR(id: UUID) async throws -> MusicLoginChallenge {
        let expires = now().addingTimeInterval(180)
        qr[id] = QR(expires: expires, signature: "")
        do {
            let url = try MusicQQWire.query("https://ssl.ptlogin2.qq.com/ptqrshow", [
                "appid": "716027609", "e": "2", "l": "M", "s": "3", "d": "72", "v": "4",
                "t": String(Int(now().timeIntervalSince1970 * 1000)), "daid": "383", "pt_3rd_aid": "100497308"
            ])
            let response = try await request(url, headers: ["Referer": "https://xui.ptlogin2.qq.com/"], maximumBytes: 1024 * 1024)
            let cookies = try MusicQQWire.cookies(response), data = response.data
            guard let signature = cookies["qrsig"], !signature.isEmpty,
                  data.starts(with: [137, 80, 78, 71, 13, 10, 26, 10]) || data.starts(with: [255, 216, 255]) else {
                throw MusicServiceError.upstreamInvalid
            }
            _ = try checkQRCurrent(id); qr[id]?.signature = signature
            return MusicLoginChallenge(id: id, platform: .qq, image: data, expiresAt: expires)
        } catch { qr.removeValue(forKey: id); throw error }
    }
    func pollQR(id: UUID) async throws -> MusicQQLoginResult {
        let current = try checkQRCurrent(id)
        guard !current.signature.isEmpty else { throw MusicServiceError.busy }
        let url = try MusicQQWire.query("https://ssl.ptlogin2.qq.com/ptqrlogin", [
            "u1": "https://graph.qq.com/oauth2.0/login_jump", "ptqrtoken": String(MusicQQWire.hash33(current.signature, seed: 0)),
            "ptredirect": "0", "h": "1", "t": "1", "g": "1", "from_ui": "1", "ptlang": "2052",
            "action": "0-0-\(Int(now().timeIntervalSince1970 * 1000))", "js_ver": "20102616", "js_type": "1",
            "pt_uistyle": "40", "aid": "716027609", "daid": "383", "pt_3rd_aid": "100497308", "has_onekey": "1"
        ])
        let response = try await request(url, headers: ["Referer": "https://xui.ptlogin2.qq.com/"],
                                         cookies: ["qrsig": current.signature], maximumBytes: 16_384)
        _ = try checkQRCurrent(id)
        let args = try MusicQQWire.callback(response.data)
        switch args[0] {
        case "66": return MusicQQLoginResult(progress: .waiting, verified: nil)
        case "67": return MusicQQLoginResult(progress: .scanned, verified: nil)
        case "65", "68": qr.removeValue(forKey: id); return MusicQQLoginResult(progress: .expired, verified: nil)
        case "0": break
        default: throw MusicServiceError.upstreamInvalid
        }
        guard let redirect = URLComponents(string: args[2]), redirect.scheme == "https",
              redirect.host == "ssl.ptlogin2.graph.qq.com", redirect.user == nil, redirect.password == nil,
              redirect.port == nil else { throw MusicServiceError.upstreamInvalid }
        let items = redirect.queryItems ?? []
        guard items.filter({ $0.name == "uin" }).count == 1, items.filter({ $0.name == "ptsigx" }).count == 1 else {
            throw MusicServiceError.upstreamInvalid
        }
        let uid = try MusicQQWire.id(items.first { $0.name == "uin" }?.value)
        let sig = try MusicQQWire.string(items.first { $0.name == "ptsigx" }?.value, maximum: 4096)
        let target = try MusicQQWire.query("https://ssl.ptlogin2.graph.qq.com/check_sig", [
            "uin": uid, "pttype": "1", "service": "ptqrlogin", "nodirect": "0", "ptsigx": sig,
            "s_url": "https://graph.qq.com/oauth2.0/login_jump", "ptlang": "2052", "ptredirect": "100",
            "aid": "716027609", "daid": "383", "j_later": "0", "low_login_hour": "0", "regmaster": "0",
            "pt_login_type": "3", "pt_aid": "0", "pt_aaid": "16", "pt_light": "0", "pt_3rd_aid": "100497308"
        ])
        let signed = try await request(target, headers: ["Referer": "https://xui.ptlogin2.qq.com/"], redirects: true)
        _ = try checkQRCurrent(id)
        let returnedCookies = try MusicQQWire.cookies(signed)
        // The fixed OAuth hop uses QQ login cookies only; do not forward an
        // arbitrary Set-Cookie name or a previous account's cookie jar.
        let cookies = returnedCookies.filter { ["p_skey", "p_uin", "pt4_token", "pt2gguin", "uin", "skey"].contains($0.key) }
        let pkey = try MusicQQWire.string(cookies["p_skey"], maximum: 8192)
        let form = [
            "response_type": "code", "client_id": "100497308",
            "redirect_uri": "https://y.qq.com/portal/wx_redirect.html?login_type=1&surl=https://y.qq.com/",
            "scope": "get_user_info,get_app_friends", "state": "state", "switch": "", "from_ptlogin": "1",
            "src": "1", "update_auth": "1", "openapi": "1010_1030", "g_tk": String(MusicQQWire.hash33(pkey, seed: 5381)),
            "auth_time": String(Int(now().timeIntervalSince1970 * 1000)), "ui": try MusicQQWire.randomHex(16)
        ]
        let oauth = try await request(URL(string: "https://graph.qq.com/oauth2.0/authorize")!, method: "POST",
                                     headers: ["Content-Type": "application/x-www-form-urlencoded"],
                                     cookies: cookies, body: MusicQQWire.form(form), redirects: true)
        _ = try checkQRCurrent(id)
        guard let location = oauth.header("location"), let components = URLComponents(string: location),
              components.scheme == "https", components.host == "y.qq.com",
              components.user == nil, components.password == nil, components.port == nil,
              components.queryItems?.filter({ $0.name == "code" }).count == 1 else { throw MusicServiceError.upstreamInvalid }
        let code = try MusicQQWire.string(components.queryItems?.first { $0.name == "code" }?.value, maximum: 4096)
        let device = try await ensureDevice()
        var anonymous = comm(device); anonymous["tmeLoginType"] = "2"
        let raw = try await rpcWith("QQConnectLogin.LoginServer", "QQLogin", ["code": code], comm: anonymous)
        _ = try checkQRCurrent(id)
        let result = try await verified(raw)
        _ = try checkQRCurrent(id); qr.removeValue(forKey: id)
        return MusicQQLoginResult(progress: .confirmed, verified: result)
    }
}
