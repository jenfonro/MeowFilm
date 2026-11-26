#if os(iOS) || os(macOS)
import Foundation
import Combine
import MeowDebug

@MainActor public final class MusicCollectionEditorDraft: ObservableObject, Identifiable {
    public let id = UUID()
    public let title: String
    @Published public private(set) var tracks: [MusicRemoteTrack]
    @Published public private(set) var selected: Set<MusicReference> = []
    fileprivate let collection: MusicCollection
    fileprivate let presentationID: UUID
    fileprivate let original: [MusicRemoteTrack]
    fileprivate var consumed = false
    fileprivate var revoked = false

    fileprivate init(collection: MusicCollection, presentationID: UUID, tracks: [MusicRemoteTrack]) {
        self.collection = collection; self.presentationID = presentationID
        self.tracks = tracks; original = tracks; title = collection.title
    }
    public var hasChanges: Bool { tracks.map(\.ref) != original.map(\.ref) }
    public var allSelected: Bool { !tracks.isEmpty && tracks.allSatisfy { selected.contains($0.ref) } }
    public var selectedTracks: [MusicRemoteTrack] { tracks.filter { selected.contains($0.ref) } }
    public var canEdit: Bool { !revoked && !consumed }
    public func toggle(_ ref: MusicReference) {
        guard canEdit, tracks.contains(where: { $0.ref == ref }) else { return }
        if !selected.insert(ref).inserted { selected.remove(ref) }
    }
    public func toggleAll() {
        guard canEdit else { return }
        selected = allSelected ? [] : Set(tracks.map(\.ref))
    }
    public func deleteSelected() {
        guard canEdit else { return }
        tracks.removeAll { selected.contains($0.ref) }; selected = []
    }
    public func remove(at offsets: IndexSet) {
        guard canEdit, !offsets.isEmpty, offsets.allSatisfy(tracks.indices.contains) else { return }
        for index in offsets { selected.remove(tracks[index].ref) }
        tracks = tracks.enumerated().filter { !offsets.contains($0.offset) }.map(\.element)
    }
    public func move(from offsets: IndexSet, to destination: Int) {
        guard canEdit, !offsets.isEmpty, offsets.allSatisfy(tracks.indices.contains),
              (0...tracks.count).contains(destination) else { return }
        let moving = offsets.map { tracks[$0] }
        var remaining = tracks.enumerated().filter { !offsets.contains($0.offset) }.map(\.element)
        remaining.insert(contentsOf: moving, at: destination - offsets.filter { $0 < destination }.count)
        tracks = remaining
    }
    fileprivate func project(_ songs: [String: MusicRemoteTrack]) { tracks = tracks.map { $0.projected(songs) } }
}

struct MusicCollectionPageSnapshot: Sendable {
    let collection: MusicCollection
    let tracks: [MusicRemoteTrack]
}

@MainActor final class WeakMusicCollectionConsumer {
    weak var value: MusicCollectionViewModel?
    init(_ value: MusicCollectionViewModel) { self.value = value }
}

@MainActor public final class MusicCollectionViewModel: ObservableObject {
    public let collectionID: String
    @Published public private(set) var collection: MusicCollection?
    @Published public private(set) var tracks: [MusicRemoteTrack] = []
    @Published public private(set) var isLoading = false
    @Published public private(set) var isActing = false
    @Published public private(set) var isMissing = false
    @Published public private(set) var error: MusicServiceError?
    @Published public private(set) var presentationID = UUID()
    @Published public var searchQuery = "" {
        didSet {
            guard !oldValue.utf8.elementsEqual(searchQuery.utf8) else { return }
            filterID = UUID()
            if action?.filterSensitive == true { action?.task.cancel() }
        }
    }
    @Published public private(set) var editor: MusicCollectionEditorDraft?
    @Published public private(set) var favoritesLoaded = false
    @Published public private(set) var favoritesLoading = false
    @Published public private(set) var favoritesError: MusicServiceError?
    @Published public private(set) var actionError: String?
    @Published public private(set) var filterID = UUID()
    public var filteredTracks: [MusicRemoteTrack] { Self.filter(tracks, query: searchQuery) }
    private static let jsWhitespace = CharacterSet(
        charactersIn: "\u{0009}\u{000A}\u{000B}\u{000C}\u{000D}\u{0020}\u{00A0}\u{1680}\u{2000}\u{2001}\u{2002}\u{2003}\u{2004}\u{2005}\u{2006}\u{2007}\u{2008}\u{2009}\u{200A}\u{2028}\u{2029}\u{202F}\u{205F}\u{3000}\u{FEFF}"
    )

