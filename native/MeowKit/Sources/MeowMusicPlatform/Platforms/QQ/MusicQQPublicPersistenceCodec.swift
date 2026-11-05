import Foundation
import MeowMusicFeature
import MeowJSON

/// Only our anonymous v1 namespace is decoded here. This is NOT a credential
/// importer, and deliberately rejects auth/cookie/unknown fields.
enum MusicQQPublicPersistenceCodec {
    static let maximumBytes = 16 * 1024
    static let strings: Set<String> = [
        "android_id", "open_udid", "open_udid2", "imei", "brand", "manufacturer",
        "model", "board", "product", "device", "host", "os_ver", "kernel"
    ]
    static let numbers: Set<String> = ["sdk", "first_api_level"]

    static func chinaDay(_ date: Date) -> String {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.calendar = Calendar(identifier: .gregorian)
        formatter.timeZone = TimeZone(identifier: "Asia/Shanghai")!
        formatter.dateFormat = "yyyy-MM-dd"
        return formatter.string(from: date)
    }
    static func time(_ value: Any?) throws -> Date {
        guard let n = value as? NSNumber, CFGetTypeID(n) != CFBooleanGetTypeID(),
              n.doubleValue.isFinite, (0...253_402_300_799).contains(n.doubleValue) else {
            throw MusicServiceError.corruptDocument
        }
        return Date(timeIntervalSince1970: n.doubleValue)
    }
    static func text(_ value: Any?, maximum: Int = 512) throws -> String {
        guard let value = value as? String, !value.isEmpty, value.utf8.count <= maximum,
              value.utf8.allSatisfy({ (32...126).contains($0) }) else { throw MusicServiceError.corruptDocument }
        return value
    }
    static func object(_ value: Any?, keys: Set<String>) throws -> [String: Any] {
        guard let value = value as? [String: Any], Set(value.keys) == keys else { throw MusicServiceError.corruptDocument }
        return value
    }
    static func decode(_ bytes: Data) throws -> [String: Any] {
        do {
            guard bytes.count <= maximumBytes else { throw MusicServiceError.corruptDocument }
            let root = try JSONObject.decode(bytes)
            guard Set(root.keys).isSubset(of: ["version", "scope", "device", "qimei", "session"]),
                  try MusicQQWire.integer(root["version"], maximum: 1, minimum: 1) == 1,
                  root["scope"] as? String == "qq-public" else { throw MusicServiceError.corruptDocument }
            var device = try object(root["device"], keys: strings.union(numbers))
            for key in strings { _ = try text(device[key]) }
            for (key, count) in [("android_id", 16), ("open_udid", 32), ("open_udid2", 32)] {
                guard let value = device[key] as? String, value.utf8.count == count,
                      value.utf8.allSatisfy({ (48...57).contains($0) || (97...102).contains($0) }) else {
                    throw MusicServiceError.corruptDocument
                }
            }
            let imei = try text(device["imei"])
            guard imei.utf8.count == 15, imei.utf8.allSatisfy({ (48...57).contains($0) }) else {
                throw MusicServiceError.corruptDocument
            }
            for key in numbers { _ = try MusicQQWire.integer(device[key], maximum: 200, minimum: 1) }
            if let raw = root["qimei"] {
                var qimei = try object(raw, keys: ["q16", "q36", "savedAt"])
                _ = try text(qimei["q16"], maximum: 128); _ = try text(qimei["q36"], maximum: 128)
                qimei["savedAt"] = try time(qimei["savedAt"])
                device["qimei"] = qimei
            }
            if let raw = root["session"] {
                var session = try object(raw, keys: ["uid", "sid", "day", "savedAt"])
                _ = try text(session["uid"], maximum: 256); _ = try text(session["sid"], maximum: 1024)
                let saved = try time(session["savedAt"])
                guard try text(session["day"], maximum: 10) == chinaDay(saved) else {
                    throw MusicServiceError.corruptDocument
                }
                session["savedAt"] = saved
                device["session"] = session
            }
            return device
        } catch { throw MusicServiceError.corruptDocument }
    }
    static func encode(_ value: [String: Any]) throws -> Data {
        guard Set(value.keys).isSubset(of: strings.union(numbers).union(["qimei", "session"])) else {
            throw MusicServiceError.corruptDocument
        }
        let device = value.filter { strings.contains($0.key) || numbers.contains($0.key) }
        var root: [String: Any] = ["version": 1, "scope": "qq-public", "device": device]
        for key in ["qimei", "session"] {
            if let raw = value[key] {
                guard var cached = raw as? [String: Any], let date = cached["savedAt"] as? Date else {
                    throw MusicServiceError.corruptDocument
                }
                cached["savedAt"] = date.timeIntervalSince1970
                root[key] = cached
            }
        }
        do {
            guard JSONSerialization.isValidJSONObject(root) else { throw MusicServiceError.corruptDocument }
            let bytes = try JSONSerialization.data(withJSONObject: root, options: [.sortedKeys])
            _ = try decode(bytes)
            return bytes
        } catch { throw MusicServiceError.corruptDocument }
    }
}
