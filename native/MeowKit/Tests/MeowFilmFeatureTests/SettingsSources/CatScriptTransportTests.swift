import Foundation
import XCTest
import MeowJSON
@testable import MeowFilmFeature

final class CatScriptTransportTests: XCTestCase {
    func testRawBlobReassemblesAcrossChunksAndReleases() async throws {
        let value = JSONValue.object(["中文业务键": .string(String(repeating: "源", count: 30_000))])
        let fixture = TransferFixture(bytes: try JSONEncoder().encode(value))
        let transport = CatScriptTransport(rpc: { method, params, _ in try await fixture.rpc(method, params) })
        let result = try await transport.call("status")
        XCTAssertEqual(result, value)
        let reads = await fixture.readOffsets
        XCTAssertEqual(reads, [0, 65_536])
        let releases = await fixture.releases
        XCTAssertEqual(releases, ["blob.release"])
    }

    func testMalformedBlobReceiptsAndChunksReleaseEvenOnFailure() async throws {
        for fault in ["hash", "length", "chunkBytes", "next", "base64", "contract", "json"] {
            let fixture = TransferFixture(bytes: Data("{\"list\":[]}".utf8), fault: fault)
            let transport = CatScriptTransport(rpc: { method, params, _ in try await fixture.rpc(method, params) })
            do { _ = try await transport.call("status"); XCTFail("Expected invalid response for \(fault)") }
            catch let error as FilmSourceError { XCTAssertEqual(error.code, "INVALID_RESPONSE") }
            let releases = await fixture.releases
            XCTAssertEqual(releases, ["blob.release"], fault)
        }
    }

    func testSourceUploadUsesExactChunkOffsetsAndAlwaysAbortsReceipt() async throws {
        let source = String(repeating: "x", count: 65_537)
        for fail in [false, true] {
            let fixture = TransferFixture(bytes: Data(), failConsume: fail)
            let transport = CatScriptTransport(rpc: { method, params, _ in try await fixture.rpc(method, params) })
            do {
                _ = try await transport.withSource(source, operation: "install", params: ["installationId": .string("a")])
                XCTAssertFalse(fail)
            } catch let error as FilmSourceError { XCTAssertEqual(error.code, "INSTALL_FAILED"); XCTAssertTrue(fail) }
            let offsets = await fixture.uploadOffsets
            let uploaded = await fixture.uploaded
            let begin = await fixture.begin
            let releases = await fixture.releases
            XCTAssertEqual(offsets, [0, 65_536])
            XCTAssertEqual(uploaded, Data(source.utf8))
            XCTAssertEqual(begin?.catField("sha256")?.catText, CatScriptTransport.hash(Data(source.utf8)))
            XCTAssertEqual(begin?.catField("kind"), .string("source"))
            XCTAssertEqual(releases, ["stage.abort"])
        }
    }

    func testMalformedStageBeginStillAbortsKnownToken() async throws {
        let fixture = TransferFixture(bytes: Data(), fault: "stageChunk")
        let transport = CatScriptTransport(rpc: { method, params, _ in try await fixture.rpc(method, params) })
        do { _ = try await transport.withSource("source", operation: "install", params: [:]); XCTFail("Invalid stage") }
        catch let error as FilmSourceError { XCTAssertEqual(error.code, "INVALID_RESPONSE") }
        let releases = await fixture.releases
        XCTAssertEqual(releases, ["stage.abort"])
    }

    func testCancellationDuringBlobReadStillReleasesOutsideCancelledTask() async throws {
        let fixture = TransferFixture(bytes: Data("{}".utf8), fault: "cancel")
        let transport = CatScriptTransport(rpc: { method, params, _ in try await fixture.rpc(method, params) })
        do { _ = try await transport.call("status"); XCTFail("Cancelled blob") }
        catch { XCTAssertTrue(error is CancellationError) }
        let releases = await fixture.releases
        XCTAssertEqual(releases, ["blob.release"])
        let cancelledCleanup = await fixture.cancelledCleanup
        XCTAssertFalse(cancelledCleanup)
    }
}

private actor TransferFixture {
    let bytes: Data
    let fault: String?
    let failConsume: Bool
    var readOffsets: [Int] = []
    var uploadOffsets: [Int] = []
    var uploaded = Data()
    var begin: JSONValue?
    var releases: [String] = []
    var cancelledCleanup = false
    init(bytes: Data, fault: String? = nil, failConsume: Bool = false) {
        self.bytes = fault == "json" ? Data("not-json".utf8) : bytes
        self.fault = fault; self.failConsume = failConsume
    }
    func rpc(_ method: String, _ params: JSONValue) throws -> JSONValue {
        let operation = String(method.dropFirst("cat.script.".count))
        var fields: [String: JSONValue] = ["ok": .bool(true), "contract": .string("cat.script.v1")]
        switch operation {
        case "status":
            if fault == "contract" { fields["contract"] = .string("music.script.v3") }
            fields["blob"] = .object(["token": .string("receipt"),
                "length": .number(fault == "length" ? 0 : Double(bytes.count)),
                "chunkBytes": .number(fault == "chunkBytes" ? 1 : 65_536),
                "sha256": .string(fault == "hash" ? String(repeating: "0", count: 64) : CatScriptTransport.hash(bytes))])
        case "blob.read":
            if fault == "cancel" { throw CancellationError() }
            let offset = params.catField("offset")!.catInteger!
            readOffsets.append(offset)
            let end = min(offset + 65_536, bytes.count)
            fields["data"] = .string(fault == "base64" ? "====" : bytes.subdata(in: offset..<end).base64EncodedString())
            fields["next"] = .number(fault == "next" ? 0 : Double(end))
        case "stage.begin":
            begin = params
            fields["token"] = .string("upload")
            fields["chunkBytes"] = .number(fault == "stageChunk" ? 1 : 65_536)
        case "stage.chunk":
            let offset = params.catField("offset")!.catInteger!
            uploadOffsets.append(offset)
            uploaded.append(Data(base64Encoded: params.catField("data")!.catText!)!)
            fields["next"] = .number(Double(uploaded.count))
        case "install":
            if failConsume { fields["ok"] = .bool(false); fields["code"] = .string("INSTALL_FAILED") }
            else { fields["data"] = .string("{}") }
        case "stage.abort", "blob.release":
            releases.append(operation)
            cancelledCleanup = Task.isCancelled
        default: throw FilmSourceError(code: "UNEXPECTED_CALL")
        }
        return .object(fields)
    }
}
