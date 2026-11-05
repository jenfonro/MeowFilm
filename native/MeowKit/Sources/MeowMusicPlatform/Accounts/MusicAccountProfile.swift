// Display mapping: songloft-plugin-iwebplus b900c259 src/{netease,kugou,qq}.ts.
// Separate from media rights: unknown/failure must never become non-membership.
import Foundation
import MeowAccounts
import MeowMusicFeature

protocol MusicAccountProfileProvider: MusicAccountProvider {
    func accountProfile(_ credential: AccountCredential) async throws -> MusicAccountProfileSnapshot
}
/// Private cache deadline, not a UI field or an authorization. In particular a
/// future product start may invalidate the display before its selected expiry.
struct MusicAccountProfileSnapshot: Sendable {
    let profile: MusicAccountProfile
    let recheckAt: Date
}

enum MusicAccountProfileWire {
    static func avatar(_ raw: Any?, platform: MusicAccountPlatform, secrets: [String]) throws -> URL? {
        guard let raw, !(raw is NSNull), (raw as? String) != "" else { return nil }
        var text = try MusicAccountPublicText.profile(MusicQQWire.string(raw, maximum: 4096), secrets: secrets)
        if platform == .kugou { text = text.replacingOccurrences(of: "{size}", with: "480") }
        return MusicArtworkURL.normalized(text)
    }
    static func expiry(_ raw: Any?) -> Date? {
        guard let raw, !(raw is NSNull) else { return nil }
        if let n = try? MusicKugouWire.integer(raw), n > 0 {
            return Date(timeIntervalSince1970: Double(n) / (n < 1_000_000_000_000 ? 1 : 1000))
        }
        guard let text = raw as? String else { return nil }
        let iso = ISO8601DateFormatter()
        if let date = iso.date(from: text), date.timeIntervalSince1970 > 0 { return date }
        iso.formatOptions.insert(.withFractionalSeconds)
        if let date = iso.date(from: text), date.timeIntervalSince1970 > 0 { return date }
        let parser = DateFormatter()
        parser.locale = Locale(identifier: "en_US_POSIX"); parser.timeZone = TimeZone(secondsFromGMT: 8 * 3600)
        parser.isLenient = false
        for format in ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd"] {
            parser.dateFormat = format
            if let date = parser.date(from: text), parser.string(from: date) == text,
               date.timeIntervalSince1970 > 0 { return date }
        }
        return nil
    }
    static func neteaseMembership(_ data: [String: Any], at: Date) -> MusicAccountMembership {
        let local = Int(at.timeIntervalSince1970 * 1000)
        let time = (try? MusicQQWire.integer(data["now"])) ?? local
        let timeMS = time == 0 ? local : time
        var values: [String: Int] = [:]
        for key in ["associator", "musicPackage", "redplus"] {
            if let row = data[key] as? [String: Any], let n = try? MusicQQWire.integer(row["expireTime"]) { values[key] = n }
        }
        let vip = max(values["associator"] ?? 0, values["musicPackage"] ?? 0)
        let svip = values["redplus"] ?? 0
        let active = max(vip, svip) > timeMS
        let end = active ? (svip > timeMS ? svip : vip) : values.values.filter { $0 > 0 && $0 <= timeMS }.max()
        return .init(vip: active ? true : values.isEmpty ? nil : false,
                     tier: active ? (svip > timeMS ? "svip" : "vip") : "",
                     expiresAt: end.map { Date(timeIntervalSince1970: Double($0) / 1000) })
    }
    static func neteaseDeadline(_ data: [String: Any], value: MusicAccountMembership, at: Date) -> Date {
        guard let vip = value.vip else { return at }
        guard vip, let end = value.expiresAt else { return at.addingTimeInterval(86_400) }
        let upstreamMS = (try? MusicQQWire.integer(data["now"])) ?? 0
        let upstream = upstreamMS > 0 ? Date(timeIntervalSince1970: Double(upstreamMS) / 1000) : at
        // Absolute time is for display; cache lifetime follows upstream time.
        return at.addingTimeInterval(max(0, end.timeIntervalSince(upstream)))
    }
    static func products(_ rows: [(String, Any?, Any?, Any?)], at: Date) -> MusicAccountMembership {
        var known = false, uncertain = false, expired: Date?
        for (tier, flag, end, start) in rows {
            guard let flag, !(flag is NSNull) else { continue }
            // Reference requires numeric 0/1, not arbitrary truthy/string flags.
            guard let n = try? MusicQQWire.integer(flag, maximum: 1) else { uncertain = true; continue }
            known = true
            let ends = expiry(end), begins = expiry(start)
            if let ends, ends <= at { expired = max(expired ?? ends, ends) }
            if n == 1, begins == nil || begins! <= at, ends == nil || ends! > at {
                return .init(vip: true, tier: tier, expiresAt: ends)
            }
        }
        return .init(vip: known && !uncertain ? false : nil, expiresAt: expired)
    }
    static func kugouMembership(_ data: [String: Any], at: Date) -> MusicAccountMembership {
        var rows: [(String, Any?, Any?, Any?)] = []
        for raw in data["busi_vip"] as? [Any] ?? [] {
            guard let row = raw as? [String: Any],
                  row["busi_type"] == nil || row["busi_type"] is NSNull || row["busi_type"] as? String == "concept",
                  let product = row["product_type"] as? String,
                  ["svip", "tvip", "wvip", "qvip", "dvip", "evip"].contains(product),
                  (try? MusicQQWire.integer(row["is_vip"], maximum: 1)) != nil else {
                rows.append(("", "unknown", nil, nil)); continue
            }
            rows.append(("concept:" + product, row["is_vip"], row["vip_end_time"], row["vip_begin_time"]))
        }
        rows.append(("vip", data["is_vip"], data["vip_end_time"], data["vip_begin_time"]))
        let result = products(rows, at: at)
        return result.vip == nil ? .init() : result
    }
    private static func qqProducts(_ data: [String: Any]) -> [(String, Any?, Any?, Any?)] {
        let i = data["identity"] as? [String: Any] ?? [:], summary = (data["userinfo"] as? [String: Any])?["expire"]
        return [
            ("svip", data["svip"], summary, nil),
            ("huge_vip", i["HugeVip"] ?? i["huge_vip"], i["HugeVipEnd"] ?? i["huge_vip_end"], i["HugeVipStart"] ?? i["huge_vip_start"]),
            ("twelve", i["twelve"], i["twelveEnd"] ?? i["twelve_end"], i["twelveStart"] ?? i["twelve_start"]),
            ("eight", i["eight"], i["eightEnd"] ?? i["eight_end"], i["eightStart"] ?? i["eight_start"]),
            ("group_vip", i["GroupVipFlag"] ?? i["group_vip_flag"], i["GroupVipEnd"] ?? i["group_vip_end"], i["GroupVipStart"] ?? i["group_vip_start"]),
            ("cp_lover", i["CPLoverFlag"] ?? i["cp_lover_flag"], i["CPLoverEnd"] ?? i["cp_lover_end"], i["CPLoverStart"] ?? i["cp_lover_start"]),
            ("star", data["star"], data["starend"] ?? data["star_end"], data["starstart"] ?? data["star_start"]),
            ("ystar", data["ystar"], data["ystarend"] ?? data["ystar_end"], data["ystarstart"] ?? data["ystar_start"]),
            ("vip", i["vip"], summary, nil), ("child_vip", i["ChildVip"] ?? i["child_vip"], nil, nil),
            ("exp_vip", i["ExpVip"] ?? i["exp_vip"], nil, nil), ("ad_vip", i["AdVipFlag"] ?? i["ad_vip_flag"], nil, nil)
        ]
    }
    static func qqMembership(_ data: [String: Any], at: Date) -> MusicAccountMembership {
        products(qqProducts(data), at: at)
    }
    static func productDeadline(_ data: [String: Any], platform: MusicAccountPlatform,
                                value: MusicAccountMembership, at: Date) -> Date {
        guard let vip = value.vip else { return at }
        var deadline = vip ? value.expiresAt ?? (platform == .qq ? at : at.addingTimeInterval(60)) : at.addingTimeInterval(86_400)
        let boundaries: [Date]
        if platform == .qq {
            boundaries = qqProducts(data).filter { (try? MusicQQWire.integer($0.1, maximum: 1)) == 1 }
                .flatMap { [expiry($0.2), expiry($0.3)].compactMap { $0 } }
        } else {
            let rows = [data] + (data["busi_vip"] as? [Any] ?? []).compactMap { $0 as? [String: Any] }
            boundaries = rows.filter { (try? MusicQQWire.integer($0["is_vip"], maximum: 1)) != nil }
                .flatMap { [expiry($0["vip_end_time"]), expiry($0["vip_begin_time"])].compactMap { $0 } }
        }
        for boundary in boundaries where boundary > at { deadline = min(deadline, boundary) }
        return deadline
    }
    static func membershipFailure(_ error: Error) throws -> MusicAccountMembership {
        if error is CancellationError { throw error }
        guard let failure = error as? MusicServiceError,
              [.unavailable, .timeout, .upstreamFailed, .upstreamInvalid, .rateLimited].contains(failure) else { throw error }
        return .init()
    }
}