    public static func filter(_ tracks: [MusicRemoteTrack], query: String) -> [MusicRemoteTrack] {
        let needle = query.trimmingCharacters(in: jsWhitespace).lowercased(with: .current)
        guard !needle.isEmpty else { return tracks }
        return tracks.filter { track in
            [track.title, track.artist, track.album].contains {
                ($0.lowercased(with: .current) as NSString).range(of: needle, options: .literal).location != NSNotFound
            }
        }
    }
    public var canEdit: Bool { canAct && collection?.canEdit == true }
    public var canRename: Bool { canEdit && collection?.kind == "playlist" }
    public var isCurrentCollection: Bool {
        guard searchQuery.isEmpty, !model.isClosed, model.current != nil else { return false }
        return model.state.sourceCollectionID?.utf8.elementsEqual(collectionID.utf8) == true
    }
    public var isPlayingCollection: Bool {
        isCurrentCollection && (model.phase == .playing || model.phase == .buffering)
    }
    public var playAllTitle: String {
        isPlayingCollection ? "暂停播放" : isCurrentCollection ? "继续播放" : "播放全部"
    }
    public var canAct: Bool {
        visible && !model.isClosed && !isMissing && !isLoading && !isActing && !needsReload && collection != nil
    }

    private let model: MusicViewModel
    private let observerID = UUID()
    private var visible = false
    private var needsReload = false
    private var reads: [UUID: Task<Void, Never>] = [:]
    private var songObservation: Task<Void, Never>?
    private var songProjectionID = UUID()
    private var action: (id: UUID, task: Task<Void, Never>, filterSensitive: Bool)?
    private var favoriteSnapshot: (collection: MusicCollection, refs: Set<MusicReference>)?
    private var favoriteReadID: UUID?

    public init(model: MusicViewModel, collectionID: String) {
        self.model = model; self.collectionID = collectionID
    }

    public func clearActionError() { actionError = nil }

    @discardableResult public func appear() -> Task<Void, Never>? {
        guard !visible else { return nil }
        visible = true; model.observeCollectionPage(self, id: observerID)
        observeSongs()
        return reload()
    }
    public func disappear() {
        visible = false; model.removeCollectionPage(id: observerID)
        songObservation?.cancel(); songObservation = nil
        invalidate(); searchQuery = ""
    }
    private func observeSongs() {
        songObservation?.cancel(); songObservation = nil
        guard visible, !model.isClosed, let repository = model.service else { return }
        songObservation = Task { [weak self] in
            let changes = await repository.songChanges()
            await self?.refreshSongProjections(repository)
            for await changed in changes {
                guard !Task.isCancelled else { return }
                await self?.refreshSongProjections(repository, changed: changed)
            }
        }
    }
    private func refreshSongProjections(_ repository: NativeMusicService, changed: Set<String>? = nil) async {
        guard !Task.isCancelled, visible, !model.isClosed else { return }
        let id = presentationID, projection = UUID()
        let referenced = Set((tracks + (editor?.tracks ?? [])).map(\.songId))
        let ids = changed.map { referenced.intersection($0) } ?? referenced
        guard !ids.isEmpty else { return }
        songProjectionID = projection
        do {
            let songs = try await repository.repositoryTracks(songIDs: ids)
            guard !Task.isCancelled, visible, !model.isClosed,
                  presentationID == id, songProjectionID == projection else { return }
            tracks = tracks.map { $0.projected(songs) }; editor?.project(songs)
        } catch {
            if !Task.isCancelled, MusicServiceError.sanitized(error) != .cancelled {
                DebugRuntime.logError(.musicLibrary, sub: "collection", event: "refreshSongProjections.error", error: error)
            }
        }
    }

