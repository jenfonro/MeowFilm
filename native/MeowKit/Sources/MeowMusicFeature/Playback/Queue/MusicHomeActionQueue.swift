import Foundation

/// Home action execution using the account owner and queue
/// lease. It does not authorize a URL, script, login, or library write.
public protocol MusicNativeHomeActionQueueProviding: MusicNativeAccountQueueProviding {
    func homeActionPage(_ lease: MusicAccountQueueLease, action: MusicHomeAction,
                        cursor: String?) async throws -> MusicNativeTrackPage
}
public protocol MusicHomeActionQueueServing: Sendable {
    func openHomeActionQueue(_ action: MusicHomeAction, title: String) async throws -> MusicRemoteQueueSource
}

public extension MusicHomeAction {
    var opensCollection: Bool {
        switch self { case .daily, .songCollection: return true; default: return false }
    }
    var isPersonalFM: Bool {
        switch self { case .fm, .ruleFM: return true; default: return false }
    }
}
