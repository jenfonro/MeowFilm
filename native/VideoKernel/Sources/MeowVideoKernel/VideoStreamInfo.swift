import Foundation
import AVFoundation
import KSPlayer

public enum VideoKernelFailure: Error, Equatable, Sendable {
    case interrupted, cancelled, openFailed, formatFailed, decodeFailed
    case seekFailed, outputFailed, timedOut, transportFailed, decryptionFailed

    init(_ error: Error, encrypted: Bool = false) {
        let value = error as NSError
        let underlying = value.userInfo[NSUnderlyingErrorKey] as? KSPlayer.AVError
        if let underlying,
           encrypted, underlying.code == -1 || underlying.code == KSPlayer.AVError.invalidData.code {
            self = .decryptionFailed
            return
        }
        if let underlying, value.domain == KSPlayerErrorDomain,
           [KSPlayerErrorCode.formatOpenInput, .formatFindStreamInfo, .readFrame].contains(where: { $0.rawValue == value.code }),
           [KSPlayer.AVError.httpBadRequest.code, KSPlayer.AVError.httpUnauthorized.code,
            KSPlayer.AVError.httpForbidden.code, KSPlayer.AVError.httpNotFound.code,
            KSPlayer.AVError.httpOther4xx.code, KSPlayer.AVError.httpServerError.code,
            -Int32(EIO), -Int32(ETIMEDOUT), -Int32(ECONNRESET), -Int32(ECONNREFUSED),
            -Int32(ENETUNREACH), -Int32(EHOSTUNREACH), -Int32(EPIPE)].contains(underlying.code) {
            self = .transportFailed
            return
        }
        if value.code == KSPlayerErrorCode.formatOpenInput.rawValue,
           underlying?.code == KSPlayer.AVError.invalidData.code {
            self = .formatFailed
            return
        }
        switch KSPlayerErrorCode(rawValue: value.code) {
        case .formatCreate, .formatOpenInput: self = .openFailed
        case .formatFindStreamInfo, .videoTracksUnplayable: self = .formatFailed
        case .readFrame: self = .transportFailed
        case .audioOutputStart: self = .outputFailed
        case .unsupportedDolbyVision, .invalidDolbyVisionConfiguration: self = .formatFailed
        case .decryptionFailed: self = .decryptionFailed
        default: self = .decodeFailed
        }
    }
}

public struct VideoStreamInfo: Sendable {
    public let duration: Double?
    public let codec: String
    public let bitDepth: Int
    public let width: Int
    public let height: Int
    public let hasAudio: Bool
    public let sampleRate: Double?
    public let audioChannels: Int?
    public let dolbyVisionProfile: UInt8?
    public let chapters: [VideoChapter]

    @MainActor
    init(player: some MediaPlayerProtocol, video: some MediaPlayerTrack) {
        duration = player.duration.isFinite && player.duration > 0 ? player.duration : nil
        let code = video.codecType
        codec = String(bytes: [UInt8(truncatingIfNeeded: code >> 24), UInt8(truncatingIfNeeded: code >> 16),
                               UInt8(truncatingIfNeeded: code >> 8), UInt8(truncatingIfNeeded: code)], encoding: .ascii) ?? ""
        bitDepth = Int(video.bitDepth)
        width = Int(video.naturalSize.width)
        height = Int(video.naturalSize.height)
        let audio = player.tracks(mediaType: .audio).first(where: \.isEnabled)
        let audioFormat = (audio as? FFmpegAssetTrack)?.audioDescriptor?.audioFormat
        hasAudio = audio != nil
        sampleRate = audioFormat?.sampleRate
        audioChannels = audioFormat.map { Int($0.channelCount) }
        dolbyVisionProfile = video.dovi?.dv_profile
        chapters = player.chapters.map {
            VideoChapter(title: $0.title, start: $0.start, end: $0.end)
        }
    }
}

public struct VideoChapter: Sendable, Equatable {
    public let title: String
    public let start: Double
    public let end: Double
}

public struct VideoTrack: Sendable, Equatable {
    public enum Kind: Equatable, Sendable {
        case video, audio, subtitle
        var mediaType: AVMediaType {
            switch self { case .video: return .video; case .audio: return .audio; case .subtitle: return .subtitle }
        }
    }
    public let id: Int32
    public let kind: Kind
    public let name: String
    public let language: String?
    public let isEnabled: Bool

    init(_ track: any MediaPlayerTrack) {
        id = track.trackID
        kind = track.mediaType == .video ? .video : track.mediaType == .audio ? .audio : .subtitle
        name = track.name
        language = track.languageCode
        isEnabled = track.isEnabled
    }
}
