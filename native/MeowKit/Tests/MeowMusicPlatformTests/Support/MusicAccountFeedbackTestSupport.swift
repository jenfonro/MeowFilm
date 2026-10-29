import Foundation
import XCTest
import CryptoKit
import zlib
import MeowAccounts
import MeowMusicFeature
import MeowJSON
@testable import MeowMusicPlatform

// Synthetic credentials/peers only. These fixtures never instantiate URLSession,
// access Keychain, execute reference code or forward an upstream request.
final class MusicAccountFeedbackGate: @unchecked Sendable {
    let entered: XCTestExpectation
    private let gate = MusicAccountTestGate()
    init(_ entered: XCTestExpectation) { self.entered = entered }
    func wait() async { entered.fulfill(); await gate.wait() }
    func release() async { await gate.release() }
}

actor MusicAccountFeedbackProviderFixture: MusicAccountFeedbackProvider {
    struct Counts: Equatable, Sendable {
        var verifies = 0, refreshes = 0, logins = 0, polls = 0
    }
    private var countsValue = Counts()
    private var reports: [MusicPlaybackFeedbackReport] = []
    private var inputs: [AccountCredential] = []
    private var feedbackGate: MusicAccountFeedbackGate?
    private var transition: (String, MusicAccountFeedbackGate)?
    private var failure: MusicPlaybackFeedbackReport.Event?
    private var ambiguous: MusicPlaybackFeedbackReport.Event?
    func counts() -> Counts { countsValue }
    func received() -> [MusicPlaybackFeedbackReport] { reports }
    func credentials() -> [AccountCredential] { inputs }
    func holdFeedback(_ gate: MusicAccountFeedbackGate) { feedbackGate = gate }
    func holdTransition(_ name: String, _ gate: MusicAccountFeedbackGate) { transition = (name, gate) }
    func fail(_ event: MusicPlaybackFeedbackReport.Event) { failure = event }
    func unconfirmed(_ event: MusicPlaybackFeedbackReport.Event) { ambiguous = event }
    private func wait(_ name: String) async {
        if let value = transition, value.0 == name { transition = nil; await value.1.wait() }
    }
    func feedback(_ report: MusicPlaybackFeedbackReport, credential: AccountCredential,
                  session: MusicAccountFeedbackNativeSession?,
                  guard authorization: MusicPlaybackFeedbackAuthorization) async throws -> MusicAccountFeedbackNativeResult {
        try await authorization.check()
        reports.append(report); inputs.append(credential)
        if let gate = feedbackGate { feedbackGate = nil; await gate.wait() }
        // Deliberately returns even after cancellation: the real channel/owner
        // must reject the late acknowledgement, not trust a cooperative fixture.
        if failure == report.event { throw MusicServiceError.timeout }
        if ambiguous == report.event { return .init(receipt: .unconfirmed) }
        return .init(receipt: report.event == .start && report.target.platform == .qq ? .registered : .confirmed)
    }
    func nativeID(_ credential: AccountCredential) throws -> String {
        try MusicQQWire.string(MusicQQWire.decode(credential.dataRepresentation())["uid"])
    }
    func verify(_ credential: AccountCredential) async throws -> MusicQQVerified {
        countsValue.verifies += 1; await wait("validate")
        return .init(credential: credential, nickname: "Synthetic", nativeID: try nativeID(credential))
    }
    func refresh(_ credential: AccountCredential) throws -> MusicQQVerified {
        countsValue.refreshes += 1
        return .init(credential: credential, nickname: "Synthetic", nativeID: try nativeID(credential))
    }
    func createQR(id: UUID) async throws -> MusicLoginChallenge {
        countsValue.logins += 1; await wait("login")
        return .init(id: id, platform: .qq, image: Data([0]), expiresAt: .distantFuture)
    }
    func pollQR(id: UUID) -> MusicQQLoginResult {
        countsValue.polls += 1; return .init(progress: .waiting, verified: nil)
    }
    func cancelQR(_ id: UUID) {}
    func revoke() async { await wait("revoke") }
    func recommendations(kind: String, page: Int, credential: AccountCredential) throws -> MusicQQRecommendation {
        throw MusicServiceError.unsupported
    }
    func collectionRows(credential: AccountCredential) -> [JSONValue] { [] }
    func tracks(collectionID: String, credential: AccountCredential) -> [JSONValue] { [] }
    func capabilities(mid: String, credential: AccountCredential) -> [String] { [] }
    func resolve(mid: String, quality: String, credential: AccountCredential) throws -> JSONValue {
        throw MusicServiceError.unsupported
    }
}

struct MusicAccountFeedbackOwnerFixture {
    let identity: AccountIdentity
    let store: MusicUpdateStore
    let provider = MusicAccountFeedbackProviderFixture()
    let accounts: AccountSessionCoordinator
    let service: NativeMusicAccountService
    init() throws {
        identity = try MusicAccountFixture.identity()
        store = MusicUpdateStore(); accounts = .init(store: store)
        store.seed(try MusicUpdateProvider.credential(), for: identity)
        service = .init(accounts: accounts, scopes: [.qq: identity], providers: [.qq: provider])
    }
    func enableAndValidate() async throws {
        _ = try await service.setAccess(available: true, generation: 1)
        _ = try await service.validate(platform: .qq)
    }
}

