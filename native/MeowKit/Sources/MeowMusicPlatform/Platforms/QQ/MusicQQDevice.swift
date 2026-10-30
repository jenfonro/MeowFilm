// SPDX-License-Identifier: GPL-3.0-only
// meowmusic cc22a7e platform/qq/device.go. These are protocol profile fields,
// not measurements of the user's iPhone or a claim of Android hardware.
import Foundation
import CryptoKit
import MeowMusicFeature

protocol MusicQQClientDeviceProviding: Sendable {
    func clientDevice() async throws -> QQMusicAccountAdapter.DeviceValue
}

extension NativeMusicQQPublicService: MusicQQClientDeviceProviding {
    func clientDevice() async throws -> QQMusicAccountAdapter.DeviceValue {
        guard !closedForClient else { throw MusicServiceError.closed }
        let token = try persistence.capture()
        let fields = try await qq.ensureDevice(publicAccess: token)
        try Task.checkCancellation(); try persistence.check(token)
        guard !closedForClient else { throw MusicServiceError.closed }
        return .init(fields: fields)
    }
}

extension QQMusicAccountAdapter {
    func chinaDay(_ date: Date) -> String {
        MusicQQPublicPersistenceCodec.chinaDay(date)
    }
    func qimeiIsCurrent(_ value: [String: Any], at date: Date) -> Bool {
        guard let q = value["qimei"] as? [String: Any], let saved = q["savedAt"] as? Date,
              let q16 = q["q16"] as? String, !q16.isEmpty,
              let q36 = q["q36"] as? String, !q36.isEmpty else { return false }
        return saved <= date && date.timeIntervalSince(saved) < 86_400
    }
    func sessionIsCurrent(_ value: [String: Any], at date: Date) -> Bool {
        guard let session = value["session"] as? [String: Any],
              let uid = session["uid"] as? String, !uid.isEmpty,
              let sid = session["sid"] as? String, !sid.isEmpty,
              session["day"] as? String == chinaDay(date) else { return false }
        // Account adapters may still hold the pre-persistence in-memory shape.
        if let saved = session["savedAt"] as? Date { return saved <= date }
        return publicPersistence == nil
    }
    func ensureDevice(publicAccess: UUID? = nil) async throws -> [String: Any] {
        try Task.checkCancellation()
        try checkPublicAccess(publicAccess)
        if let clientDeviceProvider {
            let generation = deviceGeneration
            let result = try await clientDeviceProvider.clientDevice()
            try Task.checkCancellation()
            guard generation == deviceGeneration else { throw MusicServiceError.staleResult }
            return result.fields
        }
        if let publicPersistence, let publicAccess, publicDeviceEpoch != publicAccess {
            revoke()
            let restored = try publicPersistence.read(publicAccess)
            let candidate = try restored ?? makeDevice()
            // Durable identity before any platform request, including when
            // the very first QIMEI or GetSession attempt subsequently fails.
            // Never mark this epoch loaded if cancellation prevented the seed
            // commit. A retry must re-read the durable baseline first.
            if restored == nil { try publicPersistence.save(candidate, token: publicAccess) }
            try publicPersistence.check(publicAccess)
            device = candidate
            publicDeviceEpoch = publicAccess
        }
        let at = now(), generation = deviceGeneration
        if qimeiIsCurrent(device, at: at), sessionIsCurrent(device, at: at) { return device }
        let previous = device
        let result = try await deviceRequests.value(for: generation) { [self] in
            try await loadAndPublishDevice(previous, generation: generation, publicAccess: publicAccess)
        }
        try Task.checkCancellation(); try checkPublicAccess(publicAccess)
        guard generation == deviceGeneration else { throw MusicServiceError.staleResult }
        return result.fields
    }
    private func loadAndPublishDevice(_ previous: [String: Any], generation: UUID,
                                      publicAccess: UUID?) async throws -> DeviceValue {
        let value = try await loadDevice(previous, publicAccess: publicAccess)
        try Task.checkCancellation(); try checkPublicAccess(publicAccess)
        guard generation == deviceGeneration else { throw MusicServiceError.staleResult }
        device = value
        return DeviceValue(fields: value)
    }
    func makeDevice() throws -> [String: Any] {
        let raw = try MusicQQWire.randomHex(14)
        var imei = "", sum = 0
        for (i, byte) in raw.utf8.prefix(14).enumerated() {
            let digit = Int(byte) % 10, doubled = i % 2 == 1 ? digit * 2 : digit
            imei += String(digit); sum += doubled > 9 ? doubled - 9 : doubled
        }
        imei += String((10 - sum % 10) % 10)
        return [
            "android_id": try MusicQQWire.randomHex(8), "open_udid": try MusicQQWire.randomHex(16),
            "open_udid2": try MusicQQWire.randomHex(16), "imei": imei,
            "brand": "vivo", "manufacturer": "vivo", "model": "V2408A", "board": "sun", "product": "PD2408",
            "device": "PD2408", "host": "comdg01150014", "os_ver": "15", "sdk": 35, "first_api_level": 35,
            "kernel": "Linux localhost 6.6.89-android15-8-g1f71897ac249-abogki467805059-4k #1 SMP PREEMPT Thu Dec 11 01:56:00 UTC 2025 aarch64"
        ]
    }
    private func publishPublicDevice(_ value: [String: Any], publicAccess: UUID?) throws {
        try Task.checkCancellation()
        try checkPublicAccess(publicAccess)
        if let publicPersistence, let publicAccess {
            guard publicDeviceEpoch == publicAccess else { throw MusicServiceError.staleAccess }
            try publicPersistence.save(value, token: publicAccess)
            device = value
        }
    }
    func loadDevice(_ previous: [String: Any], publicAccess: UUID? = nil) async throws -> [String: Any] {
        try checkPublicAccess(publicAccess)
        let preparedAt = now()
        var value = try previous.isEmpty ? makeDevice() : previous
        if !qimeiIsCurrent(value, at: preparedAt) {
            var refreshed = try await qimei(value, publicAccess: publicAccess)
            // Reference ensureDevice stamps at refresh start. A delayed reply
            // must not extend the anonymous cache's 24-hour lifetime.
            if publicAccess != nil { refreshed["savedAt"] = preparedAt }
            value["qimei"] = refreshed
            try publishPublicDevice(value, publicAccess: publicAccess)
        }
        let old = value["session"] as? [String: Any] ?? [:]
        if !sessionIsCurrent(value, at: publicAccess == nil ? now() : preparedAt) {
            let uid = old["uid"] as? String ?? ""
            // GetSession is always anonymous, even during a replacement login.
            let data = try await rpcWith("music.getSession.session", "GetSession",
                                         ["uid": uid, "vkey": 0, "caller": uid.isEmpty ? 2 : 1],
                                         comm: comm(value, session: false), publicAccess: publicAccess)
            let session = try MusicQQWire.object(data["session"])
            let sessionUID = try (session["uid"] as? String) ?? MusicQQWire.id(session["uid"])
            let saved = publicAccess == nil ? now() : preparedAt
            value["session"] = ["uid": try MusicQQWire.string(sessionUID, maximum: 256),
                                "sid": try MusicQQWire.string(session["sid"], maximum: 1024),
                                "day": chinaDay(saved), "savedAt": saved]
            try publishPublicDevice(value, publicAccess: publicAccess)
        }
        try Task.checkCancellation()
        try checkPublicAccess(publicAccess)
        return value
    }
    func qimei(_ device: [String: Any], publicAccess: UUID? = nil) async throws -> [String: Any] {
        let date = now(), formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX"); formatter.timeZone = TimeZone(secondsFromGMT: 0)
        formatter.dateFormat = "yyyy-MM-dd HH:mm:ss"; let timeText = formatter.string(from: date)
        let android = try MusicQQWire.string(device["android_id"])
        let digest = Array(Insecure.MD5.hash(data: Data(android.utf8)))
        func token(_ value: String) throws -> String { try MusicQQWire.aes(value, key: "lvcwmSYVr2Axv1gn", iv: "Zs0ntDqG2jyhKN0c") }
        let reserved: [String: Any] = [
            "harmony": "0", "clone": "0", "containe": "", "kelong": "0", "oz": try token(android),
            "oo": try token(MusicQQWire.string(device["model"])),
            "ip": "192.168.\(digest[0]).\(Int(digest[1]) % 253 + 2)", "uptimes": timeText, "multiUser": "0",
            "bod": device["board"] ?? "", "brd": device["brand"] ?? "", "dv": device["device"] ?? "",
            "firstLevel": "35", "manufact": device["manufacturer"] ?? "", "name": device["product"] ?? "",
            "host": device["host"] ?? "", "kernel": device["kernel"] ?? "", "pre": "0", "av": "20.9.0.8", "ch": ""
        ]
        var beacon = ""
        for i in 1...40 {
            var value = String(Int(digest[(i - 1) % 16]) * 37)
            if [1, 2, 13, 14, 17, 18, 21, 22, 25, 26, 29, 30, 33, 34, 37, 38].contains(i) {
                value = String(timeText.prefix(7)) + "-01\(100000 + Int(digest[0]) * 2000).\(100000000 + Int(digest[1]) * 3000000)"
            } else if i == 3 { value = "0000000000000000" }
            else if i == 4 { value = try MusicQQWire.randomHex(8) }
            beacon += "k\(i):\(value);"
        }
        let payload: [String: Any] = [
            "androidId": android, "platformId": 1, "appKey": "0AND0HD6FE4HY80F", "appVersion": "20.9.0.8",
            "beaconIdSrc": beacon, "brand": device["brand"] ?? "", "channelId": "10003505", "cid": "",
            "imei": device["imei"] ?? "", "imsi": "", "mac": "", "model": device["model"] ?? "", "networkType": "wifi",
            "oaid": "", "osVersion": "Android 15,level 35", "qimei": "", "qimei36": "", "sdkVersion": "1.2.13.6",
            "targetSdkVersion": "30", "audit": "", "userId": "{}", "packageId": "com.tencent.qqmusic",
            "deviceType": "Phone", "sdkName": "",
            "reserved": String(decoding: try MusicQQWire.json(reserved), as: UTF8.self)
        ]
        let crypt = try MusicQQWire.randomHex(8), nonce = try MusicQQWire.randomHex(8)
        let time = String(Int(date.timeIntervalSince1970)), extra = "{\"appKey\":\"0AND0HD6FE4HY80F\"}"
        let key = try MusicQQWire.rsaQimei(crypt)
        let params = try MusicQQWire.aes(String(decoding: MusicQQWire.json(payload), as: UTF8.self), key: crypt, iv: crypt)
        let body: [String: Any] = ["app": 0, "os": 1, "qimeiParams": [
            "key": key, "params": params, "time": time, "nonce": nonce, "extra": extra,
            "sign": MusicQQWire.md5(key + params + time + "000" + nonce + "ZdJqM15EeO2zWc08" + extra)
        ]]
        let response = try await request(URL(string: "https://api.tencentmusic.com/tme/trpc/proxy")!, method: "POST", headers: [
            "Content-Type": "application/json", "method": "GetQimei", "service": "trpc.tme_datasvr.qimeiproxy.QimeiProxy",
            "appid": "qimei_qq_android", "sign": MusicQQWire.md5("qimei_qq_androidpzAuCmaFAaFaHrdakPjLIEqKrGnSOOvH" + time),
            "timestamp": time, "User-Agent": "QQMusic"
        ], body: MusicQQWire.json(body), maximumBytes: 32_768, publicAccess: publicAccess)
        let root = try MusicQQWire.decode(response.data); try MusicQQWire.codes(root)
        let text = try MusicQQWire.string(root["data"], maximum: 8192)
        let inner = try MusicQQWire.decode(Data(text.utf8)); try MusicQQWire.codes(inner)
        let result = try MusicQQWire.object(inner["data"])
        return ["q16": try MusicQQWire.string(result["q16"], maximum: 128),
                "q36": try MusicQQWire.string(result["q36"], maximum: 128), "savedAt": now()]
    }
}
