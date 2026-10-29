import Foundation
import MeowMusicFeature
@testable import MeowMusicNativeService

// Synthetic port only. Neither this peer nor the document can use a network,
// account store, script host or real reference data.
enum NativeSyncFixture {
    static func member(_ id: String, platform: MusicAccountPlatform = .netease, occurrence: String? = nil) -> MusicLibrarySyncMember {
        let data: Data
        if platform == .qq { data = NativeMusicFixture.track(id).data }
        else {
            var row = MJ.obj([("musicId", .string(id)), ("name", .string("Synthetic " + id)),
                ("singer", .string("Fixture")), ("duration", .n(180)), ("album", .string("Album"))])
            if platform == .kugou { row["hash"] = .string(id); row["albumId"] = .string("11"); row["albumAudioId"] = .string("22") }
            data = row.bytes
        }
        return .init(nativeID: id, occurrenceID: occurrence ?? id, track: data)
    }
    static func collection(_ id: String = "501", name: String = "Remote", kind: MusicLibrarySyncKind = .playlist,
                           members: [MusicLibrarySyncMember] = [], writable: Bool = true, order: Bool = true,
                           revision: String = "1") -> MusicLibrarySyncCollection {
        .init(id: id, name: name, kind: kind, writable: writable, canRename: writable && kind == .playlist,
              canDelete: writable && kind == .playlist, canOrder: writable && order, members: members, revision: revision)
    }
    static func store(_ members: [MusicLibrarySyncMember] = [], platform: MusicAccountPlatform = .netease) throws -> NativeMusicMemoryDocument {
        var document = try MusicDocument(nil, runID: "synthetic")
        for member in members {
            let song = try MusicModel.normalize(.init(platform: platform.rawValue, data: member.track))
            document.library["library"]["songs"][song["songId"].string] = song
        }
        return NativeMusicMemoryDocument(try document.encode(changed: ["library"]))
    }
    static func refs(_ members: [MusicLibrarySyncMember], platform: MusicAccountPlatform = .netease) throws -> [MusicReference] {
        try members.map {
            let song = try MusicModel.normalize(.init(platform: platform.rawValue, data: $0.track))
            return .init(kind: .library, id: song["songId"].string)
        }
    }
    static func document(_ store: NativeMusicMemoryDocument) throws -> MusicDocument {
        try MusicDocument(store.snapshot().0, runID: "inspect")
    }
    static func request(_ platform: MusicAccountPlatform = .netease, favorites: Bool = false, playlists: Bool = true) -> MusicLibrarySyncRequest {
        .init(selections: [.init(platform: platform, favorites: favorites, playlists: playlists)])
    }
}