    private func invalidate() {
        editor?.revoked = true; editor = nil
        for task in reads.values { task.cancel() }
        action?.task.cancel()
        favoriteReadID = nil; favoriteSnapshot = nil
        favoritesLoaded = false; favoritesLoading = false; favoritesError = nil; actionError = nil
        presentationID = UUID()
        collection = nil; tracks = []; error = nil
        isLoading = false; isActing = action != nil; needsReload = false
    }
    func ownerClosed() {
        guard visible else { return }
        invalidate(); searchQuery = ""
    }
    func catalogueChanged(_ rows: [MusicCollection], origin: UUID? = nil) {
        guard visible, !model.isClosed, !isMissing else { return }
        if let origin, presentationID == origin { return }
        guard let next = rows.first(where: { $0.id.utf8.elementsEqual(collectionID.utf8) }) else {
            invalidate(); isMissing = true; return
        }
        if let collection, collection == next {
            let favorite = rows.first { $0.kind == "favorites" }
            if favoriteSnapshot?.collection != favorite { refreshFavorites() }
            return
        }
        if let draft = editor, !draft.consumed {
            draft.revoked = true
            for task in reads.values { task.cancel() }
            action?.task.cancel()
            error = .staleRevision; needsReload = true; isLoading = false
            return
        }
        reload(preservingContent: true)
    }

    @discardableResult public func reload(preservingContent: Bool = false) -> Task<Void, Never>? {
        let retained = (preservingContent && visible && !model.isClosed && !isMissing &&
            (editor == nil || editor?.consumed == true)) ? collection.map { ($0, tracks) } : nil
        invalidate()
        guard visible, !model.isClosed, !isMissing else { return nil }
        if let retained { collection = retained.0; tracks = retained.1 }
        let own = UUID(); presentationID = own; isLoading = true
        let task = Task { [weak self] in
            guard let self else { return }
            defer {
                self.reads.removeValue(forKey: own)
                if self.presentationID == own { self.isLoading = false }
            }
            do {
                let snapshot = try await self.model.readCollectionPage(id: self.collectionID, origin: own)
                guard !Task.isCancelled, self.visible, !self.model.isClosed, self.presentationID == own else { return }
                guard let snapshot else { self.invalidate(); self.isMissing = true; return }
                self.collection = snapshot.collection; self.tracks = snapshot.tracks
                if snapshot.collection.kind == "favorites" {
                    self.favoriteSnapshot = (snapshot.collection, Set(snapshot.tracks.map(\.ref))); self.favoritesLoaded = true
                } else { self.refreshFavorites() }
                if let repository = self.model.service { await self.refreshSongProjections(repository) }
            } catch {
                guard !Task.isCancelled, self.visible, !self.model.isClosed, self.presentationID == own else { return }
                let safe = MusicServiceError.sanitized(error)
                if safe == .notFound { self.invalidate(); self.isMissing = true }
                else {
                    if safe.isAuthenticationFailure || [.accessUnavailable, .staleAccess].contains(safe) {
                        self.collection = nil; self.tracks = []
                    }
                    self.error = safe; self.needsReload = true
                }
            }
        }
        reads[own] = task
        return task
    }

