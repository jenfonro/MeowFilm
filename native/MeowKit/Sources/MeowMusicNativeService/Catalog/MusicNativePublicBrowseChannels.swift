// SPDX-License-Identifier: GPL-3.0-only
import Foundation
import MeowMusicFeature

struct MusicNativePublicBrowseCube {
    var page = MusicNativePublicBrowseDiscovery()
    var feed: MJ?
    var finiteFeed: MusicNativePublicBrowseSection?
    var groups: [(id: String, title: String, node: MJ, parentKey: String, path: String)] = []
    var selected = ""
    var budget = 6000

    mutating func collect(_ node: MJ, depth: Int = 0, parent: MJ = .null,
                          path: String = "0", parentKey: String = "") throws {
        budget -= 1; try musicRequire(depth <= 25 && budget >= 0, .limit)
        if ["MusicGenreSongListItem", "TabItem"].contains(node["componentName"].string) {
            let id = node["id"].id.isEmpty ? "tab-\(groups.count)" : node["id"].id
            try musicRequire(id.range(of: "^[A-Za-z0-9_-]{1,160}$", options: .regularExpression) != nil, .upstreamInvalid)
            try musicRequire(!groups.contains { $0.id == id }, .upstreamInvalid)
            var value = node
            value["browseParent"] = parent["componentName"]
            value["browseNewTabs"] = parent["props"]["useNewTabBarStyle"]
            groups.append((id, node["props"]["title"].string, value, parentKey, path)); return
        }
        for (index, child) in node["children"].array.enumerated() {
            try collect(child, depth: depth + 1, parent: node, path: path + "/\(index)", parentKey: path)
        }
    }
    func items(_ rows: [MJ], prefix: String) throws -> [MusicNativePublicBrowseItem] {
        try musicRequire(rows.count <= 300, .limit)
        var seen = Set<String>()
        return try rows.enumerated().compactMap { try MusicNativePublicBrowseParser.resource($0.element, index: prefix + ":\($0.offset)", channel: true) }
            .filter { seen.insert($0.id).inserted }
    }
    mutating func append(_ rows: [MJ], title: String, layout: MusicPublicBrowseSection.Layout = .playlistRail) throws {
        append(try items(rows, prefix: String(page.sections.count)), title: title, layout: layout)
    }
    mutating func append(_ rows: [MusicNativePublicBrowseItem], title: String, layout: MusicPublicBrowseSection.Layout) {
        if !rows.isEmpty { page.sections.append(.init(id: String(page.sections.count), title: title, items: rows, layout: layout)) }
    }
    mutating func walk(_ node: MJ, depth: Int = 0, title: String = "", path: String = "0") throws {
        budget -= 1; try musicRequire(depth <= 25 && budget >= 0, .limit)
        let component = node["componentName"].string, props = node["props"], children = node["children"].array
        if ["MusicGenreSongListItem", "TabItem"].contains(component),
           let group = groups.first(where: { $0.path == path }), group.id != selected { return }
        if props["songList"]["ruleId"] != .null {
            let ruleID = try MusicPlatformProtocol.numericID(props["songList"]["ruleId"])
            let declared = props["songList"]["pointId"]
            try musicRequire(declared == .null || declared.isArray, .upstreamInvalid)
            let points = declared.array
            try musicRequire(points.count <= 20, .limit)
            feed = .obj([("ruleId", .string(ruleID)),
                ("pointIds", .string(try points.map { try MusicPlatformProtocol.numericID($0["id"]) }.joined(separator: ","))),
                ("title", props["songList"]["title"])])
            finiteFeed = nil
        }
        if props["carousel"].isArray {
            var rows: [MJ] = []
            try musicRequire(props["carousel"].array.count <= 100, .limit)
            for raw in props["carousel"].array {
                guard let action = try MusicNativePublicBrowseParser.action(raw["action"]),
                      ["song", "playlist"].contains(action.kind) else { continue }
                var value = raw; value["resourceType"] = .string(action.kind); value["resourceId"] = .string(action.id)
                rows.append(value)
            }
            try append(rows, title: "", layout: .banner); return
        }
        if props["recommendModule"].isArray {
            let groups = props["recommendModule"].array
            try musicRequire(groups.count <= 30, .limit)
            var tabs: [MusicNativePublicBrowseSectionTab] = [], selected = ""
            for (index, group) in groups.enumerated() {
                let rows = group["modules"].array.map { raw -> MJ in
                    var value = raw; value["resourceType"] = raw["type"]
                    if raw["type"].string == "fm",
                       raw["tabTagId"] == .null || raw["tabTagId"] == .string("") {
                        value["tabTagId"] = props["tab"]
                    }
                    return value
                }
                let id = (node["id"].id.isEmpty ? String(page.sections.count) : node["id"].id) + ":group:\(index)"
                let values = try items(rows, prefix: id)
                guard !values.isEmpty else { continue }
                tabs.append(.init(id: id, title: group["title"].string, items: values))
                if selected.isEmpty && group["isDefaultTab"] == .bool(true) { selected = id }
            }
            try musicRequire(tabs.reduce(0, { $0 + $1.items.count }) <= 600, .limit)
            if let choice = tabs.first(where: { $0.id == selected }) ?? tabs.first {
                page.sections.append(.init(id: String(page.sections.count), title: props["title"].string,
                    items: choice.items, layout: .playlistRail, tabs: tabs.count > 1 ? tabs : [],
                    selectedTab: choice.id))
            }
            return
        }
        if props["playlistIds"].isString {
            let ids = props["playlistIds"].string.split(separator: ",").map(String.init)
            let names = props["titles"].string.components(separatedBy: ",")
            try musicRequire(ids.count <= 100, .limit)
            let rows = ids.enumerated().map { index, id in
                MJ.obj([("resourceType", .string("playlist")), ("id", .string(id)),
                    ("name", .string(names.indices.contains(index) ? names[index] : ""))])
            }
            try append(rows, title: props["rankModuleTitle"].string, layout: .rankedSongs); return
        }
        if component == "BillboardSingleSongList", !props["id"].id.isEmpty {
            try append([.obj([("resourceType", .string("playlist")), ("id", props["id"]),
                ("name", props["cardTitle"]), ("subtitle", props["cardSubTitle"])])], title: props["cardTitle"].string)
            return
        }
        if props["albumData"].isObject {
            var row = props["albumData"]; row["resourceType"] = .string("album"); row["subtitle"] = row["subTitle"]
            try append([row], title: title); return
        }
        if component == "InfiniteScrollList" {
            let lists = children.filter { ["Grid", "Song"].contains($0["componentName"].string) }
            var rows: [MJ] = []
            for list in lists { try collectFiniteSongs(list, rows: &rows) }
            finiteFeed = .init(id: "feed", title: props["listTitleStr"].string,
                items: try items(rows, prefix: "feed"), filters: [.init(id: selected + "|", title: "全部歌曲")],
                selectedFilter: selected + "|")
            feed = nil
            for (index, child) in children.enumerated() where !["Grid", "Song"].contains(child["componentName"].string) {
                try walk(child, depth: depth + 1, title: title, path: path + "/\(index)")
            }
            return
        }
        if ["Grid", "Scroll"].contains(component) {
            let leafNames = ["Playlist", "Song", "Album", "Entrance"]
            let rows = children.filter { leafNames.contains($0["componentName"].string) }.map { child -> MJ in
                var value = child["props"]
                value["resourceType"] = .string(child["componentName"].string == "Entrance" ? "" : child["componentName"].string.lowercased())
                return value
            }
            let values = try items(rows, prefix: String(page.sections.count))
            func song(_ item: MusicNativePublicBrowseItem) -> Bool {
                switch item.content { case .song, .songID: return true; default: return false }
            }
            append(values.filter(song), title: title, layout: .songColumns)
            append(values.filter { !song($0) }, title: title, layout: component == "Grid" ? .grid : .playlistRail)
            for (index, child) in children.enumerated() where !leafNames.contains(child["componentName"].string) {
                try walk(child, depth: depth + 1, path: path + "/\(index)")
            }
            return
        }
        // A finite InfiniteScrollList remains finite unless a verified feed
        // descriptor exists. No guessed offset endpoint or H5 execution.
        var heading = title
        for (index, child) in children.enumerated() {
            if child["componentName"].string == "Title" {
                heading = MusicNativePublicBrowseParser.text(child["props"]["mainTitle"], child["props"]["title"])
            } else { try walk(child, depth: depth + 1, title: heading, path: path + "/\(index)") }
        }
    }
    mutating func collectFiniteSongs(_ node: MJ, depth: Int = 0, rows: inout [MJ]) throws {
        budget -= 1; try musicRequire(depth <= 12 && budget >= 0 && rows.count <= 300, .limit)
        if node["componentName"].string == "Song" {
            try musicRequire(rows.count < 300, .limit)
            var value = node["props"]; value["resourceType"] = .string("song"); rows.append(value)
        } else {
            for child in node["children"].array { try collectFiniteSongs(child, depth: depth + 1, rows: &rows) }
        }
    }
}

