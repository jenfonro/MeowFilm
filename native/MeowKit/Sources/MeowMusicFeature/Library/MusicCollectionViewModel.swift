#if os(iOS) || os(macOS)
import Foundation
import Combine

/// iWebPlus b900c259: playlist-editor.js keeps selection/order/deletions in a
/// session, then writes songs + order + ifRevision once when Done is pressed.
/// This is presentation state, not another library or persistence service.
@MainActor public final class MusicCollectionEditorDraft: ObservableObject, Identifiable {
    public typealias ID = UUID
    public let id = UUID()
    public let title: String
    @Published public private(set) var tracks: [MusicRemoteTrack]
    @Published public private(set) var selected: Set<MusicReference> = []
    fileprivate let ticket: MusicCollectionEditTicket
    fileprivate let original: [MusicRemoteTrack]

    fileprivate init(ticket: MusicCollectionEditTicket, tracks: [MusicRemoteTrack]) {
        self.ticket = ticket; self.tracks = tracks; original = tracks
        title = ticket.collection.title
    }
    public var hasChanges: Bool { tracks.map(\.ref) != original.map(\.ref) }
    public var allSelected: Bool { !tracks.isEmpty && tracks.allSatisfy { selected.contains($0.ref) } }
    public var selectedTracks: [MusicRemoteTrack] { tracks.filter { selected.contains($0.ref) } }
    public func toggle(_ ref: MusicReference) {
        guard !ticket.revoked, !ticket.consumed, tracks.contains(where: { $0.ref == ref }) else { return }
        if !selected.insert(ref).inserted { selected.remove(ref) }
    }
    public func toggleAll() {
        guard !ticket.revoked, !ticket.consumed else { return }
        selected = allSelected ? [] : Set(tracks.map(\.ref))
    }
    public func deleteSelected() {
        guard !ticket.revoked, !ticket.consumed else { return }
        tracks.removeAll { selected.contains($0.ref) }; selected = []
    }
    public func remove(at offsets: IndexSet) {
        guard !ticket.revoked, !ticket.consumed, !offsets.isEmpty,
              offsets.allSatisfy(tracks.indices.contains) else { return }
        for index in offsets { selected.remove(tracks[index].ref) }
        tracks = tracks.enumerated().filter { !offsets.contains($0.offset) }.map(\.element)
    }
    public func move(from offsets: IndexSet, to destination: Int) {
        guard !ticket.revoked, !ticket.consumed, !offsets.isEmpty,
              offsets.allSatisfy(tracks.indices.contains), (0...tracks.count).contains(destination) else { return }
        let moving = offsets.map { tracks[$0] }
        var remaining = tracks.enumerated().filter { !offsets.contains($0.offset) }.map(\.element)
        remaining.insert(contentsOf: moving, at: destination - offsets.filter { $0 < destination }.count)
        tracks = remaining
    }
}

/// A page admission, never a replacement for the native writer's access/CAS
/// checks. Revoking it synchronously cancels the tasks using that admission.
@MainActor final class MusicCollectionPageBinding {
    weak var owner: MusicViewModel?
    let generation: UUID
    let id = UUID()
    var active = true

    init(owner: MusicViewModel) {
        self.owner = owner; generation = owner.accountAccessGeneration
    }
    func matches(_ owner: MusicViewModel) -> Bool {
        active && self.owner === owner && owner.isAvailable && !owner.isClosed &&
        owner.accountAccessGeneration == generation
    }
    func check(_ owner: MusicViewModel) throws {
        try Task.checkCancellation()
        guard matches(owner) else { throw MusicServiceError.staleAccess }
    }
}

/// Copied at the UI event, including when a rename confirmation is opened.
/// No public initializer: a different page cannot mint or upgrade a ticket.
@MainActor public final class MusicCollectionEditTicket {
    let binding: MusicCollectionPageBinding
    let collection: MusicCollection
    var consumed = false
    var revoked = false
    init(binding: MusicCollectionPageBinding, collection: MusicCollection) {
        self.binding = binding; self.collection = collection
    }
    func check(_ owner: MusicViewModel) throws {
        guard !revoked else { throw MusicServiceError.staleResult }
        try binding.check(owner)
    }
}

struct MusicCollectionPageSnapshot: Sendable {
    let collection: MusicCollection
    let tracks: [MusicRemoteTrack]
}

enum MusicCollectionOwnerChange {
    case revoking
    case access
    case catalogue([MusicCollection])
}

