import Foundation
import CMeowVideoFFmpeg

/// Bounded thread-safe FIFO queue for AVPacket pointers with serial epoch tracking,
/// producer interrupt wakeup for commands, and accurate buffered-timestamp accounting.
final class PacketQueue: @unchecked Sendable {
    struct Entry {
        let packet: UnsafeMutablePointer<AVPacket>
        let serial: Int
    }

    enum PushResult {
        case pushed
        case interrupted
        case discarded
    }

    private var mutex = pthread_mutex_t()
    private var notEmpty = pthread_cond_t()
    private var notFull = pthread_cond_t()
    private var entries: [Entry] = []
    private var headIndex = 0
    private var totalBytes = 0
    private let maxPackets: Int
    private let maxBytes: Int
    private var aborted = false
    private var eof = false
    private var serial = 1
    private var maxBufferedPtsSeconds: Double = 0
    private var timeBase: Double = 0
    private var startOffsetSeconds: Double = 0

    init(maxPackets: Int, maxBytes: Int) {
        self.maxPackets = maxPackets
        self.maxBytes = maxBytes
        pthread_mutex_init(&mutex, nil)
        pthread_cond_init(&notEmpty, nil)
        pthread_cond_init(&notFull, nil)
    }

    deinit {
        pthread_mutex_lock(&mutex)
        clearLocked()
        pthread_mutex_unlock(&mutex)
        pthread_cond_destroy(&notEmpty)
        pthread_cond_destroy(&notFull)
        pthread_mutex_destroy(&mutex)
    }

    func setTimeBase(_ secondsPerTick: Double, startOffset: Double = 0) {
        pthread_mutex_lock(&mutex)
        timeBase = secondsPerTick
        startOffsetSeconds = startOffset.isFinite ? startOffset : 0
        pthread_mutex_unlock(&mutex)
    }

    var count: Int {
        pthread_mutex_lock(&mutex)
        defer { pthread_mutex_unlock(&mutex) }
        return entries.count - headIndex
    }

    var bufferedUntilSeconds: Double {
        pthread_mutex_lock(&mutex)
        defer { pthread_mutex_unlock(&mutex) }
        return maxBufferedPtsSeconds
    }

    /// Wakes a producer parked on a full queue so it can immediately service pending commands.
    func wakeProducers() {
        pthread_mutex_lock(&mutex)
        pthread_cond_broadcast(&notFull)
        pthread_mutex_unlock(&mutex)
    }

    /// Blocks until capacity is available, interrupted by `shouldInterrupt`, or aborted.
    /// - `.pushed`: queue took ownership of `pkt`.
    /// - `.interrupted`: caller retains ownership of `pkt` to retry after servicing commands.
    /// - `.discarded`: aborted or serial changed; `pkt` has been freed.
    func push(_ pkt: UnsafeMutablePointer<AVPacket>, expectedSerial: Int,
              shouldInterrupt: () -> Bool) -> PushResult {
        pthread_mutex_lock(&mutex)
        defer { pthread_mutex_unlock(&mutex) }
        while !aborted && serial == expectedSerial &&
              ((entries.count - headIndex) >= maxPackets || totalBytes >= maxBytes) {
            if shouldInterrupt() {
                return .interrupted
            }
            pthread_cond_wait(&notFull, &mutex)
        }
        if aborted || serial != expectedSerial {
            var mutable: UnsafeMutablePointer<AVPacket>? = pkt
            av_packet_free(&mutable)
            return .discarded
        }
        let size = max(0, Int(pkt.pointee.size))
        totalBytes += size
        if timeBase > 0 {
            let rawPts = pkt.pointee.pts != mvk_av_nopts_value() ? pkt.pointee.pts : pkt.pointee.dts
            if rawPts != mvk_av_nopts_value() {
                let dur = pkt.pointee.duration > 0 ? Double(pkt.pointee.duration) * timeBase : 0
                let endSec = Double(rawPts) * timeBase - startOffsetSeconds + dur
                if endSec.isFinite && endSec > maxBufferedPtsSeconds {
                    maxBufferedPtsSeconds = endSec
                }
            }
        }
        entries.append(Entry(packet: pkt, serial: expectedSerial))
        pthread_cond_signal(&notEmpty)
        return .pushed
    }

    /// Pops the next packet. Blocks while empty unless EOF or aborted.
    /// Returns nil only on EOF (with `reachedEOF == true` and exact `eofSerial`) or abort.
    func pop(reachedEOF: inout Bool, eofSerial: inout Int) -> Entry? {
        pthread_mutex_lock(&mutex)
        defer { pthread_mutex_unlock(&mutex) }
        while !aborted && headIndex >= entries.count && !eof {
            pthread_cond_wait(&notEmpty, &mutex)
        }
        if aborted {
            reachedEOF = false
            eofSerial = serial
            return nil
        }
        if headIndex >= entries.count {
            reachedEOF = eof
            eofSerial = serial
            return nil
        }
        let entry = entries[headIndex]
        headIndex += 1
        totalBytes = max(0, totalBytes - max(0, Int(entry.packet.pointee.size)))
        if headIndex > 64 && headIndex * 2 >= entries.count {
            entries.removeFirst(headIndex)
            headIndex = 0
        }
        reachedEOF = false
        eofSerial = entry.serial
        pthread_cond_signal(&notFull)
        return entry
    }

    func markEOF(expectedSerial: Int) {
        pthread_mutex_lock(&mutex)
        if serial == expectedSerial {
            eof = true
            pthread_cond_broadcast(&notEmpty)
        }
        pthread_mutex_unlock(&mutex)
    }

    func flush(newSerial: Int, resetBufferedTo: Double = 0) {
        pthread_mutex_lock(&mutex)
        clearLocked()
        serial = newSerial
        eof = false
        maxBufferedPtsSeconds = max(0, resetBufferedTo)
        pthread_cond_broadcast(&notEmpty)
        pthread_cond_broadcast(&notFull)
        pthread_mutex_unlock(&mutex)
    }

    func abort() {
        pthread_mutex_lock(&mutex)
        aborted = true
        clearLocked()
        pthread_cond_broadcast(&notEmpty)
        pthread_cond_broadcast(&notFull)
        pthread_mutex_unlock(&mutex)
    }

    private func clearLocked() {
        for idx in headIndex..<entries.count {
            var mutable: UnsafeMutablePointer<AVPacket>? = entries[idx].packet
            av_packet_free(&mutable)
        }
        entries.removeAll(keepingCapacity: true)
        headIndex = 0
        totalBytes = 0
    }
}
