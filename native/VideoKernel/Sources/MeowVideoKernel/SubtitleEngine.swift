import Foundation
import CoreGraphics
import CMeowVideoFFmpeg

/// On-demand single-track subtitle decoder & cue store.
/// Eliminates PGS 64x OOM, destructive lookup flicker, PNG round-trip encoding,
/// ASS vector-drawing leaks, and Big5-before-GB18030 mojibake.
final class SubtitleEngine: @unchecked Sendable {
    private var codecCtx: UnsafeMutablePointer<AVCodecContext>?
    private var timeBase: Double = 0
    private var startTimeSeconds: Double = 0
    private let lock = NSLock()
    private var cues: [VideoSubtitleCue] = []
    private let maxCues = 256

    deinit {
        close()
    }

    func open(stream: UnsafePointer<AVStream>, startTimeSeconds: Double = 0) -> Bool {
        close()
        guard let par = stream.pointee.codecpar,
              let codec = avcodec_find_decoder(par.pointee.codec_id),
              let ctx = avcodec_alloc_context3(codec) else {
            return false
        }
        if avcodec_parameters_to_context(ctx, par) < 0 || avcodec_open2(ctx, codec, nil) < 0 {
            var mutable: UnsafeMutablePointer<AVCodecContext>? = ctx
            avcodec_free_context(&mutable)
            return false
        }
        codecCtx = ctx
        timeBase = stream.pointee.time_base.den > 0 ? av_q2d(stream.pointee.time_base) : 0.001
        self.startTimeSeconds = startTimeSeconds.isFinite ? startTimeSeconds : 0
        return true
    }

    func close() {
        if codecCtx != nil {
            avcodec_free_context(&codecCtx)
        }
        flush()
    }

    func flush() {
        if let ctx = codecCtx {
            avcodec_flush_buffers(ctx)
        }
        lock.lock()
        cues.removeAll()
        lock.unlock()
    }

    /// Decodes one subtitle packet. Subtitle errors are isolated and never fail video playback.
    func decode(packet: UnsafePointer<AVPacket>, videoWidth: Int, videoHeight: Int) {
        guard let ctx = codecCtx else { return }
        var sub = AVSubtitle()
        var gotSub: Int32 = 0
        let ret = avcodec_decode_subtitle2(ctx, &sub, &gotSub, packet)
        guard ret >= 0, gotSub != 0 else { return }
        defer { avsubtitle_free(&sub) }

        let basePts: Double
        if sub.pts != mvk_av_nopts_value() {
            basePts = Double(sub.pts) / Double(AV_TIME_BASE) - startTimeSeconds
        } else if packet.pointee.pts != mvk_av_nopts_value() {
            basePts = Double(packet.pointee.pts) * timeBase - startTimeSeconds
        } else {
            return
        }
        let start = max(0, basePts + Double(sub.start_display_time) / 1000.0)
        let durationSec: Double
        if sub.end_display_time > sub.start_display_time {
            durationSec = Double(sub.end_display_time - sub.start_display_time) / 1000.0
        } else if packet.pointee.duration > 0 {
            durationSec = Double(packet.pointee.duration) * timeBase
        } else {
            durationSec = 5.0
        }
        let end = start + min(max(durationSec, 0.2), 30.0)

        let count = Int(sub.num_rects)
        guard count > 0, let rects = sub.rects else {
            // Empty PGS display set clears previous bitmap cue cleanly.
            lock.lock()
            cues = cues.map { cue in
                cue.image != nil && cue.end > start ? VideoSubtitleCue(start: cue.start, end: start, text: nil, image: cue.image, rect: cue.rect) : cue
            }
            lock.unlock()
            return
        }

        var textParts: [String] = []
        var parsedCues: [VideoSubtitleCue] = []
        let refW = max(1, ctx.pointee.width > 0 ? Int(ctx.pointee.width) : videoWidth)
        let refH = max(1, ctx.pointee.height > 0 ? Int(ctx.pointee.height) : videoHeight)

        for i in 0..<min(count, 32) {
            guard let rectPtr = rects[i] else { continue }
            let rect = rectPtr.pointee
            switch rect.type {
            case SUBTITLE_TEXT:
                if let cstr = rect.text, let decoded = Self.decodeText(cstr) {
                    let trimmed = decoded.trimmingCharacters(in: .whitespacesAndNewlines)
                    if !trimmed.isEmpty { textParts.append(trimmed) }
                }
            case SUBTITLE_ASS:
                if let cstr = rect.ass, let raw = Self.decodeText(cstr) {
                    if let cleaned = Self.parseASSLine(raw) {
                        textParts.append(cleaned)
                    }
                }
            case SUBTITLE_BITMAP:
                if let image = Self.renderBitmap(rect) {
                    let normRect = CGRect(
                        x: CGFloat(rect.x) / CGFloat(refW),
                        y: CGFloat(rect.y) / CGFloat(refH),
                        width: CGFloat(rect.w) / CGFloat(refW),
                        height: CGFloat(rect.h) / CGFloat(refH)
                    )
                    parsedCues.append(VideoSubtitleCue(start: start, end: end, text: nil, image: image, rect: normRect))
                }
            default:
                break
            }
        }

        if !textParts.isEmpty {
            parsedCues.append(VideoSubtitleCue(start: start, end: end, text: textParts.joined(separator: "\n")))
        }
        guard !parsedCues.isEmpty else { return }

        lock.lock()
        cues.append(contentsOf: parsedCues)
        if cues.count > maxCues {
            cues.removeFirst(cues.count - maxCues)
        }
        lock.unlock()
    }

