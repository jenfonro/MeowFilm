#if os(iOS) || os(macOS)
import Foundation
import AVFoundation
import XCTest
import MeowContracts
@testable import MeowPlayback

/// Silent-construction and preflight-only tests. None creates an audio input, opens a
/// media file, contacts a server or activates an audio session.
final class PlaybackControllerTests: XCTestCase {
    func testConstructionIsPassiveAndPlayerIsSharedAcrossIdleOperations() async {
        await MainActor.run {
            let controller = PlaybackController()
            let initial = controller.snapshot
            XCTAssertFalse(controller.hasActiveMusicInput)
            XCTAssertFalse(controller.hasMusicOutput)
            XCTAssertEqual(controller.snapshot, PlaybackSnapshot())
            XCTAssertEqual(controller.title, "")
            XCTAssertNil(controller.lastError)
            controller.pause()
            controller.resume()
            controller.seek(to: 20)
            controller.stop()
            XCTAssertEqual(controller.snapshot, initial)
            XCTAssertNil(controller.activeAudioMedia)
            XCTAssertEqual(controller.snapshot, PlaybackSnapshot())
        }
    }

    func testHeaderFailureDoesNotCreateItemAndRemainsTyped() async throws {
        let resource = try PlaybackResource(
            id: "denied", url: URL(fileURLWithPath: "/not-read/by-tests"),
            title: "请求头测试", headers: ["X-Test": "synthetic"]
        )
        await MainActor.run {
            let controller = PlaybackController()
            controller.play(resource: resource, owner: "film")
            XCTAssertEqual(controller.snapshot.phase, .failed)
            XCTAssertEqual(controller.title, "请求头测试")
            XCTAssertEqual(controller.lastError as? PlaybackNetworkError, .headersOnLocalFile)
            XCTAssertNil(controller.activeAudioMedia)
            XCTAssertFalse(controller.hasMusicOutput)
            XCTAssertEqual(controller.actualRate, 0)
            controller.resume()
            XCTAssertEqual(controller.snapshot.phase, .failed)
            controller.stop(owner: "music")
            XCTAssertEqual(controller.snapshot.phase, .failed)
            controller.stop(owner: "film")
            XCTAssertEqual(controller.snapshot, PlaybackSnapshot())
            XCTAssertNil(controller.lastError)
            XCTAssertEqual(controller.title, "")
        }
    }

    func testInvalidOwnerCannotCreateItem() async throws {
        let resource = try PlaybackResource(
            id: "invalid-owner", url: URL(fileURLWithPath: "/not-read/by-tests"), title: "测试"
        )
        await MainActor.run {
            let controller = PlaybackController()
            controller.play(resource: resource, owner: " ")
            XCTAssertNil(controller.activeAudioMedia)
            XCTAssertEqual(controller.lastError as? ContractError, .invalidIdentifier)
        }
    }

    func testIdleControllerHasNoRetentionCycle() async {
        await MainActor.run {
            var controller: PlaybackController? = PlaybackController()
            weak var reference = controller
            controller = nil
            XCTAssertNil(reference)
        }
    }

}
#endif
