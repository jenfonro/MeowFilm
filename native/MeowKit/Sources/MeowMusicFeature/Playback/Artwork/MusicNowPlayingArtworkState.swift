// SPDX-License-Identifier: GPL-3.0-only
// iWebPlus b900c259 app-colors.js cover()/prime(): sample the actual display
// image, never initiate a second fetch for a palette. The native mount gate
// obtains its cover ticket BEFORE AsyncImage exists. Request identity separates
// old phases; preference changes re-sample the retained successful Image.
import Foundation
import MeowUI
#if os(iOS) || os(macOS)
import Combine

struct MusicNowPlayingArtworkRequest: Hashable {
    let identity: MusicAppearanceSongIdentity?
    let url: URL?
    var artworkID: String { url?.absoluteString ?? "" }

    init(track: MusicRemoteTrack?) {
        identity = track.map { MusicAppearanceSongIdentity(track: $0) }
        url = track?.artwork
    }
    static func == (lhs: Self, rhs: Self) -> Bool {
        lhs.identity == rhs.identity && lhs.artworkID.utf16.elementsEqual(rhs.artworkID.utf16)
    }
    func hash(into hasher: inout Hasher) {
        hasher.combine(identity.map { Data($0.stableKey.utf8) })
        hasher.combine(Data(artworkID.utf8))
    }
}

/// View-scoped admission state, not an image loader/cache. Unit-testable without
/// constructing a UI, requesting a URL or starting a player.
@MainActor final class MusicNowPlayingArtworkState: ObservableObject {
    @Published private(set) var isReady = false
    @Published private(set) var samplingRevision = UUID()
    private var active = false
    private var rendering = false
    private var sampled = false
    private var deliveredImage = false
    private var hasAdmittedArtworkImage = false
    private var capturedDisplay = false
    private var replacedImage = false
    private var request: MusicNowPlayingArtworkRequest?
    private weak var appearance: MusicAppearancePreferencesStore?
    private var ticket: MusicAppearanceCoverTicket?
    private var displayTicket: MusicDisplayedArtworkTicket?

    @discardableResult func activate(_ request: MusicNowPlayingArtworkRequest,
                                    appearance: MusicAppearancePreferencesStore?) -> Bool {
        guard !active || self.request != request || self.appearance !== appearance else { return false }
        active = true; rendering = true; self.request = request; self.appearance = appearance
        deliveredImage = false; hasAdmittedArtworkImage = false
        capturedDisplay = false; replacedImage = false
        sampled = false; ticket = nil; samplingRevision = UUID()
        appearance?.selectSong(request.identity) // Same identity is a store-level no-op.
        refreshTicket()
        refreshDisplayTicket()
        isReady = true // AsyncImage may mount only after the preceding admission.
        return true
    }

    func preferencesChanged() {
        guard active else { return }
        refreshTicket() // Does not change isReady/request or recreate AsyncImage.
    }

    func becameActive() {
        guard active else { return }
        let suspended = !rendering
        rendering = true
        let needsArtwork = displayTicket.map {
            appearance?.acceptsDisplayedArtwork($0) == true && appearance?.displayedArtwork?.ticket != $0
        } ?? false
        guard suspended || !sampled || !capturedDisplay || needsArtwork else { return }
        // ImageRenderer may have returned nil while the scene was backgrounded.
        // Retry only its retained successful Image, never its network request.
        samplingRevision = UUID()
    }

    func suspendRendering() {
        guard active, rendering else { return }
        rendering = false
        // Background completion cannot commit even if its View captured an
        // earlier scenePhase. The actual local state revokes that revision.
        samplingRevision = UUID()
    }
    func imageChanged() {
        guard active else { return }
        replacedImage = replacedImage || deliveredImage || hasAdmittedArtworkImage
        deliveredImage = false; sampled = false; capturedDisplay = false
        samplingRevision = UUID()
    }
    func artworkContextChanged() {
        guard active, hasAdmittedArtworkImage else { return }
        // Only a success delivered BEFORE the scope change can be re-used.
        // An in-flight old AsyncImage retains its original (revoked) ticket.
        refreshDisplayTicket()
        samplingRevision = UUID()
    }

    func deactivate() {
        active = false; rendering = false; ticket = nil; sampled = false; deliveredImage = false; isReady = false
        hasAdmittedArtworkImage = false
        displayTicket = nil
        samplingRevision = UUID()
        // Do not cancel the shared store's cover: another visible surface may
        // still own the same ticket. This view's callbacks are locally rejected.
    }

    @discardableResult func deliverImage(revision: UUID, deliver: () -> Void) -> Bool {
        guard active, rendering, isReady, revision == samplingRevision, !deliveredImage else { return false }
        deliveredImage = true
        deliver()
        return true
    }
    func needsDisplay(revision: UUID) -> Bool {
        active && rendering && isReady && revision == samplingRevision && !capturedDisplay
    }
    func needsArtwork(revision: UUID) -> Bool {
        guard active, rendering, isReady, revision == samplingRevision, let appearance, let displayTicket,
              appearance.acceptsDisplayedArtwork(displayTicket) else { return false }
        return replacedImage || appearance.displayedArtwork?.ticket != displayTicket
    }
    func needsPalette(revision: UUID) -> Bool {
        guard active, rendering, isReady, !sampled, revision == samplingRevision, let appearance, let ticket else { return false }
        return appearance.accepts(ticket)
    }
    @discardableResult func captureDisplay(revision: UUID, encode: () -> Data?, deliver: (Data) -> Void) -> Bool {
        guard active, rendering, isReady, revision == samplingRevision, !capturedDisplay,
              let data = encode(), !data.isEmpty, data.count <= 8 * 1024 * 1024 else { return false }
        capturedDisplay = true; deliver(data); return true
    }

    @discardableResult func sample(revision: UUID, pixels: () -> [UInt8]?) -> Bool {
        guard active, rendering, isReady, !sampled, revision == samplingRevision,
              let appearance, let ticket, appearance.accepts(ticket) else { return false }
        guard let rgba = pixels() else { return false }
        // No async suspension occurs between admission, render and commit.
        sampled = true
        return appearance.applyCover(rgba: rgba, ticket: ticket)
    }

    @discardableResult func captureArtwork(revision: UUID, encode: () -> Data?) -> Bool {
        guard active, rendering, isReady, revision == samplingRevision,
              let appearance, let displayTicket,
              appearance.acceptsDisplayedArtwork(displayTicket) else { return false }
        hasAdmittedArtworkImage = true
        // Mini and full player share the same ticket/cache, including fixed
        // palette mode. Do not re-encode on lyric/progress/preference updates.
        if !replacedImage, appearance.displayedArtwork?.ticket == displayTicket { return true }
        guard let data = encode() else { return false }
        let applied = appearance.applyDisplayedArtwork(data, ticket: displayTicket)
        if applied { replacedImage = false }
        return applied
    }

    private func refreshDisplayTicket() {
        if let request, let identity = request.identity, request.url != nil {
            displayTicket = appearance?.beginDisplayedArtwork(for: identity, artworkID: request.artworkID)
        } else { displayTicket = nil }
    }

    private func refreshTicket() {
        let next: MusicAppearanceCoverTicket?
        if let request, let identity = request.identity, request.url != nil {
            next = appearance?.beginCover(for: identity, artworkID: request.artworkID)
        } else { next = nil }
        guard ticket != next else { return }
        ticket = next; sampled = false; samplingRevision = UUID()
    }
}
#endif
