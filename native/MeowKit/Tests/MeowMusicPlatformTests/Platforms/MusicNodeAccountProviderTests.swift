import Foundation
import XCTest
import MeowAccounts
import MeowMusicFeature
import MeowMusicTestSupport
import MeowRuntime
@testable import MeowMusicPlatform

@MainActor final class MusicNodeAccountProviderTests: XCTestCase {
    func testAccountPagingRetainsOpaqueNumbersAndDistinctUTF8MetadataKeys() async throws {
        let f = try MusicNativeFixture(); try await f.enable()
        let raw = Data(#"{"songmid":"precise","name":"Synthetic","singer":"Artist","duration":180,"types":[],"extra":{"decimal":1.234567890123456789,"huge":1e999,"integer":9007199254740993,"é":"composed","e\u0301":"decomposed"}}"#.utf8)
        let original = try RuntimeJSON.decode(raw)
        let track = MusicNativeTrackData(platform: "tx", data: raw)
        await f.platform.replies("recommendations", try MusicPlatformFixture.page(Array(repeating: track, count: 31), platform: "tx"))
        let first = try await f.service.nativeRecommendations(platform: .qq, kind: "radar", cursor: nil)
        let cursor = try XCTUnwrap(first.cursor)
        let second = try await f.service.nativeRecommendations(platform: .qq, kind: "radar", cursor: cursor)
        let replay = try await f.service.nativeRecommendations(platform: .qq, kind: "radar", cursor: cursor)
        XCTAssertEqual(first.items.count, 30); XCTAssertEqual(second.items.count, 1)
        XCTAssertEqual(second.items.first?.data, replay.items.first?.data)
        for row in first.items + second.items {
            XCTAssertEqual(try RuntimeJSON.decode(row.data), original)
        }
        let requests = await f.platform.recorded()
        XCTAssertEqual(requests.filter { $0.operation == "recommendations" }.count, 1)
        _ = await f.service.close()
    }

    func testOptionalAccountCollectionCountDoesNotRejectHealthyEntries() async throws {
        let f = try MusicNativeFixture(); try await f.enable()
        let opaque = try RuntimeJSON.decode(Data("9007199254740993".utf8))
        let counts: [RuntimeValue] = [.number(7), .string("unknown"), .number(-1), opaque, .null]
        await f.platform.replies("personalPlaylists", .object(["items": .array(counts.enumerated().map { index, count in
            .object(["id": .string("list-\(index)"), "name": .string("Synthetic"), "owned": .bool(false), "count": count])
        }), "more": .bool(false)]))
        let page = try await f.service.nativeCollections(platform: .qq, cursor: nil)
        XCTAssertEqual(page.items.map(\.id), counts.indices.map { "list-\($0)" })
        XCTAssertEqual(page.items.map(\.count), [7, nil, nil, nil, nil])
        _ = await f.service.close()
    }

    func testAccountCollectionsRetainDisplayWhenIdentityAndContinuationAreMissing() async throws {
        let continuations: [RuntimeValue] = [.null, .object(["page": .number(1)]),
                                            .object(["page": .string("changed")])]
        for continuation in continuations {
            let f = try MusicNativeFixture(); try await f.enable()
            await f.platform.replies("personalPlaylists", .object(["items": .array([
                .object(["name": .string("Visible without identity")]),
                .null,
                .object(["id": .string("identified"), "name": .string("Current"), "owned": .bool(true)])
            ]), "more": .bool(true), "continuation": continuation]))
            let page = try await f.service.nativeCollections(platform: .qq, cursor: nil)
            XCTAssertEqual(page.items.map(\.id), ["", "", "identified"])
            XCTAssertEqual(page.items.map(\.title), ["Visible without identity", "", "Current"])
            XCTAssertEqual(page.items.map(\.owned), [false, false, true])
            XCTAssertNil(page.cursor)
            let requests = await f.platform.recorded()
            XCTAssertEqual(requests.filter { $0.operation == "personalPlaylists" }.count, 1)
            _ = await f.service.close()
        }
    }

    func testAccountRecommendationsRetainRowsWhenContinuationCannotAdvance() async throws {
        let continuations: [RuntimeValue] = [.null, .object(["page": .number(0)]),
                                            .object(["page": .number(1)]), .object(["page": .string("changed")])]
        for continuation in continuations {
            let f = try MusicNativeFixture(); try await f.enable()
            let song = try MusicPlatformFixture.song(MusicAccountFixture.track())
            await f.platform.replies("recommendations", .object([
                "items": .array([song]), "continuation": continuation
            ]))
            let page = try await f.service.nativeRecommendations(platform: .qq, kind: "radar", cursor: nil)
            XCTAssertEqual(page.items.count, 1); XCTAssertEqual(page.total, 1); XCTAssertNil(page.cursor)
            let requests = await f.platform.recorded()
            XCTAssertEqual(requests.filter { $0.operation == "recommendations" }.count, 1)
            _ = await f.service.close()
        }
    }

    func testDailyStyleReadsRetainCategoriesAndTagsWithoutFieldCompletenessGate() async throws {
        let categories: [RuntimeValue] = [.null, .object(["changed": .bool(true)]), .array([
            .object(["name": .string("Current"), "tags": .array([
                .object(["name": .string("Visible tag")]), .null
            ])]),
            .object(["id": .string("other"), "tags": .object(["changed": .bool(true)])]),
            .null
        ])]
        for rows in categories {
            let fixture = try MusicPlatformFixture { request in
                XCTAssertEqual(request.operation, "dailyStyle.get")
                return .object(["categories": rows])
            }
            let client = fixture.makeClient(), provider = MusicNodeAccountProvider(platform: .netease, client: client)
            let result = try await provider.dailyStyleCategories(authorize: {})
            XCTAssertEqual(result.count, rows.musicArray.count)
            if result.count == 3 {
                XCTAssertEqual(result.map(\.id), ["", "other", ""])
                XCTAssertEqual(result[0].name, "Current")
                XCTAssertEqual(result[0].tags.map(\.name), ["Visible tag", ""])
                XCTAssertTrue(result[1].tags.isEmpty)
            }
            XCTAssertEqual(fixture.requests().count, 1)
            try await client.close(); await fixture.runtime.close()
        }
    }

    func testConsumedOpaqueNumbersCannotBecomeRoundedCountsPositionsOrUnknownExpiry() throws {
        let song = try MusicPlatformFixture.song(MusicAccountFixture.track())
        for token in ["9007199254740993", "1.234567890123456789", "1e999"] {
            let value = try RuntimeJSON.decode(Data(token.utf8))
            XCTAssertNil(value.musicNumber)
            XCTAssertThrowsError(try MusicPlatformDTO.count(value))
            let tracks = try MusicPlatformDTO.tracks(.object([
                "items": .array([song]), "sourcePositions": .array([value])
            ]))
            XCTAssertEqual(tracks.count, 1); XCTAssertNil(tracks[0].sourcePosition)
            for key in ["expiresAt", "licenseExpiresAt"] {
                var fields: RuntimeObject = ["url": .string("https://media.invalid/synthetic.mp3")]
                fields[key] = value
                XCTAssertThrowsError(try MusicNativeAccountURLCache.audio(.object(fields)))
            }
        }
    }

    func testUnchangedCanonicalVerificationAndRefreshKeepTheOriginalOpaqueCredentialBytes() async throws {
        for platform in [MusicAccountPlatform.netease, .qq, .kugou] {
            let cookie = "synthetic-unchanged-cookie"
            let credential = try AccountCredential(data: Data(
                #"{"cookie":"synthetic-unchanged-cookie","userId":"900719925474099312","platform":"\#(platform.rawValue)"}"#.utf8))
            let fixture = try MusicPlatformFixture { request in
                XCTAssertTrue(["account.verify", "account.refresh"].contains(request.operation))
                return .object(["userId": .string("900719925474099312"), "cookie": .string(cookie),
                                "profile": .object(["displayName": .string("Current name")])])
            }
            let client = fixture.makeClient(), provider = MusicNodeAccountProvider(platform: platform, client: client)
            let verified = try await provider.verify(credential), refreshed = try await provider.refresh(credential)
            for value in [verified, refreshed] {
                XCTAssertEqual(value.credential, credential)
                XCTAssertEqual(value.credential.dataRepresentation(), credential.dataRepresentation())
                XCTAssertEqual(value.nativeID, "900719925474099312")
                XCTAssertEqual(value.nickname, "Current name")
            }
            XCTAssertEqual(fixture.requests().map(\.operation), ["account.verify", "account.refresh"])
            XCTAssertFalse(fixture.requests().contains { $0.transportOperation == "session.update" })
            try await client.close(); await fixture.runtime.close()
        }
    }

    func testVerifyAndRefreshConsumeCanonicalCredentialsWithoutRequiringOptionalProfileOrPublishing() async throws {
        for platform in [MusicAccountPlatform.netease, .qq, .kugou] {
            let fixture = try MusicPlatformFixture { request in
                XCTAssertTrue(["account.verify", "account.refresh"].contains(request.operation))
                return .object(["userId": .string("900719925474099312"),
                                "cookie": .string("synthetic-verified-cookie"),
                                "unused": .array([.null, .bool(false)])])
            }
            let client = fixture.makeClient()
            let provider = MusicNodeAccountProvider(platform: platform, client: client)
            let credential = try MusicAccountFixture.credential(platform: platform)
            let verified = try await provider.verify(credential), refreshed = try await provider.refresh(credential)
            for value in [verified, refreshed] {
                XCTAssertEqual(value.nativeID, "900719925474099312")
                XCTAssertEqual(value.nickname, "")
                let saved = try MusicAccountCredential.read(value.credential)
                XCTAssertEqual(saved[music: "platform"], .string(platform.rawValue))
                XCTAssertEqual(saved[music: "userId"], .string("900719925474099312"))
            }
            let requests = fixture.requests()
            let expectedCandidate = try MusicAccountCredential.read(credential)
            XCTAssertEqual(requests.map(\.operation), ["account.verify", "account.refresh"])
            XCTAssertTrue(requests.allSatisfy { $0.candidate == expectedCandidate })
            XCTAssertFalse(requests.contains { $0.transportOperation == "session.update" },
                           "Only the secure account owner may publish after its durable receipt")
            try await client.close(); await fixture.runtime.close()
        }
    }

    func testVerifiedAccountStillRequiresActualIdentityAndCredentialMaterials() async throws {
        let responses: [RuntimeValue] = [
            .object(["cookie": .string("synthetic")]),
            .object(["userId": .string("101")]),
            .object(["userId": .number(101), "cookie": .string("synthetic")])
        ]
        for value in responses {
            let fixture = try MusicPlatformFixture { _ in value }
            let client = fixture.makeClient(), credential = try MusicAccountFixture.credential()
            let provider = MusicNodeAccountProvider(platform: .qq, client: client)
            await denied(.upstreamInvalid) { _ = try await provider.verify(credential) }
            XCTAssertEqual(fixture.requests().count, 1)
            try await client.close(); await fixture.runtime.close()
        }
    }

    func testCanonicalVerificationCannotReplaceTheNativeOwnersBoundAccount() async throws {
        for platform in [MusicAccountPlatform.netease, .qq, .kugou] {
            let f = try MusicNativeFixture(platform: platform); try await f.enable()
            let original = try XCTUnwrap(f.store.peek(f.identity))
            await f.platform.replies("account.verify", .object(["userId": .string("202"),
                "cookie": .string("synthetic-other-account")]))
            await denied(.targetChanged) { _ = try await f.service.validate(platform: platform) }
            XCTAssertEqual(f.store.peek(f.identity), original)
            let statuses = try await f.service.statuses()
            XCTAssertFalse(statuses.contains { $0.phase == .signedIn })
            XCTAssertEqual(f.store.calls().filter { $0 == "write" }.count, 1, "Only fixture seeding wrote credentials")
            XCTAssertEqual(f.runtime.requests().filter {
                $0.transportOperation == "session.update" && $0.fields[music: "session"] != .null
            }.count, 0)
            let requests = await f.platform.recorded()
            XCTAssertEqual(requests.map(\.operation), ["account.verify"])
            _ = await f.service.close()
        }
    }

    func testMissingOptionalProfileRightsAndAvatarDoNotBecomeAuthenticationFailure() async throws {
        let profiles: [RuntimeValue] = [.null, .object([:]), .object(["displayName": .string("Current name")])]
        for profile in profiles {
            let fixture = try MusicPlatformFixture { _ in
                .object(["profile": profile, "unused": .object(["future": .array([.null])])])
            }
            let client = fixture.makeClient(), provider = MusicNodeAccountProvider(platform: .netease, client: client)
            let result = try await provider.accountProfile()
            XCTAssertEqual(result.profile.nickname, profile[music: "displayName"].musicText)
            XCTAssertNil(result.profile.avatarURL)
            XCTAssertNil(result.profile.membership.vip)
            XCTAssertEqual(result.recheckAt, .distantPast)
            XCTAssertEqual(fixture.requests().map(\.operation), ["account.profile"])
            try await client.close(); await fixture.runtime.close()
        }
    }

    func testQRConsumesCanonicalProgressAndImageWithoutAdditionalProfileRequest() async throws {
        let peer = MusicAccountRuntimePeer(clock: MusicAccountTestClock())
        await peer.replies("qr.create", .object(["imageBase64": .string(Data([1, 2, 3]).base64EncodedString()),
            "expiresAt": .number(1_800_000_180_000)]))
        let fixture = try MusicPlatformFixture { try await peer.respond($0) }
        let client = fixture.makeClient(), provider = MusicNodeAccountProvider(platform: .kugou, client: client)
        let id = UUID(), challenge = try await provider.createQR(id: id)
        XCTAssertEqual(challenge.id, id); XCTAssertEqual(challenge.platform, .kugou)
        XCTAssertEqual(challenge.image, Data([1, 2, 3]))
        for (status, expected) in [("waiting", MusicLoginProgress.waiting), ("confirming", .scanned)] {
            await peer.replies("qr.poll", .object(["status": .string(status)]))
            let result = try await provider.pollQR(id: id)
            XCTAssertEqual(result.progress, expected); XCTAssertNil(result.verified)
        }
        await peer.replies("qr.poll", .object(["status": .string("authenticated"),
            "account": .object(["userId": .string("42"), "cookie": .string("synthetic-token")])]))
        let login = try await provider.pollQR(id: id)
        XCTAssertEqual(login.progress, .confirmed); XCTAssertEqual(login.verified?.nativeID, "42")
        XCTAssertEqual(login.verified?.nickname, "")
        await denied(.staleResult) { _ = try await provider.pollQR(id: id) }
        XCTAssertEqual(fixture.requests().map(\.operation), ["qr.create", "qr.poll", "qr.poll", "qr.poll"])
        try await client.close(); await fixture.runtime.close()
    }

    func testCancelledQRDoesNotAcceptLateAuthentication() async throws {
        let gate = MusicAccountQueueOwnerGate("QR actual reply")
        let fixture = try MusicPlatformFixture { request in
            switch request.operation {
            case "qr.create":
                return .object(["imageBase64": .string("AQ=="), "expiresAt": .number(1_800_000_180_000)])
            case "qr.poll":
                await gate.wait()
                return .object(["status": .string("authenticated"), "account":
                    .object(["userId": .string("42"), "cookie": .string("synthetic-token")])])
            case "qr.cancel": return .object(["cancelled": .bool(true)])
            default: throw MusicPlatformFixture.Failure.remote("UNSUPPORTED")
            }
        }
        let client = fixture.makeClient(), provider = MusicNodeAccountProvider(platform: .netease, client: client)
        let id = UUID()
        _ = try await provider.createQR(id: id)
        let poll = MusicAccountQueueOwnerCall("cancelled QR result") { try await provider.pollQR(id: id) }
        await fulfillment(of: [gate.entered], timeout: 3)
        await provider.cancelQR(id)
        await provider.cancelQR(id)
        await gate.release()
        await fulfillment(of: [poll.finished, gate.departed], timeout: 3)
        XCTAssertThrowsError(try poll.value()) { XCTAssertEqual($0 as? MusicServiceError, .staleResult) }
        XCTAssertEqual(fixture.requests().map(\.operation), ["qr.create", "qr.poll", "qr.cancel"])
        try await client.close(); await fixture.runtime.close()
    }

    func testFailedQRCreationRetiresItsParticipatingChallengeExactlyOnce() async throws {
        let fixture = try MusicPlatformFixture { request in
            switch request.operation {
            case "qr.create": return .object(["imageBase64": .string("AQ==")])
            case "qr.cancel": return .object(["cancelled": .bool(true)])
            default: throw MusicPlatformFixture.Failure.remote("UNSUPPORTED")
            }
        }
        let client = fixture.makeClient(), provider = MusicNodeAccountProvider(platform: .netease, client: client)
        let id = UUID()
        await denied(.upstreamInvalid) { _ = try await provider.createQR(id: id) }
        await provider.cancelQR(id)
        await denied(.staleResult) { _ = try await provider.pollQR(id: id) }
        XCTAssertEqual(fixture.requests().map(\.operation), ["qr.create", "qr.cancel"])
        try await client.close(); await fixture.runtime.close()
    }

    func testAlreadyCancelledCallerRetiresACompletedQRAndWaitsForPhysicalCancel() async throws {
        let caller = MusicAccountQueueOwnerGate("cancelled QR caller")
        let physical = MusicAccountQueueOwnerGate("QR physical cancel")
        let fixture = try MusicPlatformFixture { request in
            switch request.operation {
            case "qr.create": return .object(["imageBase64": .string("AQ=="), "expiresAt": .number(1_800_000_180_000)])
            case "qr.cancel": await physical.wait(); return .object(["cancelled": .bool(true)])
            default: throw MusicPlatformFixture.Failure.remote("UNSUPPORTED")
            }
        }
        let client = fixture.makeClient(), provider = MusicNodeAccountProvider(platform: .netease, client: client)
        addTeardownBlock {
            await caller.release(); await physical.release()
            try await client.close(); await fixture.runtime.close()
        }
        let id = UUID()
        _ = try await provider.createQR(id: id)
        let cancellation = MusicAccountQueueOwnerCall("cancelled caller QR cleanup completed") {
            await caller.wait()
            await provider.cancelQR(id)
        }
        defer { cancellation.cancel() }
        await fulfillment(of: [caller.entered], timeout: 3)
        cancellation.cancel()
        await caller.release()
        await fulfillment(of: [physical.entered], timeout: 3)
        XCTAssertNil(cancellation.result, "A cancelled caller still awaits the actual Node cancel")
        await provider.cancelQR(id)
        await denied(.staleResult) { _ = try await provider.pollQR(id: id) }
        await physical.release()
        await fulfillment(of: [physical.departed, cancellation.finished], timeout: 3)
        _ = try cancellation.value()
        await provider.revoke()
        let requests = fixture.requests().filter { $0.transportOperation == "request" }
        XCTAssertEqual(requests.map(\.operation), ["qr.create", "qr.cancel"])
        XCTAssertEqual(requests.last?.parameters[music: "challengeId"], .string(id.uuidString.lowercased()))
        XCTAssertEqual(requests.last?.candidate, .null)
    }

    func testCancelledCreateCatchWaitsForItsSinglePhysicalCancelAndNeverInstallsLateChallenge() async throws {
        let creation = MusicAccountQueueOwnerGate("QR late create reply")
        let physical = MusicAccountQueueOwnerGate("cancelled create physical cleanup")
        let fixture = try MusicPlatformFixture { request in
            switch request.operation {
            case "qr.create":
                await creation.wait()
                return .object(["imageBase64": .string("AQ=="), "expiresAt": .number(1_800_000_180_000)])
            case "qr.cancel": await physical.wait(); return .object(["cancelled": .bool(true)])
            default: throw MusicPlatformFixture.Failure.remote("UNSUPPORTED")
            }
        }
        let client = fixture.makeClient(), provider = MusicNodeAccountProvider(platform: .netease, client: client)
        addTeardownBlock {
            await creation.release(); await physical.release()
            try await client.close(); await fixture.runtime.close()
        }
        let id = UUID()
        let creating = MusicAccountQueueOwnerCall("cancelled QR create physically settled") { try await provider.createQR(id: id) }
        defer { creating.cancel() }
        await fulfillment(of: [creation.entered], timeout: 3)
        creating.cancel()
        await fulfillment(of: [physical.entered], timeout: 3)
        XCTAssertNil(creating.result, "The create catch must not finish before its cancel reply")
        await creation.release()
        await fulfillment(of: [creation.departed], timeout: 3)
        await provider.cancelQR(id)
        await denied(.staleResult) { _ = try await provider.pollQR(id: id) }
        await physical.release()
        await fulfillment(of: [physical.departed, creating.finished], timeout: 3)
        XCTAssertThrowsError(try creating.value()) { XCTAssertEqual($0 as? MusicServiceError, .cancelled) }
        await provider.revoke()
        let requests = fixture.requests().filter { $0.transportOperation == "request" }
        XCTAssertEqual(requests.map(\.operation), ["qr.create", "qr.cancel"])
        XCTAssertEqual(requests.last?.parameters[music: "challengeId"], .string(id.uuidString.lowercased()))
        XCTAssertEqual(requests.last?.candidate, .null)
    }

    func testCollectionReadsUseCurrentLocatorAndPreserveOrderDuplicatesAndLongIDs() async throws {
        let ids = ["900719925474099312", "canonical-alias", "900719925474099312"]
        let songs = try ids.map { try MusicPlatformFixture.song(MusicAccountFixture.track($0, platform: "kg")) }
        let locator: RuntimeValue = .object(["listId": .string("current-list"), "type": .number(2)])
        let fixture = try MusicPlatformFixture { request in
            switch request.operation {
            case "personalPlaylists":
                return .object(["items": .array([.object(["id": .string("owner:current"),
                    "name": .string("Renamed while browsing"), "owned": .bool(true), "locator": locator,
                    "action": .object(["type": .string("playlist"), "id": .string("actual-target")])])]),
                    "more": .bool(false)])
            case "playlist.complete":
                XCTAssertEqual(request.parameters[music: "id"], .string("actual-target"))
                XCTAssertEqual(request.parameters[music: "personal"], locator)
                return .object(["items": .array(songs), "total": .number(999), "unused": .null])
            default: throw MusicPlatformFixture.Failure.remote("UNSUPPORTED")
            }
        }
        let client = fixture.makeClient(), provider = MusicNodeAccountProvider(platform: .kugou, client: client)
        let directory = try await provider.collectionRows()
        XCTAssertEqual(directory.count, 1)
        let rows = try await provider.tracks(collectionID: "owner:current")
        XCTAssertEqual(try rows.map { try $0[music: "songmid"].musicRequiredText() }, ids)
        XCTAssertEqual(fixture.requests().map(\.operation), ["personalPlaylists", "playlist.complete"],
                       "Ordinary reads do not double-read directory or members to prove a snapshot")
        try await client.close(); await fixture.runtime.close()
    }

    func testShortDirectoryPageUsesExplicitContinuationWithoutRequiringCountsOrCover() async throws {
        let fixture = try MusicPlatformFixture { request in
            let second = request.parameters[music: "page"] == .number(2)
            XCTAssertEqual(request.operation, "personalPlaylists")
            if second {
                XCTAssertEqual(request.parameters[music: "continuation"], .object(["page": .number(2), "offset": .number(17)]))
            }
            return .object(["items": .array([.object(["id": .string(second ? "actual-second" : "first"),
                "name": .string(second ? "New title" : "Old title"), "owned": .bool(false)])]),
                "more": .bool(!second),
                "continuation": second ? .null : .object(["page": .number(2), "offset": .number(17)])])
        }
        let client = fixture.makeClient(), provider = MusicNodeAccountProvider(platform: .qq, client: client)
        let rows = try await provider.collectionRows()
        XCTAssertEqual(try rows.map { try $0[music: "id"].musicRequiredText() }, ["first", "actual-second"])
        XCTAssertEqual(fixture.requests().count, 2)
        try await client.close(); await fixture.runtime.close()
    }

    func testCanonicalAudioOptionalFactsAndChorusAreConsumedWithoutMembershipProbe() async throws {
        let peer = MusicAccountRuntimePeer(clock: MusicAccountTestClock())
        await peer.replies("audio", .object(["url": .string("https://media.invalid/current.mp3?signature=a%2Bb"),
            "actualQuality": .string("128k"), "duration": .number(240.25),
            "full": .bool(true), "expiresAt": .number(1_800_000_120_000)]))
        await peer.replies("chorus", .object(["point": .null]))
        let fixture = try MusicPlatformFixture { try await peer.respond($0) }
        let client = fixture.makeClient(), provider = MusicNodeAccountProvider(platform: .netease, client: client)
        let value = try await provider.publicStandardAudio(nativeID: "canonical-id")
        let audio = try MusicPlatformDTO.audio(value)
        XCTAssertEqual(audio.url.absoluteString, "https://media.invalid/current.mp3?signature=a%2Bb")
        XCTAssertEqual(audio.delivery?.quality, "128k"); XCTAssertEqual(audio.duration, 240.25)
        XCTAssertTrue(audio.fileCachePolicy.allowed); XCTAssertNil(audio.fileCachePolicy.licenseExpiresAt)
        XCTAssertEqual(audio.expiresAt, Date(timeIntervalSince1970: 1_800_000_120))
        let chorus = try await provider.publicChorus(nativeID: "canonical-id")
        XCTAssertNil(chorus)
        await peer.replies("chorus", .object(["point": .object(["start": .number(60.25), "end": .number(89.75)])]))
        let point = try await provider.chorus(nativeID: "canonical-id")
        XCTAssertEqual(point?.start, 60.25); XCTAssertEqual(point?.end, 89.75)
        XCTAssertEqual(fixture.requests().map(\.operation), ["audio", "chorus", "chorus"])
        XCTAssertEqual(fixture.requests().first?.parameters[music: "id"], .string("canonical-id"))
        try await client.close(); await fixture.runtime.close()
    }

    func testRequiredCanonicalAudioAndChorusFieldsStillFailAtTheirConsumer() async throws {
        XCTAssertThrowsError(try MusicPlatformDTO.audio(.object(["url": .null]))) {
            XCTAssertEqual($0 as? MusicServiceError, .upstreamInvalid)
        }
        let fixture = try MusicPlatformFixture { _ in .object(["point": .object(["start": .number(1)])]) }
        let client = fixture.makeClient(), provider = MusicNodeAccountProvider(platform: .kugou, client: client)
        await denied(.upstreamInvalid) { _ = try await provider.chorus(nativeID: "current") }
        try await client.close(); await fixture.runtime.close()
    }

    func testSMSUsesCanonicalReceiptClockAndTypedFailures() async throws {
        let clock = MusicAccountTestClock(), peer = MusicAccountRuntimePeer(clock: MusicAccountTestClock())
        let fixture = try MusicPlatformFixture { try await peer.respond($0) }
        let client = fixture.makeClient(), provider = MusicNodeAccountProvider(platform: .netease, client: client, clock: { clock.now() })
        let receipt = try await provider.sendSMS(countryCode: "86", phone: "synthetic-phone", attemptID: UUID())
        XCTAssertEqual(receipt.retryAt, clock.now().addingTimeInterval(60))
        for (code, detail) in [("INVALID_PHONE", MusicSMSFailure.Detail.invalidPhone), ("INVALID_CODE", .invalidCode),
                               ("ACCOUNT_SELECTION_REQUIRED", .accountSelectionRequired)] {
            await peer.fail("sms.login", code: code)
            do {
                _ = try await provider.loginSMS(countryCode: "86", phone: "synthetic-phone", code: "synthetic-code", attemptID: UUID())
                XCTFail("Expected typed SMS failure")
            } catch { XCTAssertEqual((error as? MusicSMSFailure)?.detail, detail) }
        }
        let requests = fixture.requests()
        XCTAssertTrue(requests.allSatisfy { $0.candidate == .null && $0.fields[music: "expectedSession"] == .null })
        XCTAssertEqual(requests.first?.parameters[music: "phone"], .string("synthetic-phone"))
        try await client.close(); await fixture.runtime.close()
    }

    func testHomeRequestsKeepOpaqueContinuationAndConsumeDynamicCanonicalSections() async throws {
        let peer = MusicAccountRuntimePeer(clock: MusicAccountTestClock())
        let fixture = try MusicPlatformFixture { try await peer.respond($0) }
        let client = fixture.makeClient(), provider = MusicNodeAccountProvider(platform: .netease, client: client)
        let continuation = try MusicNativeHomeContinuation(cursor: "900719925474099312",
            blockCodeOrderList: #"["current","another"]"#, loadedPositionCodes: ["position/one", "position/two"])
        let requests: [MusicNativeHomeRequest] = [.home(), .featured(continuation: continuation),
            .artistActivity(before: "999999999999999999"), .vipSongs,
            .songMetadata(ids: ["canonical-id", "900719925474099312"]),
            .playlistMetadata(ids: ["current-list"]), .heartbeatSeed]
        for (index, request) in requests.enumerated() {
            let operation = ["home", "home", "artistActivity", "channel", "songs", "playlists", "heartbeat.seed"][index]
            let payload: RuntimeValue = .object(["sections": .array((0...index).reversed().map {
                .object(["id": .string("current-\($0)"), "title": .string("Current section")])
            }), "unused": index.isMultiple(of: 2) ? .null : .object(["future": .bool(true)])])
            await peer.replies(operation, payload)
            let bytes = try await provider.homePayload(request, authorize: {})
            XCTAssertEqual(try RuntimeJSON.decode(bytes), payload)
        }
        let recorded = fixture.requests()
        XCTAssertEqual(recorded.map(\.operation), ["home", "home", "artistActivity", "channel", "songs", "playlists", "heartbeat.seed"])
        XCTAssertEqual(recorded[1].parameters[music: "featured"], .bool(true))
        let forwarded = recorded[1].parameters[music: "continuation"]
        XCTAssertEqual(forwarded[music: "cursor"], .string("900719925474099312"))
        XCTAssertEqual(forwarded[music: "blockCodeOrderList"], .string(#"["current","another"]"#))
        XCTAssertEqual(try RuntimeJSON.decode(Data(forwarded[music: "loadedPositionCodes"].musicText.utf8)),
                       .array([.string("position/one"), .string("position/two")]))
        XCTAssertEqual(recorded[2].parameters[music: "before"], .string("999999999999999999"))
        XCTAssertEqual(recorded[3].parameters[music: "id"], .string("vip"))
        XCTAssertEqual(recorded[4].parameters[music: "ids"], .array([.string("canonical-id"), .string("900719925474099312")]))
        try await client.close(); await fixture.runtime.close()
    }

    func testFMConsumesActualCanonicalRowsOptionalArtworkAndNativeActionParameters() async throws {
        let tracks = try (0..<4).map { try MusicAccountFixture.track("current-\($0)", platform: "wy") }
        let peer = MusicAccountRuntimePeer(clock: MusicAccountTestClock())
        await peer.replies("fm", try MusicPlatformFixture.page(tracks, platform: "wy", total: 1))
        let fixture = try MusicPlatformFixture { try await peer.respond($0) }
        let client = fixture.makeClient(), provider = MusicNodeAccountProvider(platform: .netease, client: client)
        let response = try await provider.fm(.fm(mode: .scene, submode: "CURRENT_SCENE", entry: .channel),
            preview: true, authorize: {})
        XCTAssertEqual(response.tracks.count, tracks.count); XCTAssertNil(response.artwork)
        XCTAssertEqual(try response.tracks.map { try RuntimeJSON.decode($0.data)[music: "songmid"].musicRequiredText() },
                       (0..<4).map { "current-\($0)" })
        await peer.replies("fm", .object(["cover": .string("https://other-cdn.invalid/cover.jpg?variant=a%2Bb")]))
        let cover = try await provider.fm(.ruleFM(ruleID: "", tabTagID: "current-tab", sourceName: "Current"),
            preview: true, authorize: {})
        XCTAssertTrue(cover.tracks.isEmpty)
        XCTAssertEqual(cover.artwork?.absoluteString, "https://other-cdn.invalid/cover.jpg?variant=a%2Bb")
        let requests = fixture.requests()
        XCTAssertEqual(requests.count, 2)
        XCTAssertEqual(requests[0].parameters[music: "submode"], .string("CURRENT_SCENE"))
        XCTAssertEqual(requests[0].parameters[music: "preview"], .bool(true))
        XCTAssertEqual(requests[1].parameters[music: "ruleId"], .string(""))
        XCTAssertEqual(requests[1].parameters[music: "tabTagId"], .string("current-tab"))
        try await client.close(); await fixture.runtime.close()
    }

    func testRevokedNativeAuthorizationRejectsLateCanonicalHomeWithoutContentComparison() async throws {
        let gate = MusicAccountQueueOwnerGate("canonical home reply")
        let fixture = try MusicPlatformFixture { _ in await gate.wait(); return .object(["sections": .array([])]) }
        let client = fixture.makeClient(), provider = MusicNodeAccountProvider(platform: .netease, client: client)
        let authorization = MusicQueueAuthorization()
        let pending = MusicAccountQueueOwnerCall("retired canonical home") {
            try await provider.homePayload(.home(), authorize: { try authorization.check() })
        }
        await fulfillment(of: [gate.entered], timeout: 3)
        authorization.revoke(); await gate.release()
        await fulfillment(of: [pending.finished, gate.departed], timeout: 3)
        XCTAssertThrowsError(try pending.value()) { XCTAssertEqual($0 as? MusicServiceError, .staleAccess) }
        XCTAssertEqual(fixture.requests().map(\.operation), ["home"])
        try await client.close(); await fixture.runtime.close()
    }

    private func denied(_ expected: MusicServiceError, _ operation: () async throws -> Void) async {
        do { try await operation(); XCTFail("Expected \(expected)") }
        catch { XCTAssertEqual(error as? MusicServiceError, expected) }
    }
}
