#if os(iOS) || os(macOS)
import Foundation
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
    var onDisplayLayerChanged: (() -> Void)? { get set }
}

/// The product coordinator owns admission and system media; the injected
/// platform adapter owns video decoding and native presentation.
@MainActor
public protocol PlaybackVideoDecoding: AnyObject {
    var output: any PlaybackVideoOutput { get }
    var position: Double { get }
    var duration: Double? { get }
    var onFailure: (@MainActor (Error) -> Void)? { get set }
    var onProgress: (@MainActor (Double) -> Void)? { get set }
    var onTransportRate: (@MainActor (Float) -> Void)? { get set }
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

public typealias PlaybackVideoDecoderFactory = @MainActor () throws -> any PlaybackVideoDecoding
#endif
