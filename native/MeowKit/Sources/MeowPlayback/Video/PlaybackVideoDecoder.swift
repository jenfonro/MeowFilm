#if os(iOS) || os(macOS)
import Foundation
import CoreGraphics
import MeowContracts
@preconcurrency import AVFoundation
#if os(iOS)
import UIKit
#else
import AppKit
#endif

@MainActor
public protocol PlaybackVideoOutput: AnyObject {
    var volume: Float { get set }
    var rate: Float { get }
    #if os(iOS)
    var presentationView: UIView { get }
    #else
    var presentationView: NSView { get }
    #endif
    var displayLayer: AVSampleBufferDisplayLayer? { get }
    var playerLayer: AVPlayerLayer? { get }
    var onDisplayLayerChanged: (() -> Void)? { get set }
    func setVideoGravity(_ gravity: AVLayerVideoGravity)
    func setPictureInPictureActive(_ active: Bool)
}

/// The product coordinator owns admission and system media; the injected
/// platform adapter owns video decoding and native presentation.
@MainActor
public protocol PlaybackVideoDecoding: AnyObject {
    var engine: PlaybackVideoEngine { get }
    var chapters: [PlaybackVideoChapter] { get }
    var tracks: [PlaybackVideoTrack] { get }
    var output: any PlaybackVideoOutput { get }
    var position: Double { get }
    var bufferedPosition: Double { get }
    var isBuffering: Bool { get }
    var videoSize: CGSize { get }
    var duration: Double? { get }
    var seekable: Bool { get }
    var diagnostics: PlaybackVideoDiagnostics? { get }
    var onFailure: (@MainActor (Error) -> Void)? { get set }
    var onProgress: (@MainActor (Double) -> Void)? { get set }
    var onTransportRate: (@MainActor (Float) -> Void)? { get set }
    var onPlaybackIntent: (@MainActor (Bool) -> Void)? { get set }
    var onEnd: (@MainActor () -> Void)? { get set }
    func prepare(url: URL, format: String?, clearKeys: [String: Data],
                 position: Double, headers: [String: String]) async throws
    func align(to position: Double) async throws
    func renderFirstVideoFrame() async throws
    func validateForCommit() throws
    func commit(rate: Float, volume: Float)
    func setRate(_ rate: Float)
    func seek(to position: Double) async throws -> Double
    func selectTrack(_ id: Int, kind: PlaybackVideoTrack.Kind) async throws
    func disableSubtitle() throws
    func cancel()
    func close() async
}

/// nil selects the user's configured engine; non-nil selects a specific engine
/// (used by explicit pre-commit fallback from Aether to the native kernel).
public typealias PlaybackVideoDecoderFactory = @MainActor (_ engine: PlaybackVideoEngine?) throws -> any PlaybackVideoDecoding
#endif