enum MusicAccountFeedbackFixture {
    static func target(_ platform: MusicAccountPlatform) -> MusicPlaybackFeedbackTarget {
        switch platform {
        case .qq: return .init(platform: .qq, nativeID: MusicAccountFixture.mid)
        case .netease: return .init(platform: .netease, nativeID: "101", source: "list", sourceID: "501", algorithm: "synthetic-alg")
        case .kugou: return .init(platform: .kugou, nativeID: MusicKugouFixture.baseHash, mixsongID: "22")
        }
    }
    static func report(_ event: MusicPlaybackFeedbackReport.Event, platform: MusicAccountPlatform = .qq,
                       instance: UUID, listened: Double = 1.25) -> MusicPlaybackFeedbackReport {
        .init(instanceID: instance, target: target(platform), event: event, duration: 180,
              listened: event == .start ? 0 : listened, mediaListened: event == .start ? 0 : listened,
              position: event == .start ? 0 : listened, rate: 1, reason: event == .start ? nil : .ui)
    }
    static func scope(_ identity: AccountIdentity) -> String {
        identity.source.installationID.uuidString.lowercased() + ":" + identity.source.siteKey + ":" +
            identity.accountID.uuidString.lowercased()
    }
    static func md5(_ data: Data) -> String {
        Insecure.MD5.hash(data: data).map { String(format: "%02x", $0) }.joined()
    }
    // Fixed independently generated zlib vectors, not production compress().
    static let codeZero = Data(base64Encoded: "eJyrVkrOT0lVsjKoBQASjwNC")!
    static let qqAck = Data(repeating: 0, count: 5) + codeZero

    // Decode producer bytes independently, with explicit zlib/gzip window mode.
    static func inflate(_ bytes: Data, gzip: Bool = false) throws -> Data {
        var stream = z_stream()
        guard inflateInit2_(&stream, gzip ? 31 : 15, ZLIB_VERSION, Int32(MemoryLayout<z_stream>.size)) == Z_OK else {
            throw MusicServiceError.invalidResponse
        }
        defer { inflateEnd(&stream) }
        return try bytes.withUnsafeBytes { input in
            stream.next_in = UnsafeMutablePointer<Bytef>(mutating: input.bindMemory(to: Bytef.self).baseAddress)
            stream.avail_in = uInt(bytes.count)
            var output = [UInt8](repeating: 0, count: 128 * 1024)
            let result = output.withUnsafeMutableBufferPointer { buffer -> Int32 in
                stream.next_out = buffer.baseAddress; stream.avail_out = uInt(buffer.count)
                return zlib.inflate(&stream, Z_FINISH)
            }
            guard result == Z_STREAM_END, stream.avail_in == 0 else { throw MusicServiceError.invalidResponse }
            return Data(output.prefix(Int(stream.total_out)))
        }
    }
}

// Only the exact M3 metadata lookup and three fixed CSCC routes. In particular,
// no generic fallback to a real transport or relaxed old-account fixture.
actor MusicAccountFeedbackKugouPeer: MusicAccountHTTPTransport {
    private var requests: [MusicAccountHTTPRequest] = []
    func recorded() -> [MusicAccountHTTPRequest] { requests }
    func send(_ request: MusicAccountHTTPRequest) throws -> MusicAccountHTTPResponse {
        requests.append(request)
        if request.url.absoluteString.hasPrefix("https://gateway.kugou.com/kmr/v2/audio?") {
            guard request.method == "POST", request.headers["x-router"] == "openapi.kugou.com" else {
                throw MusicServiceError.invalidRequest
            }
            try MusicKugouFixture.validateSignature(request)
            return try MusicKugouFixture.response(["status": 1, "data": [[
                "__status": 1, "base": ["album_audio_id": 22],
                "audio_info": ["hash": MusicKugouFixture.baseHash, "timelength": 180000]
            ]]])
        }
        guard request.url.scheme == "http", request.url.host == "d.kugou.com",
              request.headers.keys.allSatisfy({ !["cookie", "authorization", "x-router"].contains($0.lowercased()) })
        else { throw MusicServiceError.invalidRequest }
        switch (request.method, request.url.path) {
        case ("POST", "/v3/qrydid"):
            return try MusicKugouFixture.response(["status": 1, "data": ["deviceid": "synthetic-cscc-device"]])
        case ("GET", "/v2/gen"):
            // Cannot decrypt the fixed upstream RSA key; never fake a successful
            // handshake. Valid base64 with an invalid AES block is intentional.
            return try MusicKugouFixture.response(["status": 1, "data": "AA=="])
        case ("POST", "/v2/post"):
            return try MusicKugouFixture.response(["status": 1, "errcode": 0])
        default: throw MusicServiceError.invalidRequest
        }
    }
}
