// SPDX-License-Identifier: GPL-3.0-only
// Data-only presentation rules from iWebPlus b900c259 discovery-normalize.js.
import Foundation
import MeowMusicFeature

enum MusicNativeHomePresentation {
    static func rankingLayout(_ group: MJ) -> MusicPublicBrowseSection.Layout {
        switch MusicNativePublicBrowseParser.text(group["frontDisplayType"], group["displayType"]) {
        case "TOP_3": return .rankingStack
        case "ONLY_COVER_SMALL": return .rankingSmall
        default: return .playlistRail
        }
    }

    static func ranking(_ row: MJ) throws -> MusicPublicBrowseRanking? {
        let rows = row["rankItems"].isArray ? row["rankItems"] : row["trackRankList"]
        let artwork = MusicNativePublicBrowseSupport.artwork(row["songCoverImgUrl"])
        let frequency = row["updateFrequency"].string
        guard rows.isArray || artwork != nil || !frequency.isEmpty else { return nil }
        _ = try MJ.string(frequency).text(1024, empty: true)
        let preview = try rows.array.prefix(3).map { value -> MusicPublicBrowseRanking.Entry in
            let title = MusicNativePublicBrowseParser.text(value["songName"], value["title"])
            let artist = MusicNativePublicBrowseParser.text(value["artistName"], value["subTitle"])
            _ = try MJ.string(title).text(1024, empty: true)
            _ = try MJ.string(artist).text(1024, empty: true)
            return .init(id: positiveID(value["trackId"]) ?? positiveID(value["songId"]) ?? "",
                         title: title, artist: artist, artwork: MusicNativePublicBrowseParser.image(value),
                         rank: value["rank"].isInteger && value["rank"].int > 0 ? value["rank"].int : nil,
                         lastRank: value["lastRank"].isInteger && value["lastRank"].int >= 0 ? value["lastRank"].int : nil)
        }
        return .init(preview: preview, songArtwork: artwork, updateFrequency: frequency)
    }

    static func hasValue(_ value: MJ) -> Bool {
        switch value {
        case .null: return false
        case .bool(let value): return value
        case .string(let value): return !value.isEmpty
        case .number(let value): return value != 0
        default: return true
        }
    }

    static func primaryAction(_ row: MJ) -> MJ {
        [row["action"], row["url"], row["orpheus"], row["clickAction"]].first(where: hasValue) ?? .null
    }

    static func labels(_ row: MJ, kind: String, modern: Bool) -> (String, String) {
        var name = MusicNativePublicBrowseParser.title(row)
        var subtitle = MusicNativePublicBrowseParser.text(row["subTitle"], row["subtitle"])
        if modern, !row["singleLineTitle"].string.isEmpty {
            if kind == "dailysongs", hasValue(row["dayOfMonth"]) {
                // The dated card's subTitle is a calendar badge, not its name.
                if let separator = name.range(of: #"\s*\|\s*"#, options: .regularExpression) {
                    name = String(name[..<separator.lowerBound])
                }
            } else if !subtitle.isEmpty {
                name = subtitle
            }
            subtitle = row["singleLineTitle"].string
        }
        return (name, subtitle)
    }

    static func playlistArtwork(_ row: MJ, id: String, action: MJ) -> URL? {
        guard row["coverType"].string == "ALBUM_LOGO", action.isString,
              action.string.utf8.count <= 12_000, let url = URLComponents(string: action.string),
              url.scheme == "orpheus", url.host == "playlist", url.path == "/" + id,
              url.user == nil, url.password == nil, url.port == nil, url.fragment == nil else { return nil }
        let covers = (url.queryItems ?? []).filter { $0.name == "coverUrl" }
        guard covers.count == 1, let cover = covers.first?.value else { return nil }
        // Keep HTTPS and credential rejection, but image delivery may use any CDN host.
        return MusicNativePublicBrowseSupport.artwork(.string(cover))
    }

    static func positiveID(_ value: MJ, digits: Int = 18, safe: Bool = false) -> String? {
        let id = value.id, bytes = id.utf8
        guard (1...digits).contains(bytes.count), bytes.first.map({ (49...57).contains($0) }) == true,
              bytes.allSatisfy({ (48...57).contains($0) }),
              !safe || (UInt64(id) ?? UInt64.max) <= 9_007_199_254_740_991 else { return nil }
        return id
    }

