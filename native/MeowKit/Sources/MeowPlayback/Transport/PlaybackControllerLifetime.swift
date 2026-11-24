#if os(iOS) || os(macOS)
import Foundation

/// Owns the state/resources required for final cleanup independently of the
/// controller's allocation. All mutation, teardown and callback delivery is main
/// actor isolated, including after a background final release of the controller.
@MainActor
final class PlaybackControllerLifetime {
    var volumePreferences: PlaybackVolumePreferences?
    var state = PlaybackStateMachine()
    // Present throughout operation; finish releases actor-owned captures before
    // returning, regardless of which thread drops the last holder reference.
    var callbacks: PlaybackCallbackDelivery! = PlaybackCallbackDelivery()
    private var seekCompletions: PlaybackSeekCompletions { callbacks.seeks }
    var seekTimeoutTask: Task<Void, Never>?
    var seekTimeoutID: UUID?
    var audioMedia: PlaybackAudioMedia?
    var audioPreparation: Task<Void, Never>?
    var audioSeekTask: Task<Void, Never>?
    var configChangeTask: Task<Void, Never>?
    var outputAdmission: Task<Void, Never>?
    var preparedAudio: [ObjectIdentifier: PlaybackAudioMedia] = [:]
    var retiredAudio: [ObjectIdentifier: Task<Void, Error>] = [:]
    var videoTransport: PlaybackVideoTransport?
    weak var videoLease: PlaybackVideoLease?
    var videoAdmission = UUID()
    var videoAdmissionOwner: String?
    weak var videoCandidate: PlaybackVideoTransport?
    var videoSeekTask: Task<Void, Never>?
    var retiredVideoLocalDrains: [UUID: Task<Void, Never>] = [:]
    var retiredVideos: [UUID: Task<Void, Error>] = [:]
    #if os(iOS)
    var systemSession: PlaybackSystemSession?
    var audioActivation: UUID?
    var systemReleases: [UUID: Task<NativePlaybackError?, Never>] = [:]
    #endif

    init(volumePreferences: PlaybackVolumePreferences?) {
        self.volumePreferences = volumePreferences
    }

    func finish() {
        state.stop()
        cancelPreparedAudio()
        revokeVideoAdmission()
        seekCompletions.enqueue(state.takeSeekResults())
        tearDownItem()
        #if os(iOS)
        systemSession?.invalidate()
        systemSession = nil
        #endif
        seekCompletions.cancelAll()
        seekCompletions.deliverPending()
        callbacks = nil
        volumePreferences = nil
        retiredVideoLocalDrains.removeAll()
        retiredVideos.removeAll()
    }

    func revokeVideoAdmission() {
        #if os(iOS)
        systemSession?.endAudioAdmission(generation: videoAdmission)
        #endif
        videoAdmission = UUID(); videoAdmissionOwner = nil
        let candidate = videoCandidate; videoCandidate = nil
        candidate?.cancel()
        #if os(iOS)
        if let candidate, !(state.isPlayable && state.wantsPlayback && !state.isInterrupted) {
            candidate.systemRelease = deactivateAudio()
        }
        #endif
    }

    func cancelSeekDeadline() {
        seekTimeoutTask?.cancel()
        seekTimeoutTask = nil
        seekTimeoutID = nil
    }

    func tearDownItem(endingSystemSession: Bool = false) {
        #if os(iOS)
        let retiringLease = videoLease
        #endif
        cancelSeekDeadline()
        #if os(iOS)
        audioActivation = nil
        #endif
        videoSeekTask?.cancel(); videoSeekTask = nil
        if let videoTransport {
            self.videoTransport = nil
            videoLease?.detach(videoTransport); videoLease = nil
            videoTransport.cancel()
            let retirement = UUID()
            let localDrain = Task { [weak self] in
                await videoTransport.closeLocal()
                self?.retiredVideoLocalDrains[retirement] = nil
            }
            retiredVideoLocalDrains[retirement] = localDrain
            retiredVideos[retirement] = Task { [weak self] in
                defer { self?.retiredVideos[retirement] = nil }
                await localDrain.value
                try await videoTransport.close()
            }
        }
        outputAdmission?.cancel(); outputAdmission = nil
        configChangeTask?.cancel(); configChangeTask = nil
        audioPreparation?.cancel(); audioPreparation = nil
        audioSeekTask?.cancel(); audioSeekTask = nil
        if let media = audioMedia {
            audioMedia = nil
            _ = retireAudio(media)
        }
        #if os(iOS)
        if endingSystemSession, let release = systemSession?.endSession() {
            retiringLease?.recordSystemRelease(release)
            let id = UUID()
            systemReleases[id] = release
            Task { [weak self] in
                _ = await release.value
                self?.systemReleases[id] = nil
            }
        }
        #endif
    }

    func retireAudio(_ media: PlaybackAudioMedia) -> Task<Void, Error> {
        let id = ObjectIdentifier(media)
        preparedAudio[id] = nil
        if let task = retiredAudio[id] { return task }
        media.cancel()
        let task = Task { [weak self] in
            defer { self?.retiredAudio[id] = nil }
            try await media.close()
        }
        retiredAudio[id] = task
        return task
    }

    func cancelPreparedAudio() {
        for media in Array(preparedAudio.values) { _ = retireAudio(media) }
    }

    #if os(iOS)
    func deactivateAudio() -> Task<NativePlaybackError?, Never>? {
        audioActivation = nil
        return systemSession?.deactivate()
    }
    #endif
}
#endif
