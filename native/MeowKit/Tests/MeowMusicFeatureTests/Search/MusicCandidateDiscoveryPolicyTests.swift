import XCTest
import MeowMusicFeature

final class MusicCandidateDiscoveryPolicyTests: XCTestCase {
    func testQualityDefaultSpatialOrderingOfficialTieAnd192kAreDistinct() throws {
        let official = MusicCandidateDiscoveryFixture.option("official:fixture",
            qualities: ["128k", "192k", "320k", "flac", "flac24bit", "master", "dolby", "sky"])
        let script = MusicCandidateDiscoveryFixture.option(qualities: ["128k", "320k", "flac", "flac24bit"])
        let plan = try MusicCandidateDiscoveryPolicy().plan(MusicCandidateDiscoveryFixture.snapshot([script, official]))
        XCTAssertEqual(plan.first?.quality, "master")
        XCTAssertEqual(plan.first?.selection.installationId, "official:fixture")
        XCTAssertLessThan(try XCTUnwrap(plan.firstIndex { $0.quality == "sky" }), try XCTUnwrap(plan.firstIndex { $0.quality == "dolby" }))
        let high = plan.filter { $0.selection.installationId == "official:fixture" && ["192k", "320k"].contains($0.quality) }
        XCTAssertEqual(high.map(\.quality), ["320k", "192k"])
        XCTAssertEqual(Set(plan.map(\.selection.installationId)), ["script-a", "official:fixture"])
    }
    func testAllKnownEditionsRotatePlatformsAndKeepExactScriptInstances() throws {
        var options: [MusicCandidateDiscoveryOption] = []
        for n in 0..<14 { options.append(MusicCandidateDiscoveryFixture.option("script-\(n)", source: "tx-\(n)", qualities: ["flac"])) }
        options.append(MusicCandidateDiscoveryFixture.option("script-a", platform: "wy", source: "wy-1", qualities: ["flac"]))
        options.append(MusicCandidateDiscoveryFixture.option("script-b", platform: "wy", source: "wy-1", qualities: ["flac"]))
        let plan = try MusicCandidateDiscoveryPolicy().plan(MusicCandidateDiscoveryFixture.snapshot(options))
        XCTAssertEqual(Set(plan.map(\.selection.sourceKey)).count, 15)
        XCTAssertEqual(plan.count, 16)
        XCTAssertTrue(plan.contains { $0.selection.siteId == "wy" && $0.selection.installationId == "script-a" })
        XCTAssertTrue(plan.contains { $0.selection.siteId == "wy" && $0.selection.installationId == "script-b" })
        XCTAssertEqual(Set(plan.prefix(2).map(\.selection.siteId)), ["tx", "wy"])
    }
    func testPreferredMustMatchVersionRevisionAndQualityNotOnlyScriptName() throws {
        let one = MusicCandidateDiscoveryFixture.option("a", qualities: ["flac"])
        let two = MusicCandidateDiscoveryFixture.option("b", qualities: ["flac"])
        let snapshot = MusicCandidateDiscoveryFixture.snapshot([one, two]), policy = MusicCandidateDiscoveryPolicy()
        let selected = try policy.plan(snapshot, preferred: two.target(quality: "flac"))
        XCTAssertEqual(selected.first?.selection.installationId, "b")
        let stale = MusicCandidateDiscoveryFixture.option("b", qualities: ["flac"], version: "0", revision: "old")
        let rejected = try policy.plan(snapshot, preferred: stale.target(quality: "flac"))
        XCTAssertEqual(rejected.first?.selection.installationId, "a")
        XCTAssertFalse(two.target(quality: "flac").sameRequest(as: stale.target(quality: "flac")))
    }
    func testDisabledQualityCannotEnterPlanOrBeRestoredByPreference() throws {
        let option = MusicCandidateDiscoveryFixture.option(qualities: ["flac", "128k"])
        let base = MusicCandidateDiscoveryFixture.snapshot([option])
        let limited = MusicCandidateDiscoverySnapshot(track: base.track,
            state: .init(qualityEnabled: ["standard"], qualityOrder: ["standard"]), options: [option])
        let plan = try MusicCandidateDiscoveryPolicy().plan(limited, preferred: option.target(quality: "flac"))
        XCTAssertEqual(plan.map(\.quality), ["128k"])
    }
}
