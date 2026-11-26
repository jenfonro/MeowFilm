import Combine
import Foundation
import MeowContracts
import MeowDebug
import MeowPlayback
import MeowRuntime
#if os(iOS)
import UIKit
#endif

public enum FilmVideoScale: String, CaseIterable, Sendable {
    case fit, fill, stretch
    public var title: String {
        switch self {
        case .fit: "原始比例"
        case .fill: "填满"
        case .stretch: "拉伸"
        }
    }
}

public struct FilmMediaState: Equatable, Sendable {
    public var sessionID: UUID?
    public var attemptID: String?
    public var resourceID: String?
    public var engine: PlaybackVideoEngine = .native
    public var chapters: [PlaybackVideoChapter] = []
    public var tracks: [PlaybackVideoTrack] = []
    public var position: Double = 0
    public var duration: Double = 0
    public var bufferedPosition: Double = 0
    public var videoWidth: Int = 0
    public var videoHeight: Int = 0
    public var canSeek = false
    public var paused = true
    public var buffering = false
    public var rate: Double = 1
    public var volume: Double = 1
    public var pictureInPicture = false
    public var pictureInPictureAvailable = false
    public var pictureInPictureState: String = "unavailable"
    public var diagnostics: PlaybackVideoDiagnostics?
    public init() {}
}

public enum FilmMediaEvent: Sendable {
    case state(FilmMediaState)
    case started(attemptID: String)
    case failed(attemptID: String, error: FilmPlaybackError = .init(code: "MEDIA_UNAVAILABLE"))
    case ended(attemptID: String)
    case ownerEnded(attemptID: String)
    case systemOwnerEnded(attemptID: String)
}

/// Concrete film media adapter owning one stable `PlaybackVideoLease` on `PlaybackController`.
@MainActor
public final class FilmPlaybackMedia {
    private var undrained: [Media] = []
    public var currentModel: FilmPlaybackModel? {
        guard let current, attempt(current.sessionID) != nil else { return nil }
        return current.model
    }
    public var models: [FilmPlaybackModel] { (undrained + [pending, current].compactMap { $0 }).map(\.model) }
    public var restoreUI: (@MainActor (UUID) async -> Bool)?
    private let playback: PlaybackController
    private let preferences: FilmPlayerPreferencesStore
    private var preferenceObservation: AnyCancellable?
    private var lease: PlaybackVideoLease!
    private var pending: Media?
    private var current: Media?
    private var stoppingSessions: [UUID: Int] = [:]

    private final class Media {
        let sessionID: UUID
        let attemptID: String
        let receive: @MainActor (FilmMediaEvent) -> Void
        let model: FilmPlaybackModel
        var nativeAttempt: PlayAttempt?
        var state = FilmMediaState()
        init(input: FilmPlaybackMediaInput, model: FilmPlaybackModel,
             receive: @escaping @MainActor (FilmMediaEvent) -> Void) {
            sessionID = input.sessionID; attemptID = input.attemptID; self.receive = receive
            self.model = model
            state.sessionID = input.sessionID; state.attemptID = input.attemptID
            state.resourceID = input.resourceID
            state.position = input.position; state.paused = input.paused
        }
    }