    /// The normalizer gives explicit modes precedence over legacy rule/tab
    /// selectors. An invalid nonempty selector never falls through to another.
    static func fmAction(_ row: MJ, channel: Bool, name: String) throws -> MusicHomeAction? {
        let mode: MJ = row["mode"] == .null ? .string("") : row["mode"]
        let submode = [row["subMode"], row["submode"]].first(where: hasValue) ?? .string("")
        if mode == .string(""), hasValue(submode) { return nil }
        let hasRule = row["ruleId"] != .null && row["ruleId"] != .string("")
        let hasTab = row["tabTagId"] != .null && row["tabTagId"] != .string("")
        let result: MusicHomeAction
        if mode == .string(""), channel || hasRule || hasTab {
            let ruleID: String, tabID: String
            if hasRule {
                guard let id = positiveID(row["ruleId"], digits: 16, safe: true) else { return nil }
                ruleID = id; tabID = ""
            } else {
                let tabs = ["rap", "electronic", "sleep", "chinese", "occident", "korea", "classical"]
                guard let id = positiveID(row["tabTagId"], digits: 16, safe: true) ??
                        (tabs.contains(row["tabTagId"].string) ? row["tabTagId"].string : nil) else { return nil }
                ruleID = ""; tabID = id
            }
            let sourceName = row["fmLogoTextContent"].string.isEmpty ? name : row["fmLogoTextContent"].string
            result = .ruleFM(ruleID: ruleID, tabTagID: tabID, sourceName: sourceName)
        } else {
            guard mode.isString, let value = MusicHomeAction.FMMode(rawValue: mode.string.isEmpty ? "DEFAULT" : mode.string),
                  submode.isString else { return nil }
            result = .fm(mode: value, submode: submode.string, entry: channel ? .channel : nil)
        }
        do { try result.validate() }
        catch MusicServiceError.invalidRequest { return nil } // An unsupported declaration, not a failed request.
        return result
    }

    enum SongCollection { case none, ids([String]) }
    static func songCollection(_ row: MJ, play: MJ) -> SongCollection {
        let declared = play["playAction"]["songIds"] == .null ? row["songIds"] : play["playAction"]["songIds"]
        guard declared.isArray, !declared.array.isEmpty else { return .none }
        var seen = Set<String>()
        let ids = declared.array.compactMap { positiveID($0) }.filter { seen.insert($0).inserted }
        return ids.isEmpty ? .none : .ids(ids)
    }

    static func actionID(_ action: MusicHomeAction, resourceID: String = "") -> String {
        switch action {
        case .fm(let mode, let submode, let entry):
            return "fm:\(mode.rawValue):\(submode):\(entry?.rawValue ?? "")"
        case .ruleFM(let ruleID, let tabID, _): return "fm:rule:\(ruleID):\(tabID)"
        case .similar(let ids, _): return "similar:song:" + ids.joined(separator: ",")
        case .similarArtist(let ids, _): return "similar:artist:" + ids.joined(separator: ",")
        case .daily(let branch): return "daily:" + branch.rawValue
        case .heartbeat: return "heartbeat"
        case .heartbeatSong(let id): return "heartbeat:" + id
        case .songCollection(let ids):
            // Same FNV-1a fingerprint as the reference; never Swift's randomized
            // Hasher, a title, or a preview/playlist ID masquerading as a queue.
            var hash: UInt32 = 2_166_136_261
            for byte in ids.joined(separator: ",").utf8 { hash = (hash ^ UInt32(byte)) &* 16_777_619 }
            return "songs:" + String(resourceID.prefix(100)) + ":" + String(hash, radix: 16)
        }
    }

    /// Stable home identity for merging continuation pages. Only anonymous
    /// blocks need the cursor/index fallback; identified columns must not.
    static func blockID(_ block: MJ, index: Int, previous: MJ, occurrences: inout [String: Int]) throws -> String {
        let code = MusicNativePublicBrowseParser.textID(
            block["positionCode"], block["blockCode"], block["code"], block["id"])
        let instance = MusicNativePublicBrowseParser.textID(block["id"], block["channelCode"])
        let base: String
        if code.isEmpty && instance.isEmpty {
            // No stable identity is supplied. Do not merge unrelated anonymous
            // blocks at index zero on every continuation page.
            base = "anonymous:" + MusicNativePublicBrowseSupport.digest(previous) + ":\(index)"
        } else {
            base = "block:" + (try identityComponent(code)) + ":" + (try identityComponent(instance))
        }
        let occurrence = occurrences[base, default: 0]
        occurrences[base] = occurrence + 1
        return base + ":\(occurrence)"
    }

    static func rootID(_ root: MJ, index: Int) throws -> String {
        let id = MusicNativePublicBrowseParser.textID(root["id"], root["positionCode"],
            root["blockCode"], root["code"], root["block"]["id"])
        return id.isEmpty ? "root-index:\(index)" : "root:" + (try identityComponent(id))
    }
    private static func identityComponent(_ value: String) throws -> String {
        _ = try MJ.string(value).text(200, empty: true)
        return value.replacingOccurrences(of: "%", with: "%25").replacingOccurrences(of: ":", with: "%3A")
    }

    static func legacyResources(_ block: MJ) -> [MJ] {
        block["creatives"].array.flatMap { creative -> [MJ] in
            guard !["video", "voice", "podcast"].contains(where: creative["creativeType"].string.lowercased().contains) else { return [] }
            let resources = creative["resources"].array
            if resources.isEmpty {
                var row = creative
                row["resourceId"] = creative["creativeId"]
                return [row]
            }
            return resources.map { resource in
                var row = creative
                for (key, value) in resource.fields { row[key] = value }
                row["creativeType"] = creative["creativeType"]
                if !hasValue(resource["uiElement"]) { row["uiElement"] = creative["uiElement"] }
                if !hasValue(resource["action"]) { row["action"] = creative["action"] }
                return row
            }
        }
    }
}