    /// Non-destructive lookup for active cues at `time`; evicts only cues expired by > 1s.
    func activeCue(at time: Double) -> VideoSubtitleCue? {
        lock.lock()
        defer { lock.unlock() }
        cues.removeAll { $0.end < time - 1.0 }
        let matching = cues.filter { time >= $0.start && time <= $0.end }
        if matching.isEmpty { return nil }
        if let bitmap = matching.last(where: { $0.image != nil }) {
            return bitmap
        }
        let combined = matching.compactMap(\.text).joined(separator: "\n")
        guard !combined.isEmpty, let first = matching.first, let last = matching.last else { return nil }
        return VideoSubtitleCue(start: first.start, end: last.end, text: combined)
    }

    /// Parses an FFmpeg-emitted ASS event line, ignoring `Comment:` lines, stripping `{...}` override blocks,
    /// and suppressing ASS vector drawing payloads (`{\p1}...{\p0}`).
    static func parseASSLine(_ raw: String) -> String? {
        let trimmed = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty, !trimmed.hasPrefix("Comment:") else { return nil }

        let body: String
        if trimmed.hasPrefix("Dialogue:") {
            let after = trimmed.dropFirst("Dialogue:".count)
            body = Self.fieldAfterNthComma(after, count: 9) ?? String(after)
        } else {
            // FFmpeg SUBTITLE_ASS rects emit 8 comma-separated header fields before Text.
            body = Self.fieldAfterNthComma(trimmed[...], count: 8) ?? trimmed
        }

        var out = ""
        out.reserveCapacity(body.count)
        var depth = 0
        var drawingMode = false
        var tagBuf = ""
        var index = body.startIndex
        while index < body.endIndex {
            let ch = body[index]
            if ch == "{" {
                if depth == 0 { tagBuf.removeAll(keepingCapacity: true) }
                depth += 1
            } else if ch == "}" {
                if depth > 0 {
                    depth -= 1
                    if depth == 0 {
                        if let mode = Self.parseDrawingTagMode(tagBuf) {
                            drawingMode = mode
                        }
                    }
                }
            } else if depth > 0 {
                tagBuf.append(ch)
            } else if !drawingMode {
                if ch == "\\" {
                    let next = body.index(after: index)
                    if next < body.endIndex {
                        let esc = body[next]
                        if esc == "N" || esc == "n" {
                            out.append("\n")
                            index = body.index(after: next)
                            continue
                        } else if esc == "h" {
                            out.append(" ")
                            index = body.index(after: next)
                            continue
                        }
                    }
                }
                out.append(ch)
            }
            index = body.index(after: index)
        }
        let cleaned = out.trimmingCharacters(in: .whitespacesAndNewlines)
        return cleaned.isEmpty ? nil : cleaned
    }