    @MainActor
    public final class Session {
        public weak var model: FilmPlaybackModel?
        let owner: FilmPlaybackMedia
        public init(owner: FilmPlaybackMedia) { self.owner = owner }
        func play(_ input: FilmPlaybackMediaInput, preservePosition: Bool,
                  waitForCommit: @escaping @MainActor () async throws -> Void,
                  canCommit: @escaping @MainActor () -> Bool,
                  release: @escaping @MainActor () async throws -> Void,
                  receive: @escaping @MainActor (FilmMediaEvent) -> Void) async throws {
            guard let model, model.sessionID == input.sessionID else { throw FilmPlaybackError(code: "STALE_SESSION") }
            try await owner.play(input, model: model, preservePosition: preservePosition,
                                 waitForCommit: waitForCommit, canCommit: canCommit, release: release, receive: receive)
        }
        func cancelPending(sessionID: UUID) async throws { try await owner.cancelPending(sessionID: sessionID) }
        func stop(sessionID: UUID) async throws { try await owner.stop(sessionID: sessionID) }
        func isLocallyDrained(sessionID: UUID) -> Bool { owner.isLocallyDrained(sessionID: sessionID) }
        func isCurrent(sessionID: UUID, attemptID: String) -> Bool { owner.isCurrent(sessionID: sessionID, attemptID: attemptID) }
        func pause(sessionID: UUID, attemptID: String) -> Bool { owner.pause(sessionID: sessionID, attemptID: attemptID) }
        func resume(sessionID: UUID, attemptID: String, userInitiated: Bool) {
            owner.resume(sessionID: sessionID, attemptID: attemptID, userInitiated: userInitiated)
        }
        func seek(sessionID: UUID, to position: Double) { owner.seek(sessionID: sessionID, to: position) }
        func setRate(sessionID: UUID, rate: Double) { owner.setRate(sessionID: sessionID, rate: rate) }
        func setTemporaryRate(sessionID: UUID, attemptID: String, rate: Double?) {
            owner.setTemporaryRate(sessionID: sessionID, attemptID: attemptID, rate: rate)
        }
        func setVolume(sessionID: UUID, volume: Double) { owner.setVolume(sessionID: sessionID, volume: volume) }
        func selectTrack(sessionID: UUID, id: Int, kind: PlaybackVideoTrack.Kind) async throws {
            try await owner.selectTrack(sessionID: sessionID, id: id, kind: kind)
        }
        func disableSubtitle(sessionID: UUID) throws { try owner.disableSubtitle(sessionID: sessionID) }
        func currentDiagnostics(sessionID: UUID) -> PlaybackVideoDiagnostics? {
            owner.currentDiagnostics(sessionID: sessionID)
        }
        func startPictureInPicture(sessionID: UUID) { owner.startPictureInPicture(sessionID: sessionID) }
        func stopPictureInPicture(sessionID: UUID) { owner.stopPictureInPicture(sessionID: sessionID) }
        func hasSystemOwner(sessionID: UUID) -> Bool { owner.hasSystemOwner(sessionID: sessionID) }
        #if os(iOS)
        func makeVideoView(sessionID: UUID) -> UIView { owner.makeVideoView(sessionID: sessionID) }
        func setVideoScale(sessionID: UUID, scale: FilmVideoScale) { owner.setVideoScale(sessionID: sessionID, scale: scale) }
        #endif
    }

    public init(playback: PlaybackController, preferences: FilmPlayerPreferencesStore,
                willCommit: @escaping @MainActor () -> (@MainActor () -> Void)?) throws {
        self.playback = playback; self.preferences = preferences
        lease = try playback.bindVideo(owner: "film", commands: .init(
            previous: { [weak self] attempt in self?.navigate(attempt, forward: false) ?? false },
            next: { [weak self] attempt in self?.navigate(attempt, forward: true) ?? false }
        ), willCommit: willCommit) { [weak self] event in self?.receive(event) }
        #if os(iOS)
        lease.restorePictureInPictureUI = { [weak self] in
            guard let self, let current else { return false }
            return await self.restoreUI?(current.sessionID) ?? false
        }
        #endif
        preferenceObservation = preferences.$preferences.map(\.showsControlCenter).removeDuplicates()
            .sink { [weak self] visible in
                guard let self, let current, let attempt = self.attempt(current.sessionID) else { return }
                self.playback.setNowPlayingVisible(visible, attempt: attempt)
            }
    }

