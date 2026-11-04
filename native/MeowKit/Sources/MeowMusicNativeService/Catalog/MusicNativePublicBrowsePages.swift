// SPDX-License-Identifier: GPL-3.0-only
import Foundation
import MeowMusicFeature

/// Evidence for observed source positions. This is not a complete-list cache:
/// explicit whole-resource actions still read and validate the full resource.
struct MusicNativePublicBrowseWitness {
    let collectionID: String
    let head: String
    let total: Int
    var firstCollection: MusicPublicBrowseCollection
    let expires: Date
    var members: [Int: String] = [:]
}

extension MusicNativePlatforms {
    func browsePagedDetail(_ collection: MusicPublicBrowseCollection, offset: Int,
                           snapshot: String) async throws -> MusicNativePublicBrowseDetailPage {
        let own = epoch
        if !snapshot.isEmpty {
            guard let witness = detailWitnesses[snapshot], witness.expires > now(),
                  witness.collectionID == collection.id else { throw MusicServiceError.staleResult }
        }
        let page: MusicPublicBrowseNativeCollectionPage
        var witness: MusicNativePublicBrowseWitness?
        if !snapshot.isEmpty { witness = detailWitnesses[snapshot] }
        switch collection.platform {
        case .qq:
            guard let provider = qq as? any MusicPublicBrowseQQProviding else { throw MusicServiceError.unsupported }
            page = try await provider.publicBrowsePlaylistPage(id: collection.nativeID, offset: offset)
        case .kugou:
            page = try await browseKGPage(collection.nativeID, offset: offset, limit: 30,
                                          metadata: witness?.firstCollection, total: witness?.total)
        default: throw MusicServiceError.unsupported
        }
        try Task.checkCancellation()
        try musicRequire(own == epoch && page.collection.id == collection.id, .staleResult)
        try musicRequire(page.tracks.count <= 30 && offset + page.tracks.count <= page.total &&
            (!page.more || !page.tracks.isEmpty) && !page.headSignature.isEmpty, .upstreamInvalid)
        detailWitnesses = detailWitnesses.filter { $0.value.expires > now() }
        let observed = Dictionary(uniqueKeysWithValues: page.tracks.enumerated().map {
            (offset + $0.offset, MusicNativePublicBrowseSupport.digestData($0.element.data))
        })
        let key: String
        var current: MusicNativePublicBrowseWitness
        if snapshot.isEmpty {
            // A fresh read of the same observed page renews action handles
            // without inventing a different resource identity. Refresh clears
            // these witnesses; changed or expired evidence is never reused.
            if let saved = detailWitnesses.filter({ entry in
                entry.value.collectionID == collection.id && entry.value.head == page.headSignature &&
                    entry.value.total == page.total && observed.allSatisfy { entry.value.members[$0.key] == $0.value }
            }).max(by: { $0.value.expires < $1.value.expires }) {
                key = saved.key; current = saved.value
                current.firstCollection = page.collection
            } else {
                key = MusicNativePublicBrowseSupport.digest(.array([.string(collection.id), .string(UUID().uuidString)]))
                current = .init(collectionID: collection.id, head: page.headSignature, total: page.total,
                                firstCollection: page.collection, expires: now().addingTimeInterval(600))
            }
        } else {
            guard let previous = witness, previous.expires > now() else { throw MusicServiceError.staleResult }
            key = snapshot; current = previous
        }
        try musicRequire(current.head == page.headSignature && current.total == page.total, .staleResult)
        for (position, digest) in observed {
            try musicRequire(current.members[position] == nil || current.members[position] == digest, .staleResult)
            current.members[position] = digest
        }
        let otherMembers = detailWitnesses.reduce(0) { $0 + ($1.key == key ? 0 : $1.value.members.count) }
        try musicRequire(otherMembers + current.members.count <= 64_000, .limit)
        detailWitnesses[key] = current
        return .init(collection: page.collection, tracks: page.tracks,
                     sourcePositions: Array(offset..<(offset + page.tracks.count)), total: page.total,
                     more: page.more, snapshot: key, consumed: page.tracks.count)
    }

    func validateCompleteBrowse(_ result: MusicPublicBrowseNativeCollection, snapshot: String) throws {
        // A caller may already hold the same verified complete digest.
        if result.snapshot == snapshot { return }
        if result.collection.kind == .playlist, [.qq, .kugou].contains(result.collection.platform) {
            guard let witness = detailWitnesses[snapshot], witness.expires > now(),
                  witness.collectionID == result.collection.id, witness.head == result.headSignature,
                  witness.total == result.tracks.count else { throw MusicServiceError.staleResult }
            for (position, digest) in witness.members {
                try musicRequire(result.tracks.indices.contains(position) &&
                    MusicNativePublicBrowseSupport.digestData(result.tracks[position].data) == digest, .staleResult)
            }
        } else { throw MusicServiceError.staleResult }
    }

    func browseKGPage(_ id: String, offset: Int, limit: Int,
                      metadata: MusicPublicBrowseCollection? = nil,
                      total knownTotal: Int? = nil) async throws -> MusicPublicBrowseNativeCollectionPage {
        let root = try await browseKGDetail(id, offset: offset, limit: limit), data = root["data"], info = data["list_info"]
        let end = root["error_code"].id == "149"
        try musicRequire(data.isObject && (offset == 0 || info == .null || info.isObject), .upstreamInvalid)
        if info != .null {
            try musicRequire(info.isObject &&
                (!info.contains("global_collection_id") || info["global_collection_id"].string == id), .targetChanged)
        }
        let count: Int
        if end, let previous = knownTotal ?? metadata?.count {
            count = previous
        } else {
            count = try data["count"].integer()
            if info.contains("count") {
                try musicRequire(info["count"].integer() == count, .upstreamInvalid)
            }
        }
        let version = info.contains("list_ver") ? info["list_ver"] : data.contains("list_ver") ? data["list_ver"] : root["list_ver"]
        try musicRequire(version == .null || version.isInteger, .upstreamInvalid)
        let rows = data.contains("songs") ? data["songs"] : data["info"]
        try musicRequire(rows.isArray && rows.array.count <= limit &&
            (end ? rows.array.isEmpty : offset + rows.array.count <= count &&
                (!rows.array.isEmpty || offset >= count)), .upstreamInvalid)
        let collection: MusicPublicBrowseCollection
        if info.isObject, offset == 0 || metadata == nil {
            var meta = info; meta["global_collection_id"] = .string(id); meta["count"] = .n(count)
            collection = try browseCollection(meta, platform: .kugou)
        } else if let metadata {
            collection = metadata
        } else {
            throw MusicServiceError.upstreamInvalid
        }
        // The head binds only the platform, collection ID and total. Later
        // pages may omit list_info and reuse the first page's metadata.
        let head = MJ.array([.string(MusicPublicBrowsePlatform.kugou.rawValue),
                             .string(collection.nativeID), .n(count)])
        return .init(collection: collection,
                     tracks: try rows.array.map(kugouSong), total: count, more: !end && offset + rows.array.count < count,
                     headSignature: MusicNativePublicBrowseSupport.digest(head))
    }
}