extension MusicNativePlatforms {
    func browseCube(_ channel: MusicPublicBrowseChannel, tag: String, remote: MJ) async throws -> MusicNativePublicBrowseDiscovery {
        try musicRequire(channel.pageCode.range(of: "^[A-Za-z0-9_-]{6,100}$", options: .regularExpression) != nil)
        _ = try MJ.string(tag).text(220, empty: true)
        let parts = tag.components(separatedBy: "|")
        try musicRequire(parts.count <= 2)
        let selectedGroup = parts.first ?? "", selectedAlgorithm = parts.count == 2 ? parts[1] : ""
        let root = try await browseWEAPI("/api/cube/render/page/protocol", .obj([("pageCode", .string(channel.pageCode)), ("scene", .string("music"))]))
        let raw = root["data"]["pageProtocol"] != .null ? root["data"]["pageProtocol"] : root["pageProtocol"]
        let node = try MusicNativePublicBrowseParser.object(raw)
        try musicRequire(node["pages"].isArray && node["pages"].array.count <= 30, .upstreamInvalid)
        let signature = MusicNativePublicBrowseSupport.digest(node)
        if remote["snapshot"] != .null { try musicRequire(remote["snapshot"].string == signature, .staleResult) }
        var cube = MusicNativePublicBrowseCube()
        for (index, page) in node["pages"].array.enumerated() {
            try cube.collect(page, path: "page-\(index)", parentKey: "root")
        }
        if !selectedGroup.isEmpty {
            try musicRequire(cube.groups.contains { $0.id == selectedGroup }, .staleResult)
            cube.selected = selectedGroup
        } else if let explicit = cube.groups.first(where: { $0.node["props"]["isDefaultTab"] == .bool(true) }) {
            cube.selected = explicit.id
        } else if let first = cube.groups.first {
            let middle = first.node["browseParent"].string == "MusicGenreSongList" && first.node["browseNewTabs"] != .bool(true)
            let siblings = cube.groups.filter { $0.parentKey == first.parentKey }
            cube.selected = siblings[middle ? siblings.count / 2 : 0].id
        }
        cube.page.filters = cube.groups.filter { !$0.title.isEmpty }.map { .init(id: $0.id + "|", title: $0.title) }
        cube.page.selected = cube.selected + "|"
        for (index, page) in node["pages"].array.enumerated() { try cube.walk(page, path: "page-\(index)") }
        if let feed = cube.feed {
            let offset = remote["offset"] == .null ? 0 : try remote["offset"].integer()
            try musicRequire(offset % 30 == 0)
            var seen = Set<String>(), filters: [MusicPublicBrowseFilter] = []
            do {
                let tags = try await wy("/api/tag/tab/tags", .obj([("ruleId", feed["ruleId"]), ("ruleType", .n(1)), ("tabTagId", .string(""))]))
                let tagRows = tags["data"]["algTagInfos"] != .null ? tags["data"]["algTagInfos"] : tags["algTagInfos"]
                // The real public endpoint returns algTagInfos:null for
                // channels without algorithm choices. It is not a failed feed.
                try musicRequire((tagRows == .null || tagRows.isArray) && tagRows.array.count <= 100, .upstreamInvalid)
                var flat: [(MJ, String)] = []
                for row in tagRows.array {
                    if row["children"].isArray && !row["children"].array.isEmpty {
                        let group = try row["algTagName"].text(200, empty: true)
                        flat += row["children"].array.map { ($0, group) }
                    } else { flat.append((row, "")) }
                }
                try musicRequire(flat.count <= 200, .limit)
                for (row, group) in flat {
                    let id = try MusicPlatformProtocol.numericID(row["algTagId"]), title = try row["algTagName"].text(200)
                    if seen.insert(id).inserted { filters.append(.init(id: cube.selected + "|" + id, title: title, group: group)) }
                }
            } catch {
                let failure = MusicServiceError.sanitized(error)
                if Task.isCancelled || failure.isAuthenticationFailure ||
                    [.cancelled, .closed, .staleAccess, .accessUnavailable].contains(failure) { throw error }
                // netease-client.js:272–273 treats optional filter reads as
                // empty, while still fetching the channel's actual songs.
                seen = []; filters = []
            }
            // Never silently substitute the all-songs feed when the chosen
            // algorithm cannot be validated. Preserve the caller's last page.
            try musicRequire(selectedAlgorithm.isEmpty || seen.contains(selectedAlgorithm), .upstreamInvalid)
            if offset > 0 { cube.page.sections.removeAll() }
            var section = MusicNativePublicBrowseSection(id: "feed", title: feed["title"].string,
                items: [], filters: [.init(id: cube.selected + "|", title: "全部歌曲")] + filters,
                selectedFilter: cube.selected + "|" + selectedAlgorithm)
            do {
                let response = try await wy("/api/tag/tab/alg/recommends", .obj([
                    ("ruleType", .n(1)), ("ruleId", feed["ruleId"]), ("tabTagId", .string("")),
                    ("algTagIds", .string(selectedAlgorithm)), ("extMap", .string(MJ.obj([("pointIds", feed["pointIds"])]).json)),
                    ("limit", .n(30)), ("offset", .n(offset)), ("trialMode", .n(103)), ("source", .string(offset == 0 ? "reset" : "nextPage"))]))
                let data = response["songs"] != .null ? response : response["data"]
                let more = response["hasMore"] == .null ? data["hasMore"] : response["hasMore"]
                try musicRequire((data["songs"].isArray || data["songs"] == .null) &&
                    data["songs"].array.count <= 30 && (more.isBool || more == .null), .upstreamInvalid)
                section.items = try data["songs"].array.map { raw -> MusicNativePublicBrowseItem in
                    .init(id: try MusicPlatformProtocol.numericID(raw["id"]), title: try raw["name"].text(1024), content: .song(try Self.browseWYSong(raw)))
                }
                if more.bool && !section.items.isEmpty {
                    // The next offset must remain exactly representable in JSON.
                    try musicRequire(offset <= 9_007_199_254_740_991 - 30, .limit)
                    cube.page.next = .obj([("offset", .n(offset + 30)), ("snapshot", .string(signature))])
                }
            } catch {
                let failure = MusicServiceError.sanitized(error)
                if offset > 0 || Task.isCancelled ||
                    failure.isAuthenticationFailure ||
                    [.cancelled, .closed, .staleAccess, .accessUnavailable].contains(failure) { throw error }
                section.error = failure
            }
            cube.page.sections.append(section)
        } else {
            try musicRequire(remote == .obj() && selectedAlgorithm.isEmpty, .invalidRequest)
            if let finite = cube.finiteFeed { cube.page.sections.append(finite) }
        }
        return cube.page
    }
}