    public func debugSnapshot() -> DebugJSONValue {
        .object([
            "undrainedCount": .int(undrained.count),
            "hasPending": .bool(pending != nil),
            "pendingAttemptId": .optional(pending?.attemptID),
            "hasCurrent": .bool(current != nil),
            "currentAttemptId": .optional(current?.attemptID),
            "stoppingSessionsCount": .int(stoppingSessions.count),
            "lease": lease.debugSnapshot()
        ])
    }

    func play(_ input: FilmPlaybackMediaInput, model: FilmPlaybackModel, preservePosition: Bool,
              waitForCommit: @escaping @MainActor () async throws -> Void,
              canCommit: @escaping @MainActor () -> Bool,
              release: @escaping @MainActor () async throws -> Void,
              receive: @escaping @MainActor (FilmMediaEvent) -> Void) async throws {
        let record = Media(input: input, model: model, receive: receive)
        let receipt: PlaybackVideoMediaLease
        do {
            let native = try PlaybackVideoInput(resourceID: input.resourceID, url: input.url, title: input.title,
                format: .init(runtimeValue: input.format),
                startPosition: input.position, decryption: try Self.decryption(input.drm))
            receipt = try PlaybackVideoMediaLease(attemptID: input.attemptID, input: native) { [weak self, record] in
                self?.undrained.removeAll { $0 === record }
                if self?.current === record { self?.current = nil }
                if self?.pending === record { self?.pending = nil }
                try await release()
            }
        } catch { throw Self.failure(error) }
        undrained.append(record)
        pending = record
        defer { if pending === record { pending = nil } }
        do {
            record.nativeAttempt = try await lease.play(receipt, paused: input.paused, preservePosition: preservePosition,
                                                       initialRate: current?.sessionID == input.sessionID
                                                           ? nil : preferences.preferences.defaultRate,
                                                       showsNowPlaying: preferences.preferences.showsControlCenter,
                                                       waitForCommit: waitForCommit, canCommit: canCommit)
        } catch {
            let originalError = error
            let isCancelledOrStale = originalError is CancellationError ||
                (originalError as? PlaybackVideoError) == .cancelled ||
                (originalError as? PlaybackVideoError) == .staleAttempt
            do { try await receipt.close() }
            catch {
                if isCancelledOrStale { throw FilmPlaybackError(code: "MEDIA_RELEASE_FAILED") }
            }
            throw Self.failure(originalError)
        }
    }

