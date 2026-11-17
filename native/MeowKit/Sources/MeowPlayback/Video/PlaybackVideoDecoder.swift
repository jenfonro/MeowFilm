#if os(iOS) || os(macOS)
import Foundation
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

public extension PlaybackVideoOutput {
    var playerLayer: AVPlayerLayer? { nil }
    func setVideoGravity(_ gravity: AVLayerVideoGravity) {
        displayLayer?.videoGravity = gravity
        playerLayer?.videoGravity = gravity
    }
    func setPictureInPictureActive(_ active: Bool) {}
}

/// The product coordinator owns admission and system media; the injected
/// platform adapter owns video decoding and native presentation.
@MainActor
public protocol PlaybackVideoDecoding: AnyObject {
    var engine: PlaybackVideoEngine { get }
    var chapters: [PlaybackVideoChapter] { get }
    var output: any PlaybackVideoOutput { get }
    var position: Double { get }
    var duration: Double? { get }
    var seekable: Bool { get }
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
    func cancel()
    func close() async
}

public extension PlaybackVideoDecoding {
    var engine: PlaybackVideoEngine { .ksPlayer }
    var chapters: [PlaybackVideoChapter] { [] }
    var onPlaybackIntent: (@MainActor (Bool) -> Void)? {
        get { nil }
        set {}
    }
}

public typealias PlaybackVideoDecoderFactory = @MainActor () throws -> any PlaybackVideoDecoding
#endif
