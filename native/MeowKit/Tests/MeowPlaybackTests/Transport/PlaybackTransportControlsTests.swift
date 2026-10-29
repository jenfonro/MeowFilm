#if os(iOS) || os(macOS)
import XCTest
import MeowContracts
@testable import MeowPlayback

final class PlaybackTransportControlsTests: XCTestCase {
    func testNativeVolumePersistsPerOwnerButSpeedAndStaleCommandsDoNot() async throws {
        let resource = try PlaybackResource(id: "local-controls",
            url: URL(fileURLWithPath: "/synthetic-test-not-opened/control.wav"), title: "测试")
        await MainActor.run {
            let suite = "playback-controls-test-" + UUID().uuidString
            let defaults = UserDefaults(suiteName: suite)!
            defer { defaults.removePersistentDomain(forName: suite) }
            let preferences = PlaybackVolumePreferences(defaults: defaults)
            XCTAssertEqual(preferences.volume(for: "music"), 1)
            let controller = PlaybackController(volumePreferences: preferences)
            let music = controller.playAttempt(resource: resource, owner: "music", autoplay: false)
            XCTAssertTrue(controller.setPreferredRate(1.5, attempt: music))
            XCTAssertTrue(controller.setVolume(0.35, attempt: music))
            let film = controller.playAttempt(resource: resource, owner: "film", autoplay: false)
            XCTAssertEqual(controller.volume, 1)
            XCTAssertFalse(controller.setVolume(0.9, attempt: music))
            XCTAssertTrue(controller.setVolume(0.6, attempt: film))
            controller.stop(attempt: film)
            let restored = PlaybackController(volumePreferences: PlaybackVolumePreferences(defaults: defaults))
            let next = restored.playAttempt(resource: resource, owner: "music", autoplay: false)
            XCTAssertEqual(restored.volume, 0.35)
            XCTAssertEqual(restored.player.volume, 0.35)
            XCTAssertEqual(restored.player.rate, 0)
            XCTAssertEqual(restored.preferredRate, 1, "Reference speed is not a persistent preference")
            XCTAssertEqual(preferences.volume(for: "film"), 0.6)
            restored.stop(attempt: next)
            XCTAssertFalse(restored.setVolume(1, attempt: next))
            XCTAssertEqual(preferences.volume(for: "music"), 0.35)
        }
    }

    func testVolumePreferenceRejectsInvalidValues() async {
        await MainActor.run {
            let suite = "playback-controls-test-" + UUID().uuidString
            let defaults = UserDefaults(suiteName: suite)!
            defer { defaults.removePersistentDomain(forName: suite) }
            let preferences = PlaybackVolumePreferences(defaults: defaults)
            for value in [true, "0.4", -1, 2] as [Any] {
                defaults.set(["music": value], forKey: PlaybackVolumePreferences.storageKey)
                XCTAssertEqual(preferences.volume(for: "music"), 1)
            }
            preferences.saveVolume(0.4, for: "music")
            for value in [Double.nan, .infinity, -1, 2] { preferences.saveVolume(value, for: "music") }
            XCTAssertEqual(preferences.volume(for: "music"), 0.4)
        }
    }

    func testControlsAreScopedFiniteAndDoNotResumePausedTransport() async throws {
        let resource = try PlaybackResource(id: "local-controls",
            url: URL(fileURLWithPath: "/synthetic-test-not-opened/control.wav"), title: "测试")
        await MainActor.run {
            let controller = PlaybackController()
            let first = controller.playAttempt(resource: resource, owner: "music", autoplay: false)
            XCTAssertTrue(controller.setPreferredRate(1.5, attempt: first))
            XCTAssertTrue(controller.setVolume(0.4, attempt: first))
            XCTAssertEqual(controller.player.defaultRate, 1.5)
            XCTAssertEqual(controller.player.volume, 0.4)
            XCTAssertEqual(controller.player.rate, 0, "A control change is not play intent")
            for rate in [Double.nan, .infinity, 0, 3.1] {
                XCTAssertFalse(controller.setPreferredRate(rate, attempt: first))
            }
            for volume in [Double.nan, .infinity, -0.1, 1.1] {
                XCTAssertFalse(controller.setVolume(volume, attempt: first))
            }
            let next = controller.playAttempt(resource: resource, owner: "music", autoplay: false)
            XCTAssertEqual(controller.preferredRate, 1.5)
            XCTAssertEqual(controller.volume, 0.4)
            XCTAssertFalse(controller.setPreferredRate(2, attempt: first))
            XCTAssertFalse(controller.setVolume(1, attempt: first))
            XCTAssertTrue(controller.setPreferredRate(2, attempt: next))
            let film = controller.playAttempt(resource: resource, owner: "film", autoplay: false)
            XCTAssertEqual(controller.preferredRate, 1)
            XCTAssertEqual(controller.volume, 1)
            XCTAssertFalse(controller.setPreferredRate(0.5, attempt: next))
            controller.stop(attempt: film)
            XCTAssertFalse(controller.setVolume(0.1, attempt: film))
        }
    }
}
#endif