    private func receive(_ event: PlaybackVideoEvent) {
        let record = pending?.attemptID == event.attemptID ? pending : current?.attemptID == event.attemptID ? current : nil
        guard let record else { return }
        record.nativeAttempt = event.nativeAttempt ?? record.nativeAttempt
        record.state.position = event.snapshot.position
        record.state.duration = event.snapshot.duration ?? 0
        record.state.bufferedPosition = event.bufferedPosition
        record.state.videoWidth = Int(event.videoSize.width)
        record.state.videoHeight = Int(event.videoSize.height)
        record.state.canSeek = event.canSeek
        record.state.paused = event.snapshot.phase == .paused || !event.wantsPlayback
        record.state.buffering = event.isBuffering || event.snapshot.phase == .preparing || event.snapshot.phase == .buffering
        record.state.engine = event.engine
        record.state.chapters = event.chapters
        record.state.tracks = event.tracks
        record.state.diagnostics = event.diagnostics
        record.state.rate = playback.effectiveRate; record.state.volume = playback.volume
        record.state.pictureInPictureState = event.pictureInPicture.rawValue
        record.state.pictureInPicture = event.pictureInPicture == .active
        record.state.pictureInPictureAvailable = [.possible, .active].contains(event.pictureInPicture)
        switch event.kind {
        case .preparing, .prepared: return
        case .started:
            let previous = current
            current = record
            if pending === record { pending = nil }
            DebugRuntime.log(.filmPlayback, sub: "film.media", event: "started", level: .info, data: .object([
                "attemptId": .string(record.attemptID),
                "engine": .string(event.engine.rawValue),
                "duration": .number(record.state.duration),
                "tracksCount": .int(record.state.tracks.count)
            ]))
            record.receive(.state(record.state))
            record.receive(.started(attemptID: record.attemptID))
            if let previous, previous.sessionID != record.sessionID {
                previous.receive(.ownerEnded(attemptID: previous.attemptID))
            }
        case .progress, .paused:
            record.receive(.state(record.state))
        case .stopped:
            let explicitStop = (stoppingSessions[record.sessionID] ?? 0) > 0
            DebugRuntime.log(.filmPlayback, sub: "film.media", event: "stopped", level: .info, data: .object([
                "attemptId": .string(record.attemptID),
                "explicitStop": .bool(explicitStop),
                "nextOwner": .optional(playback.currentAttempt?.owner)
            ]))
            record.receive(.state(record.state))
            if !explicitStop, playback.currentAttempt?.owner != lease.owner {
                record.receive(.ownerEnded(attemptID: record.attemptID))
            }
        case .failed:
            let mapped = Self.failure(event.error ?? .outputFailed)
            DebugRuntime.log(.filmPlayback, sub: "film.media", event: "failed", level: .error, data: .object([
                "attemptId": .string(record.attemptID),
                "code": .string(mapped.code)
            ]))
            record.receive(.state(record.state))
            record.receive(.failed(attemptID: record.attemptID, error: mapped))
        case .ended:
            DebugRuntime.log(.filmPlayback, sub: "film.media", event: "ended", level: .info, data: .object([
                "attemptId": .string(record.attemptID),
                "position": .number(record.state.position)
            ]))
            record.receive(.state(record.state))
            record.receive(.ended(attemptID: record.attemptID))
        case .pictureInPictureChanged:
            record.receive(.state(record.state))
            if event.pictureInPicture == .failed {
                record.model.reportMediaControlError(Self.failure(PlaybackVideoError.pictureInPictureFailed))
            }
        case .systemOwnerEnded:
            record.receive(.state(record.state))
            record.receive(.systemOwnerEnded(attemptID: record.attemptID))
        }
    }

    private func navigate(_ attempt: PlayAttempt, forward: Bool) -> Bool {
        guard let current, current.nativeAttempt == attempt else { return false }
        return current.model.navigateFromSystem(forward: forward)
    }

    func cancelPending(sessionID: UUID) async throws {
        guard pending?.sessionID == sessionID else { return }
        do { try await lease.cancelPending() } catch { throw Self.failure(error) }
    }

    func stop(sessionID: UUID) async throws {
        let isCurrentSession = current?.sessionID == sessionID
        let isPendingSession = pending?.sessionID == sessionID
        let hasUndrained = undrained.contains { $0.sessionID == sessionID }
        guard isPendingSession || isCurrentSession || hasUndrained else { return }
        stoppingSessions[sessionID, default: 0] += 1
        defer {
            if let count = stoppingSessions[sessionID], count > 1 {
                stoppingSessions[sessionID] = count - 1
            } else {
                stoppingSessions.removeValue(forKey: sessionID)
            }
        }
        do {
            if isCurrentSession {
                try await lease.stop()
            } else if isPendingSession {
                try await lease.cancelPending()
                if undrained.contains(where: { $0.sessionID == sessionID }) {
                    try await lease.drain()
                }
            } else if hasUndrained {
                try await lease.drain()
            }
        } catch { throw Self.failure(error) }
        if pending?.sessionID == sessionID { pending = nil }
        if current?.sessionID == sessionID { current = nil }
    }

    func isLocallyDrained(sessionID: UUID) -> Bool {
        !undrained.contains { $0.sessionID == sessionID }
    }

    public func close() async throws {
        try await lease.close()
        pending = nil; current = nil
    }