@MainActor final class WeakMusicCollectionConsumer {
    weak var value: MusicCollectionViewModel?
    init(_ value: MusicCollectionViewModel) { self.value = value }
}

/// Owns only one local collection presentation. It neither owns storage nor
/// creates a second queue/player. Reads never fall back to navigation metadata.
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
            // Changing the filter is a newer playback intent, not permission
            // for a queued callback to play a different row at the same index.
            if action?.filterSensitive == true { action?.task.cancel() }
        }
    }
    @Published public private(set) var editor: MusicCollectionEditorDraft?
    @Published public private(set) var favoritesLoaded = false
    @Published public private(set) var favoritesLoading = false
    @Published public private(set) var favoritesError: MusicServiceError?
    @Published public private(set) var actionError: String?
    @Published public private(set) var filterID = UUID()
    public var filteredTracks: [MusicRemoteTrack] {
        Self.filter(tracks, query: searchQuery)
    }
    /// Matches browse.js filteredSongs: trim, locale lower-case, substring in
    /// each field separately. No accent folding, token OR or title-only search.
    public static func filter(_ tracks: [MusicRemoteTrack], query: String) -> [MusicRemoteTrack] {
        // JS trim includes BOM, but not NEL; Foundation's whitespace set differs.
        let whitespace = CharacterSet(charactersIn: "\u{0009}\u{000A}\u{000B}\u{000C}\u{000D}\u{0020}\u{00A0}\u{1680}\u{2000}\u{2001}\u{2002}\u{2003}\u{2004}\u{2005}\u{2006}\u{2007}\u{2008}\u{2009}\u{200A}\u{2028}\u{2029}\u{202F}\u{205F}\u{3000}\u{FEFF}")
        let needle = query.trimmingCharacters(in: whitespace).lowercased(with: .current)
        guard !needle.isEmpty else { return tracks }
        return tracks.filter { track in
            [track.title, track.artist, track.album].contains {
                // NSString literal search, like JS includes, does not merge
                // composed/decomposed spellings or whole grapheme clusters.
                ($0.lowercased(with: .current) as NSString).range(of: needle, options: .literal).location != NSNotFound
            }
        }
    }
    public var canEdit: Bool { canAct && ["playlist", "favorites"].contains(collection?.kind ?? "") }
    public var canRename: Bool { canAct && collection?.kind == "playlist" }
    public var isCurrentCollection: Bool {
        guard searchQuery.isEmpty, isAvailable, model.current != nil else { return false }
        if let currentQueueCollectionID {
            return currentQueueCollectionID()?.utf8.elementsEqual(collectionID.utf8) == true
        }
        guard let receipt = collectionQueue,
              receipt.generation == model.accountAccessGeneration,
              receipt.entries == model.queue.map(\.id), !receipt.entries.isEmpty else { return false }
        return true
    }
    public var isPlayingCollection: Bool {
        isCurrentCollection && (model.phase == .playing || model.phase == .buffering)
    }
    public var playAllTitle: String {
        isPlayingCollection ? "暂停播放" : isCurrentCollection ? "继续播放" : "播放全部"
    }
    public var isAvailable: Bool { model.isAvailable && !model.isClosed }
    public var canAct: Bool {
        visible && isAvailable && !isMissing && !isLoading && !isActing &&
        !needsReload && collection != nil && binding?.matches(model) == true
    }

    /// Source inspection uses this local page's stable library reference, not
    /// a public-page handle refresh.
    /// A captured menu cannot silently target a successor page/recording.
    public func sourceTrack(_ track: MusicRemoteTrack, presentationID: UUID) throws -> MusicRemoteTrack {
        try Task.checkCancellation()
        guard self.presentationID == presentationID, canAct, editor == nil,
              let displayed = tracks.first(where: { $0.ref == track.ref }),
              displayed.songId.utf8.elementsEqual(track.songId.utf8),
              displayed.sources == track.sources else { throw MusicServiceError.staleResult }
        return displayed
    }

    private let model: MusicViewModel
    private let currentQueueCollectionID: (@MainActor () -> String?)?
    private let observerID = UUID()
    private var visible = false
    private var needsReload = false
    private var binding: MusicCollectionPageBinding?
    private var renameConfirmation: MusicCollectionEditTicket?
    // Cancelled, non-cooperative reads still occupy their slots until they
    // return. Repeated navigation/retry cannot create an unbounded task tail.
    private var reads: [UUID: Task<Void, Never>] = [:]
    private var action: (id: UUID, task: Task<Void, Never>, filterSensitive: Bool)?
    private var favoriteSnapshot: MusicCollectionPageSnapshot?
    private var favoriteReadID: UUID?
    // Receipt of this page's successful unfiltered queue transaction. Never
    // infer source identity from matching song titles or another list's tracks.
    @Published private var collectionQueue: (generation: UUID, entries: [String])?

    /// Supply the queue owner's authoritative local collection ID when it is
    /// exposed by product composition. nil provider means only this page's
    /// exact queue receipt is known; a provider returning nil means no source.
    /// No source provenance is guessed from another playlist's identical songs.
    public init(model: MusicViewModel, collectionID: String,
                currentQueueCollectionID: (@MainActor () -> String?)? = nil) {
        self.model = model; self.collectionID = collectionID
        self.currentQueueCollectionID = currentQueueCollectionID
    }

    @discardableResult public func appear() -> Task<Void, Never>? {
        guard !visible else { return nil }
        visible = true; model.observeCollectionPage(self, id: observerID)
        return reload()
    }
    public func disappear() {
        visible = false; model.removeCollectionPage(id: observerID)
        invalidate(); searchQuery = ""
    }

    /// The detail/editor share one page scope. Backgrounding cancels work
    /// without discarding the user's unsaved draft or changing storage identity.
    public func pauseCommands() {
        for task in reads.values { task.cancel() }
        action?.task.cancel()
    }
    private func invalidate() {
        renameConfirmation?.revoked = true; renameConfirmation = nil
        editor?.ticket.revoked = true; editor = nil
        binding?.active = false; binding = nil
        for task in reads.values { task.cancel() }
        action?.task.cancel()
        favoriteReadID = nil; favoriteSnapshot = nil
        favoritesLoaded = false; favoritesLoading = false; favoritesError = nil; actionError = nil
        presentationID = UUID()
        collection = nil; tracks = []; error = nil
        isLoading = false; isActing = action != nil; needsReload = false
    }
    func ownerChanged(_ change: MusicCollectionOwnerChange, origin: UUID? = nil) {
        guard visible else { return }
        if let origin, binding?.id == origin { return }
        switch change {
        case .revoking:
            invalidate(); searchQuery = ""; collectionQueue = nil
        case .access:
            invalidate()
            if isAvailable && !isMissing { reload() }
        case .catalogue(let rows):
            guard isAvailable, !isMissing else { return }
            guard let next = rows.first(where: { $0.id.utf8.elementsEqual(collectionID.utf8) }) else {
                invalidate(); isMissing = true; return
            }
            if let collection, collection == next {
                let favorite = rows.first { $0.kind == "favorites" }
                if favoriteSnapshot?.collection != favorite { refreshFavorites() }
                return
            }
            // A background catalogue change must not silently throw away an
            // unsaved editor. Revoke its admission and retain the draft/error
            // until the user discards it or explicitly reloads. Our own Done
            // transaction has consumed its ticket and takes the normal reread.
            if let draft = editor, !draft.ticket.consumed {
                draft.ticket.revoked = true; binding?.active = false
                for task in reads.values { task.cancel() }
                action?.task.cancel()
                error = .staleRevision; needsReload = true; isLoading = false
                return
            }
            reload()
        }
    }
    @discardableResult public func reload(preservingContent: Bool = false) -> Task<Void, Never>? {
        let retained: MusicCollectionPageSnapshot?
        if preservingContent, visible, isAvailable, !isMissing, editor == nil,
           binding?.matches(model) == true, let collection {
            retained = .init(collection: collection, tracks: tracks)
        } else { retained = nil }
        invalidate()
        guard visible, isAvailable, !isMissing else { return nil }
        // browse.js refreshDetail retains the same owner's content during a
        // refresh. Retained data is display-only until the fresh read succeeds.
        if let retained { collection = retained.collection; tracks = retained.tracks }
        guard reads.count < 2 else { error = .busy; needsReload = true; return nil }
        let own = MusicCollectionPageBinding(owner: model)
        binding = own; presentationID = own.id; isLoading = true
        let task = Task { [weak self] in
            guard let self else { return }
            defer {
                self.reads.removeValue(forKey: own.id)
                if self.binding === own { self.isLoading = false }
            }
            do {
                let snapshot = try await self.model.readCollectionPage(id: self.collectionID, binding: own)
                try own.check(self.model)
                guard self.visible, self.binding === own else { return }
                guard let snapshot else {
                    self.invalidate(); self.isMissing = true; return
                }
                self.collection = snapshot.collection; self.tracks = snapshot.tracks
                if snapshot.collection.kind == "favorites" {
                    self.favoriteSnapshot = snapshot; self.favoritesLoaded = true
                } else { self.refreshFavorites() }
            } catch {
                guard own.matches(self.model), self.visible, self.binding === own else { return }
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
        reads[own.id] = task
        return task
    }

    @discardableResult public func refreshFavorites() -> Task<Void, Never>? {
        guard visible, let own = binding, own.matches(model), collection != nil else { return nil }
        if let previous = favoriteReadID { reads[previous]?.cancel() }
        favoriteReadID = nil; favoritesLoading = false; favoritesLoaded = false
        favoriteSnapshot = nil; favoritesError = nil
        guard let target = model.collections.first(where: { $0.kind == "favorites" }) else {
            favoritesError = .notFound; return nil
        }
        if target == collection {
            favoriteSnapshot = .init(collection: target, tracks: tracks); favoritesLoaded = true
            return nil
        }
        guard reads.count < 2 else { favoritesError = .busy; return nil }
        let id = UUID(); favoriteReadID = id; favoritesLoading = true
        let task = Task { [weak self] in
            guard let self else { return }
            defer {
                self.reads.removeValue(forKey: id)
                if self.favoriteReadID == id { self.favoriteReadID = nil; self.favoritesLoading = false }
            }
            do {
                guard let snapshot = try await self.model.readCollectionPage(id: target.id, binding: own),
                      snapshot.collection.kind == "favorites" else { throw MusicServiceError.notFound }
                try own.check(self.model)
                guard self.favoriteReadID == id, self.binding === own else { return }
                self.favoriteSnapshot = snapshot; self.favoritesLoaded = true
            } catch {
                guard self.favoriteReadID == id, own.matches(self.model), self.binding === own else { return }
                self.favoritesError = MusicServiceError.sanitized(error)
            }
        }
        reads[id] = task
        return task
    }
    public func isFavorite(_ ref: MusicReference) -> Bool {
        favoriteSnapshot?.tracks.contains(where: { $0.ref == ref }) == true
    }
    @discardableResult public func toggleFavorite(_ ref: MusicReference, presentationID: UUID) -> Task<Void, Never>? {
        guard favoritesLoaded, tracks.contains(where: { $0.ref == ref }), let snapshot = favoriteSnapshot,
              let ticket = ticket(presentationID: presentationID) else { return nil }
        var refs = snapshot.tracks.map(\.ref)
        if refs.contains(ref) { refs.removeAll { $0 == ref } } else { refs.append(ref) }
        let favoriteTicket = MusicCollectionEditTicket(binding: ticket.binding, collection: snapshot.collection)
        return run(ticket, mutation: true, reloadAfterMutation: false) { model in
            try await model.editCollectionPage(favoriteTicket, name: nil, refs: refs, order: nil)
        }
    }
    public func beginEditing(presentationID: UUID) -> MusicCollectionEditorDraft? {
        guard canEdit, let ticket = ticket(presentationID: presentationID) else { return nil }
        editor?.ticket.revoked = true
        let draft = MusicCollectionEditorDraft(ticket: ticket, tracks: tracks)
        editor = draft
        return draft
    }
    public func discardEditing(_ draft: MusicCollectionEditorDraft) {
        guard editor === draft, !isActing else { return }
        draft.ticket.revoked = true; editor = nil
    }
    @discardableResult public func saveEditing(_ draft: MusicCollectionEditorDraft) -> Task<Void, Never>? {
        guard editor === draft, canAct else { return nil }
        guard draft.hasChanges else { discardEditing(draft); return nil }
        // Both the member set and its order are one conditional native write.
        let refs = draft.tracks.map(\.ref), order = draft.tracks.map(\.songId)
        return run(draft.ticket, mutation: true) { model in
            try await model.editCollectionPage(draft.ticket, name: nil, refs: refs, order: order)
        }
    }
    @discardableResult public func enqueueSelected(_ draft: MusicCollectionEditorDraft) -> Task<Void, Never>? {
        guard editor === draft, !draft.selectedTracks.isEmpty,
              let ticket = ticket(presentationID: presentationID) else { return nil }
        let refs = draft.selectedTracks.map(\.ref)
        return run(ticket, mutation: false, reportsModelError: true) { model in
            try await model.queueCollectionPage(ticket, change: .append(refs, next: true), startPlayback: false)
        }
    }
    @discardableResult public func addSelected(_ draft: MusicCollectionEditorDraft, to target: MusicCollection) -> Task<Void, Never>? {
        guard editor === draft else { return nil }
        return add(draft.selectedTracks.map(\.ref), to: target, presentationID: presentationID)
    }
    @discardableResult public func add(_ refs: [MusicReference], to target: MusicCollection,
                                      presentationID: UUID) -> Task<Void, Never>? {
        guard !refs.isEmpty, refs.allSatisfy({ ref in tracks.contains { $0.ref == ref } }),
              ["playlist", "favorites"].contains(target.kind),
              let ticket = ticket(presentationID: presentationID) else { return nil }
        return run(ticket, mutation: true, reloadAfterMutation: false) { model in
            guard let snapshot = try await model.readCollectionPage(id: target.id, binding: ticket.binding) else {
                throw MusicServiceError.notFound
            }
            try ticket.check(model)
            // A menu copied an explicit destination revision. Never silently
            // upgrade that admission to a successor collection.
            guard snapshot.collection == target else { throw MusicServiceError.staleRevision }
            var seen = Set(snapshot.tracks.map(\.ref))
            let merged = snapshot.tracks.map(\.ref) + refs.filter { seen.insert($0).inserted }
            guard merged != snapshot.tracks.map(\.ref) else { return }
            let targetTicket = MusicCollectionEditTicket(binding: ticket.binding, collection: target)
            try await model.editCollectionPage(targetTicket, name: nil, refs: merged, order: nil)
        }
    }
    @discardableResult public func playFiltered(at index: Int = 0, presentationID: UUID,
                                               filterID: UUID, toggleCurrent: Bool = false) -> Task<Void, Never>? {
        let filtered = filteredTracks
        guard self.filterID == filterID, filtered.indices.contains(index),
              let ticket = ticket(presentationID: presentationID) else { return nil }
        let unfiltered = searchQuery.isEmpty
        let toggle = toggleCurrent && isCurrentCollection
        let change: MusicQueueChange = unfiltered
            ? .collection(id: ticket.collection.id, revision: ticket.collection.revision, index: index)
            : .replace(filtered.map(\.ref), index: index)
        let previousRevision = model.state.revision
        return run(ticket, mutation: false, reportsModelError: true, filterSensitive: true) { [weak self] model in
            if toggle { await model.togglePlayback() }
            else { try await model.queueCollectionPage(ticket, change: change, startPlayback: true) }
            try ticket.check(model)
            guard let self, self.filterID == filterID else { return }
            if !toggle, unfiltered, model.state.revision != previousRevision,
               model.queue.map({ Data($0.track.songId.utf8) }) == filtered.map({ Data($0.songId.utf8) }) {
                self.collectionQueue = (model.accountAccessGeneration, model.queue.map(\.id))
            } else if !toggle { self.collectionQueue = nil }
        }
    }
    @discardableResult public func removeCollection(presentationID: UUID) -> Task<Void, Never>? {
        guard canRename, let ticket = ticket(presentationID: presentationID) else { return nil }
        return run(ticket, mutation: true, reportsModelError: true) { model in
            try ticket.check(model)
            await model.removePlaylist(ticket.collection)
        }
    }

    private func ticket(presentationID: UUID) -> MusicCollectionEditTicket? {
        guard self.presentationID == presentationID, canAct, let binding, let collection else { return nil }
        return .init(binding: binding, collection: collection)
    }
    public func requestRename(presentationID: UUID) -> MusicCollectionEditTicket? {
        guard collection?.kind != "favorites", let next = ticket(presentationID: presentationID) else { return nil }
        renameConfirmation?.revoked = true; renameConfirmation = next
        return next
    }
    public func cancelRename(_ ticket: MusicCollectionEditTicket) {
        ticket.revoked = true
        if renameConfirmation === ticket { renameConfirmation = nil }
    }
    @discardableResult public func rename(_ name: String, ticket: MusicCollectionEditTicket) -> Task<Void, Never>? {
        guard renameConfirmation === ticket else { return nil }
        renameConfirmation = nil
        return run(ticket, mutation: true) { model in
            try await model.editCollectionPage(ticket, name: name, refs: nil, order: nil)
        }
    }
    @discardableResult public func deleteMembers(at offsets: IndexSet, presentationID: UUID) -> Task<Void, Never>? {
        guard !offsets.isEmpty, offsets.allSatisfy(tracks.indices.contains),
              let ticket = ticket(presentationID: presentationID) else { return nil }
        let refs = tracks.enumerated().filter { !offsets.contains($0.offset) }.map(\.element.ref)
        return run(ticket, mutation: true) { model in
            try await model.editCollectionPage(ticket, name: nil, refs: refs, order: nil)
        }
    }
    @discardableResult public func moveMembers(from offsets: IndexSet, to destination: Int, presentationID: UUID) -> Task<Void, Never>? {
        guard !offsets.isEmpty, offsets.allSatisfy(tracks.indices.contains),
              (0...tracks.count).contains(destination), let ticket = ticket(presentationID: presentationID) else { return nil }
        let moved = offsets.map { tracks[$0].songId }
        var order = tracks.enumerated().filter { !offsets.contains($0.offset) }.map(\.element.songId)
        order.insert(contentsOf: moved, at: destination - offsets.filter { $0 < destination }.count)
        return run(ticket, mutation: true) { model in
            try await model.editCollectionPage(ticket, name: nil, refs: nil, order: order)
        }
    }
    @discardableResult public func play(at index: Int, presentationID: UUID) -> Task<Void, Never>? {
        guard tracks.indices.contains(index), let ticket = ticket(presentationID: presentationID) else { return nil }
        return run(ticket, mutation: false, reportsModelError: true) { model in
            try await model.queueCollectionPage(ticket, change: .collection(id: ticket.collection.id,
                revision: ticket.collection.revision, index: index), startPlayback: true)
        }
    }
    @discardableResult public func enqueue(at index: Int, next: Bool, presentationID: UUID) -> Task<Void, Never>? {
        guard tracks.indices.contains(index), let ticket = ticket(presentationID: presentationID) else { return nil }
        let ref = tracks[index].ref
        return run(ticket, mutation: false, reportsModelError: true) { model in
            try await model.queueCollectionPage(ticket, change: .append([ref], next: next), startPlayback: false)
        }
    }
    @discardableResult public func add(at index: Int, to target: MusicCollection, presentationID: UUID) -> Task<Void, Never>? {
        guard tracks.indices.contains(index), let ticket = ticket(presentationID: presentationID) else { return nil }
        let ref = tracks[index].ref
        return run(ticket, mutation: true) { model in
            try await model.addCollectionPage(ticket, ref: ref, to: target)
        }
    }
    private func run(_ ticket: MusicCollectionEditTicket, mutation: Bool,
                     reloadAfterMutation: Bool = true, reportsModelError: Bool = false, filterSensitive: Bool = false,
                     operation: @escaping @MainActor (MusicViewModel) async throws -> Void) -> Task<Void, Never>? {
        guard canAct, action == nil, ticket.binding === binding, !ticket.consumed, !ticket.revoked else { return nil }
        ticket.consumed = true
        let id = UUID(), own = ticket.binding
        isActing = true; error = nil; actionError = nil
        if reportsModelError { model.error = nil }
        let task = Task { [weak self] in
            guard let self else { return }
            defer {
                if self.action?.id == id { self.action = nil; self.isActing = false }
            }
            do {
                try ticket.check(self.model)
                try await operation(self.model)
                try ticket.check(self.model)
                if reportsModelError, let message = self.model.error {
                    self.actionError = message
                    if mutation { self.needsReload = true }
                    return
                }
                // Source mutations normally publish a catalogue and reread.
                // A batch add/favorite on another collection keeps the open
                // source editor; catalogue events refresh its favorite state.
                if mutation, reloadAfterMutation, self.binding === own { self.reload() }
            } catch {
                guard own.matches(self.model), self.visible, self.binding === own else { return }
                if Task.isCancelled {
                    if mutation { self.error = .commitUnconfirmed; self.needsReload = true }
                    return
                }
                let safe = MusicServiceError.sanitized(error)
                if safe != .cancelled {
                    self.error = safe
                    // An ambiguous commit is read back only, never replayed.
                    if mutation { self.needsReload = true }
                }
            }
        }
        action = (id, task, filterSensitive)
        return task
    }
}
#endif