actor NativeSyncPeer: MusicNativeAccountProviding, MusicNativeLibrarySyncProviding {
    enum Fault { case none, lostCreateResponse, failedReadback, acknowledgedWithoutChange, appliedThenThrow }
    let platform: MusicAccountPlatform
    private var rows: [MusicLibrarySyncCollection]
    private var revision = 1, next = 600
    private var session: MusicLibrarySyncSession?
    private var uid = "42", scope = "synthetic-install:local-account"
    private var mutations: [MusicLibrarySyncMutation] = []
    private var reads = 0, begins = 0
    private var fault: Fault = .none
    private var readFailure = false
    private var readGate: NativeMusicGate?
    private var failDocument: NativeMusicMemoryDocument?
    init(platform: MusicAccountPlatform = .netease, rows: [MusicLibrarySyncCollection] = []) {
        self.platform = platform; self.rows = rows
    }
    func configure(fault: Fault = .none, gate: NativeMusicGate? = nil, failDocument: NativeMusicMemoryDocument? = nil) {
        self.fault = fault; readGate = gate; self.failDocument = failDocument
    }
    func switchAccount(uid: String = "99", scope: String = "synthetic-install:other-account") {
        self.uid = uid; self.scope = scope; session = nil
    }
    func replace(_ rows: [MusicLibrarySyncCollection]) { self.rows = rows; revision += 1 }
    func calls() -> [MusicLibrarySyncMutation] { mutations }
    func counts() -> (Int, Int) { (begins, reads) }
    func contents() -> [MusicLibrarySyncCollection] { rows }
    func beginLibrarySync(platform: MusicAccountPlatform) throws -> MusicLibrarySyncSession {
        guard platform == self.platform, session == nil else { throw MusicServiceError.busy }
        let value = MusicLibrarySyncSession(id: UUID(), platform: platform, accountID: uid, scope: scope)
        session = value; begins += 1; return value
    }
    func checkLibrarySync(_ value: MusicLibrarySyncSession) throws {
        guard session == value else { throw MusicServiceError.staleResult }
    }
    func readLibrarySync(_ value: MusicLibrarySyncSession, kinds: [MusicLibrarySyncKind]) async throws -> MusicLibrarySyncSnapshot {
        try checkLibrarySync(value); reads += 1
        if readFailure { readFailure = false; throw MusicServiceError.upstreamFailed }
        let snapshot = MusicLibrarySyncSnapshot(accountID: uid, collections: rows.filter { kinds.contains($0.kind) }, revision: String(revision))
        if let readGate { await readGate.wait() } // Deliberately non-cooperative.
        failDocument?.fail(.storageConflict)
        return snapshot
    }
    func writeLibrarySync(_ value: MusicLibrarySyncSession, mutation: MusicLibrarySyncMutation) throws -> MusicLibrarySyncWriteResult {
        try checkLibrarySync(value)
        if mutation.action == .create {
            guard mutation.directoryRevision == String(revision) else { return .notDispatched(.revisionConflict) }
        } else {
            guard rows.first(where: { $0.id == mutation.collectionID })?.revision == mutation.before?.revision else {
                return .notDispatched(.revisionConflict)
            }
        }
        mutations.append(mutation); revision += 1
        if fault == .acknowledgedWithoutChange { return .acknowledged(createdID: nil, createdName: nil) }
        var created: String?
        if mutation.action == .create {
            next += 1; created = String(next)
            rows.append(NativeSyncFixture.collection(created!, name: mutation.name!, revision: String(revision)))
        } else {
            guard let index = rows.firstIndex(where: { $0.id == mutation.collectionID }) else { throw MusicServiceError.targetChanged }
            let old = rows[index]
            if mutation.action == .delete { rows.remove(at: index) }
            else {
                var members = old.members, name = old.name
                switch mutation.action {
                case .rename: name = mutation.name!
                case .add: members += mutation.members
                case .remove: members.removeAll { row in mutation.members.contains { $0.nativeID == row.nativeID } }
                case .order: members = mutation.members
                default: break
                }
                rows[index] = NativeSyncFixture.collection(old.id, name: name, kind: old.kind, members: members,
                    writable: old.writable, order: old.canOrder, revision: String(revision))
            }
        }
        if fault == .failedReadback { readFailure = true; fault = .none }
        if fault == .lostCreateResponse || fault == .appliedThenThrow { throw MusicServiceError.timeout }
        return .acknowledged(createdID: created, createdName: mutation.name)
    }
    func endLibrarySync(_ value: MusicLibrarySyncSession) { if session == value { session = nil } }
    func nativeRecommendations(platform: MusicAccountPlatform, kind: String, cursor: String?) throws -> MusicNativeTrackPage { throw MusicServiceError.unavailable }
    func nativeCollections(platform: MusicAccountPlatform, cursor: String?) throws -> MusicAccountCollectionPage { throw MusicServiceError.unavailable }
    func nativeTracks(platform: MusicAccountPlatform, collectionID: String, cursor: String?) throws -> MusicNativeTrackPage { throw MusicServiceError.unavailable }
    func nativeOptions(platform: MusicAccountPlatform, nativeID: String, sourceKey: String) throws -> [MusicPlaybackOption] { throw MusicServiceError.unavailable }
    func nativeResolve(platform: MusicAccountPlatform, nativeID: String, selection: MusicPlaybackSelection) throws -> MusicNativeAudio { throw MusicServiceError.unavailable }
}