    @discardableResult public func refreshFavorites() -> Task<Void, Never>? {
        guard visible, !model.isClosed, collection != nil else { return nil }
        let own = presentationID
        if let previous = favoriteReadID { reads[previous]?.cancel() }
        favoriteReadID = nil; favoritesLoading = false; favoritesLoaded = false
        favoriteSnapshot = nil; favoritesError = nil
        guard let target = model.collections.first(where: { $0.kind == "favorites" }) else {
            favoritesError = .notFound; return nil
        }
        if target == collection {
            favoriteSnapshot = (target, Set(tracks.map(\.ref))); favoritesLoaded = true
            return nil
        }
        let id = UUID(); favoriteReadID = id; favoritesLoading = true
        let task = Task { [weak self] in
            guard let self else { return }
            defer {
                self.reads.removeValue(forKey: id)
                if self.favoriteReadID == id { self.favoriteReadID = nil; self.favoritesLoading = false }
            }
            do {
                let refs = try await self.model.readCollectionReferences(target)
                guard !Task.isCancelled, self.favoriteReadID == id, self.presentationID == own, !self.model.isClosed else { return }
                self.favoriteSnapshot = (target, Set(refs)); self.favoritesLoaded = true
            } catch {
                guard !Task.isCancelled, self.favoriteReadID == id, self.presentationID == own, !self.model.isClosed else { return }
                self.favoritesError = MusicServiceError.sanitized(error)
            }
        }
        reads[id] = task
        return task
    }
    public func isFavorite(_ ref: MusicReference) -> Bool { favoriteSnapshot?.refs.contains(ref) == true }

