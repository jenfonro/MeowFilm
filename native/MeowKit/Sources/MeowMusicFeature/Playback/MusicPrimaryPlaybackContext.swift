import Foundation

/// Travels only with an explicit primary-page action, including its child
/// tasks. A later global "playing" observation cannot manufacture this intent.
enum MusicPrimaryPlaybackContext {
    @TaskLocal static var intent: UUID?
}