    private func attempt(_ sessionID: UUID) -> PlayAttempt? {
        guard current?.sessionID == sessionID, lease.currentAttempt == current?.nativeAttempt else { return nil }
        return current?.nativeAttempt
    }
    func isCurrent(sessionID: UUID, attemptID: String) -> Bool {
        current?.attemptID == attemptID && attempt(sessionID) != nil
    }
    func pause(sessionID: UUID, attemptID: String) -> Bool {
        guard isCurrent(sessionID: sessionID, attemptID: attemptID), playback.wantsPlayback,
              let value = attempt(sessionID) else { return false }
        return playback.pause(attempt: value)
    }
    func resume(sessionID: UUID, attemptID: String, userInitiated: Bool) {
        guard isCurrent(sessionID: sessionID, attemptID: attemptID), userInitiated || !playback.wantsPlayback,
              let value = attempt(sessionID) else { return }
        playback.resume(attempt: value, userInitiated: userInitiated)
    }
    func seek(sessionID: UUID, to position: Double) {
        if let value = attempt(sessionID) { playback.seek(to: position, attempt: value) }
    }
    func setRate(sessionID: UUID, rate: Double) {
        if let value = attempt(sessionID), playback.setPreferredRate(rate, attempt: value), let current {
            current.state.rate = playback.effectiveRate; current.receive(.state(current.state))
        }
    }
    func setTemporaryRate(sessionID: UUID, attemptID: String, rate: Double?) {
        guard isCurrent(sessionID: sessionID, attemptID: attemptID), let value = attempt(sessionID),
              playback.setTemporaryVideoRate(rate, attempt: value), let current else { return }
        current.state.rate = playback.effectiveRate; current.receive(.state(current.state))
    }
    func setVolume(sessionID: UUID, volume: Double) {
        if let value = attempt(sessionID), playback.setVolume(volume, attempt: value), let current {
            current.state.volume = playback.volume; current.receive(.state(current.state))
        }
    }
    func selectTrack(sessionID: UUID, id: Int, kind: PlaybackVideoTrack.Kind) async throws {
        guard let value = attempt(sessionID) else { throw FilmPlaybackError(code: "MEDIA_UNAVAILABLE") }
        do { try await lease.selectTrack(id, kind: kind, attempt: value) }
        catch { throw Self.failure(error) }
    }
    func disableSubtitle(sessionID: UUID) throws {
        guard let value = attempt(sessionID) else { throw FilmPlaybackError(code: "MEDIA_UNAVAILABLE") }
        do { try lease.disableSubtitle(attempt: value) }
        catch { throw Self.failure(error) }
    }
    func currentDiagnostics(sessionID: UUID) -> PlaybackVideoDiagnostics? {
        guard attempt(sessionID) != nil else { return nil }
        return lease.diagnostics
    }
    func startPictureInPicture(sessionID: UUID) {
        #if os(iOS)
        if let value = attempt(sessionID), !lease.startPictureInPicture(attempt: value) {
            current?.model.reportMediaControlError(.init(code: "PICTURE_IN_PICTURE_UNAVAILABLE"))
        }
        #else
        if attempt(sessionID) != nil {
            current?.model.reportMediaControlError(.init(code: "PICTURE_IN_PICTURE_UNAVAILABLE"))
        }
        #endif
    }
    func stopPictureInPicture(sessionID: UUID) {
        #if os(iOS)
        if let value = attempt(sessionID) { lease.stopPictureInPicture(attempt: value) }
        #endif
    }
    func hasSystemOwner(sessionID: UUID) -> Bool { attempt(sessionID) != nil && lease.canContinueInBackground }
    #if os(iOS)
    func makeVideoView(sessionID: UUID) -> UIView { lease.surface }
    func setVideoScale(sessionID: UUID, scale: FilmVideoScale) {
        switch scale {
        case .fit: lease.surface.aspect = .fit
        case .fill: lease.surface.aspect = .fill
        case .stretch: lease.surface.aspect = .stretch
        }
    }
    #endif