    /// Scans an ASS override block for `\p<n>` (drawing mode scale): returns `true` for `\p1+`, `false` for `\p0`, `nil` if absent.
    private static func parseDrawingTagMode(_ block: String) -> Bool? {
        var result: Bool?
        var idx = block.startIndex
        while idx < block.endIndex {
            if block[idx] == "\\" {
                let next = block.index(after: idx)
                if next < block.endIndex && block[next] == "p" {
                    let argIdx = block.index(after: next)
                    if argIdx < block.endIndex, let ascii = block[argIdx].asciiValue, ascii >= 48 && ascii <= 57 {
                        // Reject longer tag names like `\pos` or `\pbo`; `\p` is immediately followed by digits.
                        result = (ascii != 48)
                    }
                }
            }
            idx = block.index(after: idx)
        }
        return result
    }

    private static func fieldAfterNthComma(_ s: Substring, count: Int) -> String? {
        var commas = 0
        var idx = s.startIndex
        while idx < s.endIndex {
            if s[idx] == "," {
                commas += 1
                if commas == count {
                    return String(s[s.index(after: idx)...])
                }
            }
            idx = s.index(after: idx)
        }
        return nil
    }

    /// Decodes subtitle bytes preferring UTF-8 (with BOM), then GB18030 (before Big5), then ISO-Latin-1.
    private static func decodeText(_ ptr: UnsafePointer<CChar>) -> String? {
        let length = strlen(ptr)
        guard length > 0 else { return nil }
        var data = Data(bytes: ptr, count: length)
        if data.starts(with: [0xEF, 0xBB, 0xBF]) {
            data.removeFirst(3)
            return String(data: data, encoding: .utf8)
        }
        if let utf8 = String(data: data, encoding: .utf8) { return utf8 }
        let gb18030 = CFStringConvertEncodingToNSStringEncoding(
            CFStringEncoding(CFStringEncodings.GB_18030_2000.rawValue)
        )
        if let text = String(data: data, encoding: String.Encoding(rawValue: gb18030)) { return text }
        let big5 = CFStringConvertEncodingToNSStringEncoding(
            CFStringEncoding(CFStringEncodings.big5.rawValue)
        )
        if let text = String(data: data, encoding: String.Encoding(rawValue: big5)) { return text }
        return String(data: data, encoding: .isoLatin1)
    }

    /// Direct RGBA8888 CGImage from paletted PGS/DVD bitmap using exact `bytesPerRow = width * 4` (no PNG round-trip).
    private static func renderBitmap(_ rect: AVSubtitleRect) -> CGImage? {
        let w = Int(rect.w), h = Int(rect.h)
        guard w > 0, h > 0, w <= 4096, h <= 2160,
              let src = rect.data.0, let palettePtr = rect.data.1 else { return nil }
        let srcStride = Int(rect.linesize.0)
        guard srcStride >= w else { return nil }
        let nbColors = max(0, min(256, Int(rect.nb_colors)))
        guard nbColors > 0 else { return nil }

        let bytesPerRow = w * 4
        var rgba = Data(count: bytesPerRow * h)
        let palette = UnsafeRawPointer(palettePtr).assumingMemoryBound(to: UInt32.self)
        rgba.withUnsafeMutableBytes { dstRaw in
            guard let dst = dstRaw.baseAddress?.assumingMemoryBound(to: UInt8.self) else { return }
            for y in 0..<h {
                let srow = src.advanced(by: y * srcStride)
                let drow = dst.advanced(by: y * bytesPerRow)
                for x in 0..<w {
                    let idx = Int(srow[x])
                    let argb = idx < nbColors ? palette[idx] : 0
                    let a = UInt8((argb >> 24) & 0xFF)
                    let r = UInt8((argb >> 16) & 0xFF)
                    let g = UInt8((argb >> 8) & 0xFF)
                    let b = UInt8(argb & 0xFF)
                    let o = x * 4
                    // Premultiply alpha for CoreGraphics RGBA8888
                    drow[o]     = UInt8((UInt16(r) * UInt16(a)) / 255)
                    drow[o + 1] = UInt8((UInt16(g) * UInt16(a)) / 255)
                    drow[o + 2] = UInt8((UInt16(b) * UInt16(a)) / 255)
                    drow[o + 3] = a
                }
            }
        }
        guard let provider = CGDataProvider(data: rgba as CFData) else { return nil }
        return CGImage(
            width: w,
            height: h,
            bitsPerComponent: 8,
            bitsPerPixel: 32,
            bytesPerRow: bytesPerRow,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.premultipliedLast.rawValue),
            provider: provider,
            decode: nil,
            shouldInterpolate: true,
            intent: .defaultIntent
        )
    }
}
