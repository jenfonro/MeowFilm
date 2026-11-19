#if os(iOS) || os(macOS)
import XCTest
import MeowContracts
import MeowStorage
import MeowStorageTestSupport
@testable import MeowPlayback

@MainActor final class PlaybackTransportControlsTests: XCTestCase {
    func testNativeVolumePersistsPerOwnerButSpeedAndStaleCommandsDoNot() async throws {
        let resource = try PlaybackResource(id: "local-controls",
            url: URL(fileURLWithPath: "/synthetic-test-not-opened/control.wav"), title: "测试")
        let database = try storageTestDatabase(self)
        let preferences = PlaybackVolumePreferences()
        try await preferences.bind(database: database)
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
        await preferences.close()
        let restoredPreferences = PlaybackVolumePreferences()
        try await restoredPreferences.bind(database: database)
        let restored = PlaybackController(volumePreferences: restoredPreferences)
        let next = restored.playAttempt(resource: resource, owner: "music", autoplay: false)
        XCTAssertEqual(restored.volume, 0.35)
        XCTAssertEqual(restored.actualRate, 0)
        XCTAssertEqual(restored.preferredRate, 1, "Reference speed is not a persistent preference")
        XCTAssertEqual(preferences.volume(for: "film"), 0.6)
        restored.stop(attempt: next)
        XCTAssertFalse(restored.setVolume(1, attempt: next))
        XCTAssertEqual(preferences.volume(for: "music"), 0.35)
        await restoredPreferences.close()
    }

    func testVolumePreferenceRejectsInvalidValues() async throws {
        let database = try storageTestDatabase(self), preferences = PlaybackVolumePreferences()
        try await preferences.bind(database: database)
        preferences.saveVolume(0.4, for: "music")
        for value in [Double.nan, .infinity, -1, 2] { preferences.saveVolume(value, for: "music") }
        preferences.saveVolume(0.6, for: "")
        XCTAssertEqual(preferences.volume(for: "music"), 0.4)
        await preferences.close()
        for change in ["volume='invalid'", "volume=-1", "volume=2"] {
            await storageTestThrows { try await database.transaction { try $0.execute("UPDATE ios_playback_volume SET " + change) } }
        }
        let restored = PlaybackVolumePreferences()
        try await restored.bind(database: database)
        XCTAssertEqual(restored.volume(for: "music"), 0.4)
        XCTAssertEqual(restored.volume(for: "film"), 1)
        await restored.close()
    }

    func testControlsAreScopedFiniteAndDoNotResumePausedTransport() async throws {
        let resource = try PlaybackResource(id: "local-controls",
            url: URL(fileURLWithPath: "/synthetic-test-not-opened/control.wav"), title: "测试")
        await MainActor.run {
            let controller = PlaybackController()
            let first = controller.playAttempt(resource: resource, owner: "music", autoplay: false)
            XCTAssertTrue(controller.setPreferredRate(1.5, attempt: first))
            XCTAssertTrue(controller.setVolume(0.4, attempt: first))
            XCTAssertEqual(controller.preferredRate, 1.5)
            XCTAssertEqual(controller.volume, 0.4)
            XCTAssertEqual(controller.actualRate, 0, "A control change is not play intent")
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