    private static func decryption(_ value: RuntimeValue?) throws -> PlaybackVideoDecryption? {
        guard let value, value != .null else { return nil }
        guard case .object(let fields) = value else {
            throw FilmPlaybackError(code: "MEDIA_DRM_UNSUPPORTED")
        }
        var typeMatches = false
        if let typeVal = fields["type"], typeVal != .null {
            guard case .string(let s) = typeVal, s.caseInsensitiveCompare("clearkey") == .orderedSame else {
                throw FilmPlaybackError(code: "MEDIA_DRM_UNSUPPORTED")
            }
            typeMatches = true
        }
        var keySystemMatches = false
        if let ksVal = fields["keySystem"], ksVal != .null {
            guard case .string(let s) = ksVal, s.caseInsensitiveCompare("org.w3.clearkey") == .orderedSame else {
                throw FilmPlaybackError(code: "MEDIA_DRM_UNSUPPORTED")
            }
            keySystemMatches = true
        }
        guard typeMatches || keySystemMatches else {
            throw FilmPlaybackError(code: "MEDIA_DRM_UNSUPPORTED")
        }
        let clearKeysField = fields["clearKeys"]
        let jwkField = fields["keys"]
        let hasClearKeys = clearKeysField != nil && clearKeysField != .null
        let hasJWK = jwkField != nil && jwkField != .null
        guard hasClearKeys != hasJWK else {
            throw FilmPlaybackError(code: "MEDIA_DRM_UNSUPPORTED")
        }
        var keys: [String: String] = [:]
        if case .object(let values)? = clearKeysField {
            for (kid, value) in values {
                guard case .string(let key) = value else { throw FilmPlaybackError(code: "MEDIA_DRM_UNSUPPORTED") }
                keys[kid] = key
            }
        } else if case .array(let values)? = jwkField {
            for value in values {
                guard case .object(let row) = value, row["kty"] == .string("oct"),
                      case .string(let kid)? = row["kid"], case .string(let key)? = row["k"], keys[kid] == nil else {
                    throw FilmPlaybackError(code: "MEDIA_DRM_UNSUPPORTED")
                }
                keys[kid] = key
            }
        } else { throw FilmPlaybackError(code: "MEDIA_DRM_UNSUPPORTED") }
        guard !keys.isEmpty else { throw FilmPlaybackError(code: "MEDIA_INPUT_INVALID") }
        do { return try PlaybackVideoDecryption(clearKeys: keys) }
        catch { throw FilmPlaybackError(code: "MEDIA_DRM_UNSUPPORTED") }
    }

    private static func failure(_ error: Error) -> FilmPlaybackError {
        if let error = error as? FilmPlaybackError { return error }
        if error is CancellationError { return .init(code: "CANCELLED") }
        guard let error = error as? PlaybackVideoError else { return .init(code: "MEDIA_INPUT_INVALID") }
        let code: String
        switch error {
        case .cancelled, .staleAttempt: code = "CANCELLED"
        case .invalidInput: code = "MEDIA_INPUT_INVALID"
        case .inputFailed: code = "MEDIA_READ_FAILED"
        case .unsupportedFormat: code = "MEDIA_FORMAT_UNSUPPORTED"
        case .decodingFailed: code = "MEDIA_DECODING_FAILED"
        case .outputFailed: code = "MEDIA_OUTPUT_FAILED"
        case .seekFailed: code = "MEDIA_SEEK_FAILED"
        case .timedOut: code = "TIMEOUT"
        case .decryptionFailed: code = "MEDIA_DECRYPTION_FAILED"
        case .resourceReleaseFailed: code = "MEDIA_RELEASE_FAILED"
        case .pictureInPictureUnavailable: code = "PICTURE_IN_PICTURE_UNAVAILABLE"
        case .pictureInPictureFailed: code = "PICTURE_IN_PICTURE_FAILED"
        }
        return .init(code: code)
    }
}