extension NeteaseMusicAccountAdapter: MusicAccountProfileProvider {
    func accountProfile(_ credential: AccountCredential) async throws -> MusicAccountProfileSnapshot {
        let context = try context(credential), own = epoch
        let profile = try await displayProfile(context)
        var membership: MusicAccountMembership, deadline = now()
        do {
            let root = try await request("/api/music-vip-membership/client/vip/info", data: ["userId": ""],
                                         crypto: "weapi", context: context)
            let data = root["data"] as? [String: Any] ?? root
            guard try MusicNeteaseWire.id(data["userId"]) == context.accountID else { throw MusicServiceError.targetChanged }
            membership = MusicAccountProfileWire.neteaseMembership(data, at: now())
            deadline = MusicAccountProfileWire.neteaseDeadline(data, value: membership, at: now())
        } catch { membership = try MusicAccountProfileWire.membershipFailure(error) }
        try check(context, epoch: own)
        return .init(profile: .init(nickname: profile.nickname, avatarURL: profile.avatarURL, membership: membership),
                     recheckAt: deadline)
    }
}
extension KugouMusicAccountAdapter: MusicAccountProfileProvider {
    func accountProfile(_ credential: AccountCredential) async throws -> MusicAccountProfileSnapshot {
        let context = try context(credential)
        let profile = try await displayProfile(context)
        var membership: MusicAccountMembership, deadline = now()
        do {
            let root = try await request(.membership, context: context, query: ["busi_type": "concept"])
            let data = try MusicQQWire.object(root["data"])
            if let id = data["userid"], !(id is NSNull),
               try MusicKugouWire.id(id) != context.auth!.uid { throw MusicServiceError.targetChanged }
            for raw in data["busi_vip"] as? [Any] ?? [] {
                if let row = raw as? [String: Any], let id = row["userid"], !(id is NSNull),
                   try MusicKugouWire.id(id) != context.auth!.uid { throw MusicServiceError.targetChanged }
            }
            membership = MusicAccountProfileWire.kugouMembership(data, at: now())
            deadline = MusicAccountProfileWire.productDeadline(data, platform: .kugou, value: membership, at: now())
        } catch { membership = try MusicAccountProfileWire.membershipFailure(error) }
        try check(context)
        return .init(profile: .init(nickname: profile.nickname, avatarURL: profile.avatarURL, membership: membership),
                     recheckAt: deadline)
    }
}
extension QQMusicAccountAdapter: MusicAccountProfileProvider {
    func accountProfile(_ credential: AccountCredential) async throws -> MusicAccountProfileSnapshot {
        let auth = try auth(credential), own = deviceGeneration
        guard let euin = auth["encrypt_uin"] as? String else { throw MusicServiceError.verificationRequired }
        let data = try await rpc("music.UnifiedHomepage.UnifiedHomepageSrv", "GetHomepageHeader",
                                 ["uin": euin, "IsQueryTabDetail": 1], auth: auth)
        let base = try MusicQQWire.object(MusicQQWire.object(data["Info"])["BaseInfo"])
        guard base["EncryptedUin"] as? String == euin else { throw MusicServiceError.targetChanged }
        let secrets = ["musickey", "refresh_token", "refresh_key", "access_token", "openid", "unionid"].compactMap { auth[$0] as? String }
        let nickname = try MusicAccountPublicText.profile(MusicQQWire.string(base["Name"] ?? "", maximum: 512, empty: true), secrets: secrets)
        let avatar = try MusicAccountProfileWire.avatar(base["Avatar"], platform: .qq, secrets: secrets)
        var membership: MusicAccountMembership, deadline = now()
        do {
            let data = try await rpc("VipLogin.VipLoginInter", "vip_login_base", auth: auth)
            membership = MusicAccountProfileWire.qqMembership(data, at: now())
            deadline = MusicAccountProfileWire.productDeadline(data, platform: .qq, value: membership, at: now())
        } catch { membership = try MusicAccountProfileWire.membershipFailure(error) }
        try Task.checkCancellation()
        guard own == deviceGeneration else { throw MusicServiceError.staleResult }
        return .init(profile: .init(nickname: nickname, avatarURL: avatar, membership: membership), recheckAt: deadline)
    }
}