    @discardableResult public func toggleFavorite(_ ref: MusicReference, presentationID: UUID) -> Task<Void, Never>? {
        guard favoritesLoaded, tracks.contains(where: { $0.ref == ref }), let snapshot = favoriteSnapshot else { return nil }
        let removing = snapshot.refs.contains(ref)
        return run(presentationID: presentationID, mutation: true, reloadAfterMutation: false) { model in
            try await model.changeCollectionPageMembership(snapshot.collection,
                adding: removing ? [] : [ref], removing: removing ? [ref] : [])
        }
    }
    public func beginEditing(presentationID: UUID) -> MusicCollectionEditorDraft? {
        guard canEdit, self.presentationID == presentationID, let collection else { return nil }
        editor?.revoked = true
        let draft = MusicCollectionEditorDraft(collection: collection, presentationID: presentationID, tracks: tracks)
        editor = draft
        return draft
    }
    public func discardEditing(_ draft: MusicCollectionEditorDraft) {
        guard editor === draft, !isActing else { return }
        draft.revoked = true; editor = nil
    }
    @discardableResult public func saveEditing(_ draft: MusicCollectionEditorDraft) -> Task<Void, Never>? {
        guard editor === draft, draft.canEdit, canAct else { return nil }
        guard draft.hasChanges else { discardEditing(draft); return nil }
        draft.consumed = true
        let refs = draft.tracks.map(\.ref), order = draft.tracks.map(\.songId)
        return run(presentationID: draft.presentationID, mutation: true) { model in
            try await model.editCollectionPage(draft.collection, name: nil, refs: refs, order: order)
        }
    }
    @discardableResult public func enqueueSelected(_ draft: MusicCollectionEditorDraft) -> Task<Void, Never>? {
        guard editor === draft, !draft.selectedTracks.isEmpty else { return nil }
        let refs = draft.selectedTracks.map(\.ref)
        return run(presentationID: presentationID, mutation: false, reportsModelError: true) { model in
            await model.queueAction(.append(refs, next: true), startPlayback: false)
        }
    }
    @discardableResult public func addSelected(_ draft: MusicCollectionEditorDraft, to target: MusicCollection) -> Task<Void, Never>? {
        guard editor === draft else { return nil }
        return add(draft.selectedTracks.map(\.ref), to: target, presentationID: presentationID)
    }
    @discardableResult public func add(_ refs: [MusicReference], to target: MusicCollection,
                                      presentationID: UUID) -> Task<Void, Never>? {
        guard !refs.isEmpty, refs.allSatisfy({ ref in tracks.contains { $0.ref == ref } }),
              ["playlist", "favorites"].contains(target.kind) else { return nil }
        return run(presentationID: presentationID, mutation: true, reloadAfterMutation: false) { model in
            try await model.changeCollectionPageMembership(target, adding: refs, removing: [])
        }
    }
    @discardableResult public func playFiltered(at index: Int = 0, presentationID: UUID,
                                               filterID: UUID, toggleCurrent: Bool = false) -> Task<Void, Never>? {
        let filtered = filteredTracks
        guard self.filterID == filterID, filtered.indices.contains(index), let col = collection else { return nil }
        let toggle = toggleCurrent && isCurrentCollection
        let change: MusicQueueChange = searchQuery.isEmpty
            ? .collection(id: col.id, revision: col.revision, index: index)
            : .replace(filtered.map(\.ref), index: index)
        return run(presentationID: presentationID, mutation: false, reportsModelError: true, filterSensitive: true) { model in
            if toggle { await model.togglePlayback() }
            else { await model.queueAction(change, startPlayback: true, collection: col) }
        }
    }
    @discardableResult public func removeCollection(presentationID: UUID) -> Task<Void, Never>? {
        guard canRename, let col = collection else { return nil }
        return run(presentationID: presentationID, mutation: true, reportsModelError: true) { model in
            await model.removePlaylist(col)
        }
    }
    @discardableResult public func rename(_ name: String, collection target: MusicCollection, presentationID: UUID) -> Task<Void, Never>? {
        guard target.kind != "favorites" else { return nil }
        return run(presentationID: presentationID, mutation: true) { model in
            try await model.editCollectionPage(target, name: name, refs: nil, order: nil)
        }
    }
    @discardableResult public func deleteMembers(at offsets: IndexSet, presentationID: UUID) -> Task<Void, Never>? {
        guard !offsets.isEmpty, offsets.allSatisfy(tracks.indices.contains), let col = collection else { return nil }
        let refs = tracks.enumerated().filter { !offsets.contains($0.offset) }.map(\.element.ref)
        return run(presentationID: presentationID, mutation: true) { model in
            try await model.editCollectionPage(col, name: nil, refs: refs, order: nil)
        }
    }
    @discardableResult public func enqueue(at index: Int, next: Bool, presentationID: UUID) -> Task<Void, Never>? {
        guard tracks.indices.contains(index) else { return nil }
        let ref = tracks[index].ref
        return run(presentationID: presentationID, mutation: false, reportsModelError: true) { model in
            await model.queueAction(.append([ref], next: next), startPlayback: false)
        }
    }
    private func run(presentationID: UUID, mutation: Bool,
                     reloadAfterMutation: Bool = true, reportsModelError: Bool = false, filterSensitive: Bool = false,
                     operation: @escaping @MainActor (MusicViewModel) async throws -> Void) -> Task<Void, Never>? {
        guard canAct, action == nil, self.presentationID == presentationID else { return nil }
        let id = UUID()
        isActing = true; error = nil; actionError = nil
        if reportsModelError { model.error = nil }
        let task = Task { [weak self] in
            guard let self else { return }
            defer { if self.action?.id == id { self.action = nil; self.isActing = false } }
            do {
                try Task.checkCancellation()
                guard self.visible, !self.model.isClosed, self.presentationID == presentationID else { return }
                try await operation(self.model)
                guard !Task.isCancelled, self.visible, !self.model.isClosed, self.presentationID == presentationID else { return }
                if reportsModelError, let message = self.model.notices.error {
                    self.actionError = message
                    if mutation { self.needsReload = true }
                    return
                }
                if mutation, reloadAfterMutation { self.reload(preservingContent: true) }
            } catch {
                guard self.visible, !self.model.isClosed, self.presentationID == presentationID else { return }
                if Task.isCancelled {
                    if mutation { self.needsReload = true }
                    return
                }
                let safe = MusicServiceError.sanitized(error)
                if safe != .cancelled {
                    self.error = safe
                    if mutation { self.needsReload = true }
                }
            }
        }
        action = (id, task, filterSensitive)
        return task
    }
    deinit {
        songObservation?.cancel()
        for task in reads.values { task.cancel() }
        action?.task.cancel()
    }
}
#endif
