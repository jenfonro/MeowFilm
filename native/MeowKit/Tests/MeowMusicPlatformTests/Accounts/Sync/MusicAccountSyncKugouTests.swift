import Foundation
import XCTest
import MeowAccounts
import MeowMusicFeature
@testable import MeowMusicPlatform

private actor SyncKugouHTTP: MusicAccountHTTPTransport {
    let reads: MusicKugouTestHTTP
    private var recordedRequests: [MusicAccountHTTPRequest] = []
    private var ambiguousMapping = false, wrongAlbum = false, malformedSuccess = false
    private var automaticDetails = false, invalidDetail: String?
    private var subscriptionHeader: MusicAccountHTTPResponse?
    init(_ reads: MusicKugouTestHTTP) { self.reads = reads }
    func configure(ambiguousMapping: Bool = false, wrongAlbum: Bool = false, malformedSuccess: Bool = false,
                   subscriptionHeader: MusicAccountHTTPResponse? = nil) {
        self.ambiguousMapping = ambiguousMapping; self.wrongAlbum = wrongAlbum; self.malformedSuccess = malformedSuccess
        self.subscriptionHeader = subscriptionHeader
    }
    func recorded() -> [MusicAccountHTTPRequest] { recordedRequests }
    func details(invalid: String? = nil) { automaticDetails = true; invalidDetail = invalid }
    func send(_ request: MusicAccountHTTPRequest) async throws -> MusicAccountHTTPResponse {
        recordedRequests.append(request)
        if automaticDetails, request.url.path == "/v1/audio/audio" {
            guard MusicKugouRoute.permits(request) else { throw MusicServiceError.invalidRequest }
            try MusicKugouFixture.validateSignature(request)
            let body = try MusicQQWire.decode(request.body ?? Data())
            let hash = try MusicKugouWire.hash((body["data"] as? [[String: Any]])?.first?["hash"])
            var row = MusicKugouFixture.song()
            row["hash"] = hash == invalidDetail ? MusicKugouFixture.baseHash : hash
            return try MusicKugouFixture.response(["status": 1, "data": [row]])
        }
        guard let route = MusicAccountSyncRoute.Kugou(rawValue: request.url.path) else { return try await reads.send(request) }
        guard MusicAccountSyncRoute.permits(request), request.headers.keys.allSatisfy({
            !["cookie", "authorization"].contains($0.lowercased())
        }) else { throw MusicServiceError.invalidRequest }
        if route == .hashMapping {
            guard try MusicKugouFixture.fields(request).isEmpty else { throw MusicServiceError.invalidRequest }
            let value: [String: Any] = ["album_audio_id": 22, "hash": MusicKugouFixture.baseHash]
            return try MusicKugouFixture.response(["status": 1, "data": [ambiguousMapping ? [value, value] : [value]]])
        }
        if route == .metadata {
            try MusicKugouFixture.validateSignature(request)
            guard request.headers["KG-TID"] == "238" else { throw MusicServiceError.invalidRequest }
            return try MusicKugouFixture.response(["status": 1, "data": [[
                "__status": 1, "base": ["album_audio_id": 22, "album_id": wrongAlbum ? 99 : 11, "audio_name": "Verified"],
                "audio_info": ["hash": MusicKugouFixture.baseHash, "audio_id": 21, "timelength": 180000],
                "album_info": ["album_name": "Album"], "authors": [["base": ["author_name": "Fixture"]]]
            ]]])
        }
        if !route.cloud { try MusicKugouFixture.validateSignature(request) }
        else {
            let fields = try MusicKugouFixture.fields(request)
            guard fields["signature"] == nil, fields["token"] == nil, fields["userid"] == nil, fields["uuid"] == nil else {
                throw MusicServiceError.invalidRequest
            }
        }
        if route.encrypted {
            // The fixed public RSA key has no test private key. Verify the
            // wire envelope, then return an explicit rejection, not fake success.
            return try MusicKugouFixture.response(malformedSuccess ? ["status": 1] : ["status": 0, "errcode": 20028])
        }
        if route == .subscriptionInfo, let subscriptionHeader { return subscriptionHeader }
        if route == .create { return try MusicKugouFixture.response(["status": 1, "data": ["listid": 8]]) }
        return try MusicKugouFixture.response(["status": 1])
    }
}

@MainActor final class MusicAccountSyncKugouTests: XCTestCase {
    func testEveryAdditionIsResolvedBeforeFirstWriteAndMismatchDoesNotPartiallyAdd() async throws {
        for invalid in [false, true] {
            let clock = MusicAccountTestClock(), reads = MusicKugouTestHTTP(clock: clock), http = SyncKugouHTTP(reads)
            let first = String(repeating: "E", count: 32), second = String(repeating: "F", count: 32)
            await http.details(invalid: invalid ? second : nil)
            let provider = KugouMusicAccountAdapter(transport: http, now: { clock.now() })
            let credential = try MusicKugouFixture.credential(), authorization = MusicAccountSyncAuthorization {}
            let snapshot = try await provider.syncRead(kinds: [.favorites], credential: credential, guard: authorization)
            let collection = try XCTUnwrap(snapshot.collections.first)
            let members = try [first, second].map { hash in
                MusicLibrarySyncMember(nativeID: hash, occurrenceID: hash,
                    track: try MusicQQWire.json(["hash": hash, "albumId": "11", "albumAudioId": "22"]))
            }
            let result = try await provider.syncWrite(.init(action: .add, collectionID: collection.id, before: collection, members: members),
                                                      credential: credential, guard: authorization)
            let requests = await http.recorded(), additions = requests.filter { $0.url.path == "/cloudlist.service/v6/add_song" }
            if invalid {
                if case .notDispatched(.targetChanged) = result {} else { XCTFail("identity mismatch") }
                XCTAssertTrue(additions.isEmpty)
            } else {
                if case .acknowledged = result {} else { XCTFail("actual add") }
                XCTAssertEqual(additions.count, 1)
                let body = try MusicQQWire.decode(XCTUnwrap(additions.first?.body))
                let rows = try XCTUnwrap(body["data"] as? [[String: Any]])
                XCTAssertEqual(rows.compactMap { $0["hash"] as? String }, [first, second])
                XCTAssertEqual(rows.compactMap { $0["album_id"] as? Int }, [11, 11])
                XCTAssertEqual(rows.compactMap { $0["mixsongid"] as? Int }, [22, 22])
                let writeIndex = try XCTUnwrap(requests.firstIndex { $0.url.path == "/cloudlist.service/v6/add_song" })
                let detailIndices = requests.indices.filter { requests[$0].url.path == "/v1/audio/audio" }
                XCTAssertEqual(detailIndices.count, 2); XCTAssertTrue(detailIndices.allSatisfy { $0 < writeIndex })
                XCTAssertTrue(requests[(detailIndices.last! + 1)..<writeIndex].contains { $0.url.path == "/v7/get_all_list" })
            }
        }
    }
    func testRemoveUsesAllFileIDOccurrencesNotHashesAndNoAutomaticRetry() async throws {
        let clock = MusicAccountTestClock(), reads = MusicKugouTestHTTP(clock: clock), http = SyncKugouHTTP(reads)
        let provider = KugouMusicAccountAdapter(transport: http, now: { clock.now() })
        let credential = try MusicKugouFixture.credential(), authorization = MusicAccountSyncAuthorization {}
        let snapshot = try await provider.syncRead(kinds: [.favorites], credential: credential, guard: authorization)
        let collection = try XCTUnwrap(snapshot.collections.first)
        XCTAssertEqual(collection.members.map(\.occurrenceID), ["501", "502"])
        XCTAssertEqual(Set(collection.members.map(\.nativeID)), [MusicKugouFixture.baseHash])
        _ = try await provider.syncWrite(.init(action: .remove, collectionID: collection.id, before: collection,
            members: [collection.members[0]]), credential: credential, guard: authorization)
        let requests = await http.recorded(), writes = requests.filter { $0.url.path == "/v4/delete_songs" }
        XCTAssertEqual(writes.count, 1)
        let body = try MusicQQWire.decode(XCTUnwrap(writes.first?.body))
        XCTAssertEqual(body["data"] as? [[String: Int]], [["fileid": 501], ["fileid": 502]])
        XCTAssertEqual(body["listid"] as? String, "7"); XCTAssertEqual(body["type"] as? Int, 0)
    }

    func testExactHashMetadataFallbackIsBoundedAndRejectsAmbiguityBeforeAnyMutation() async throws {
        for ambiguous in [false, true] {
            let clock = MusicAccountTestClock(), reads = MusicKugouTestHTTP(clock: clock), http = SyncKugouHTTP(reads)
            var raw = MusicKugouFixture.song()
            raw["name"] = "未知歌曲"; raw["album_id"] = 0; raw["album_audio_id"] = 0
            await reads.respond("/v1/audio/audio", [try MusicKugouFixture.response(["status": 1, "data": [raw]])])
            await http.configure(ambiguousMapping: ambiguous)
            let provider = KugouMusicAccountAdapter(transport: http, now: { clock.now() })
            let context = try await provider.context(MusicKugouFixture.credential())
            let member = MusicLibrarySyncMember(nativeID: MusicKugouFixture.baseHash, occurrenceID: MusicKugouFixture.baseHash,
                track: try MusicQQWire.json(["hash": MusicKugouFixture.baseHash, "albumId": "0", "albumAudioId": "0"]))
            do {
                let resource = try await provider.syncAddition(member, context: context, guard: MusicAccountSyncAuthorization {})
                XCTAssertFalse(ambiguous)
                XCTAssertEqual(resource["hash"] as? String, MusicKugouFixture.baseHash)
                XCTAssertEqual(resource["album_id"] as? Int, 11); XCTAssertEqual(resource["mixsongid"] as? Int, 22)
                XCTAssertEqual(resource["name"] as? String, "Verified")
            } catch { XCTAssertTrue(ambiguous); XCTAssertEqual(error as? MusicServiceError, .targetChanged) }
            let requests = await http.recorded()
            XCTAssertEqual(requests.filter { $0.url.path == "/v2/album_audio/audio" }.count, 1)
            XCTAssertEqual(requests.filter { $0.url.path == "/kmr/v2/audio" }.count, ambiguous ? 0 : 1)
            XCTAssertFalse(requests.contains { MusicAccountSyncRoute.Kugou(rawValue: $0.url.path).map { ![.hashMapping, .metadata].contains($0) } ?? false })
            let mapping = try XCTUnwrap(requests.first { $0.url.path == "/v2/album_audio/audio" })
            let body = try MusicQQWire.decode(XCTUnwrap(mapping.body))
            XCTAssertNil(body["token"]); XCTAssertNil(body["userid"])
        }
    }

    func testLegacyAndCloudWriteEnvelopesRetainRouteScopesAndPlaintextSuccessCannotBypassDecrypt() async throws {
        let clock = MusicAccountTestClock(), reads = MusicKugouTestHTTP(clock: clock), http = SyncKugouHTTP(reads)
        let provider = KugouMusicAccountAdapter(transport: http, now: { clock.now() })
        let context = try await provider.context(MusicKugouFixture.credential()), authorization = MusicAccountSyncAuthorization {}
        for route in [MusicAccountSyncRoute.Kugou.delete, .rename, .order] {
            do { _ = try await provider.syncRequest(route, payload: ["listid": 7], context: context, guard: authorization); XCTFail("explicit rejection") }
            catch { XCTAssertEqual(error as? MusicServiceError, .verificationRequired) }
        }
        var requests = await http.recorded()
        XCTAssertEqual(requests.count, 3)
        for request in requests {
            let query = try MusicKugouFixture.fields(request)
            XCTAssertEqual(query["p"]?.count, 256); XCTAssertEqual(query["p"], query["p"]?.uppercased())
            XCTAssertEqual(query["key"], MusicKugouCrypto.paramsKey(1_800_000_000))
            let data = try XCTUnwrap(request.body)
            if request.url.path == "/v2/delete_list" {
                let cipher = try XCTUnwrap(Data(base64Encoded: data))
                XCTAssertEqual(cipher.count % 16, 0)
                XCTAssertEqual(request.headers["Content-Type"], "application/x-www-form-urlencoded")
                XCTAssertNotNil(query["token"]); XCTAssertNotNil(query["signature"])
            } else {
                XCTAssertEqual(data.count % 16, 0); XCTAssertNil(query["token"]); XCTAssertNil(query["signature"])
            }
        }
        await http.configure(malformedSuccess: true)
        do { _ = try await provider.syncRequest(.rename, payload: ["listid": 7], context: context, guard: authorization); XCTFail("bad encrypted success") }
        catch { XCTAssertEqual(error as? MusicServiceError, .upstreamInvalid) }
        requests = await http.recorded(); XCTAssertEqual(requests.count, 4)
    }

    func testKugouDirectorySeparatesAccountSubscriptionLocatorAndNeverReadsSubscribedMembers() async throws {
        let clock = MusicAccountTestClock(), reads = MusicKugouTestHTTP(clock: clock), http = SyncKugouHTTP(reads)
        let subscription = MusicKugouFixture.collection(listID: 9, type: 1, owner: 99, name: "Network",
            special: 0, count: 800, global: "public-global")
        await reads.respond("/v7/get_all_list", [try MusicKugouFixture.directory([MusicKugouFixture.collection(), subscription])])
        let provider = KugouMusicAccountAdapter(transport: http, now: { clock.now() })
        let credential = try MusicKugouFixture.credential(), authorization = MusicAccountSyncAuthorization {}
        let full = try await provider.syncRead(kinds: [.favorites, .playlist], credential: credential, guard: authorization)
        XCTAssertEqual(full.collections.map(\.id), ["0:7"])
        XCTAssertEqual(full.subscriptions, [.init(remoteID: "public-global", relationID: "1:9", name: "Network", count: 800)])
        let before = await http.recorded()
        XCTAssertFalse(before.contains { $0.url.path == "/pubsongs/v2/get_other_list_file_nofilt" })
        let directory = try await provider.syncReadSubscriptions(credential: credential, guard: authorization)
        XCTAssertTrue(directory.collections.isEmpty); XCTAssertEqual(directory.subscriptions, full.subscriptions)
        let after = await http.recorded()
        XCTAssertEqual(after.filter { $0.url.path == "/v4/get_list_all_file_v3" }.count,
                       before.filter { $0.url.path == "/v4/get_list_all_file_v3" }.count)
        XCTAssertFalse(after.contains { $0.url.path == "/pubsongs/v2/get_other_list_file_nofilt" })
    }

    func testKugouUnsubscribeRequiresCurrentAccountLocatorInsteadOfTheAuthorsListID() async throws {
        let clock = MusicAccountTestClock(), reads = MusicKugouTestHTTP(clock: clock), http = SyncKugouHTTP(reads)
        let row = MusicKugouFixture.collection(listID: 9, type: 1, owner: 99, name: "Network",
            special: 0, count: 800, global: "public-global")
        await reads.respond("/v7/get_all_list", [try MusicKugouFixture.directory([row])])
        let provider = KugouMusicAccountAdapter(transport: http, now: { clock.now() })
        let credential = try MusicKugouFixture.credential(), authorization = MusicAccountSyncAuthorization {}
        let snapshot = try await provider.syncReadSubscriptions(credential: credential, guard: authorization)
        let wrong = try await provider.syncWrite(.init(action: .unsubscribe, collectionID: "public-global",
            directoryRevision: snapshot.revision,
            beforeSubscription: .init(remoteID: "public-global", relationID: "1:87", name: "Network", count: 800)),
            credential: credential, guard: authorization)
        if case .notDispatched(.revisionConflict) = wrong {} else { XCTFail("author locator must not authorize unsubscribe") }
        var requests = await http.recorded()
        XCTAssertFalse(requests.contains { $0.url.path == "/v2/delete_list" })
        let before = try XCTUnwrap(snapshot.subscriptions.first)
        do {
            _ = try await provider.syncWrite(.init(action: .unsubscribe, collectionID: before.remoteID,
                directoryRevision: snapshot.revision, beforeSubscription: before), credential: credential, guard: authorization)
            XCTFail("fixture returns an explicit encrypted-route rejection, not success")
        } catch { XCTAssertEqual(error as? MusicServiceError, .verificationRequired) }
        requests = await http.recorded()
        let writes = requests.filter { $0.url.path == "/v2/delete_list" }
        XCTAssertEqual(writes.count, 1); XCTAssertEqual(writes.first?.headers["x-router"], "cloudlist.service.kugou.com")
        XCTAssertFalse(requests.contains { ["/cloudlist.service/v5/add_list", "/v4/get_list_all_file_v3",
            "/pubsongs/v2/get_other_list_file_nofilt"].contains($0.url.path) })
    }

    func testKugouUnprovedHeaderCannotDispatchSubscriptionAndRevokedGrantDoesNotReadOrWrite() async throws {
        let clock = MusicAccountTestClock(), reads = MusicKugouTestHTTP(clock: clock), http = SyncKugouHTTP(reads)
        let provider = KugouMusicAccountAdapter(transport: http, now: { clock.now() })
        let credential = try MusicKugouFixture.credential(), authorization = MusicAccountSyncAuthorization {}
        let snapshot = try await provider.syncReadSubscriptions(credential: credential, guard: authorization)
        let mutation = MusicLibrarySyncMutation(action: .subscribe, collectionID: "other-global", name: "Network",
            directoryRevision: snapshot.revision)
        // The fixture supplies no proven author header. It must not be filled
        // by parsing a globalID, scanning members or guessing owner/list IDs.
        let unproved = try await provider.syncWrite(mutation, credential: credential, guard: authorization)
        if case .notDispatched = unproved {} else { XCTFail("missing author identity") }
        var requests = await http.recorded()
        XCTAssertEqual(requests.filter { $0.url.path == "/v3/get_list_info" }.count, 1)
        XCTAssertFalse(requests.contains { ["/cloudlist.service/v5/add_list", "/v2/delete_list",
            "/v4/get_list_all_file_v3", "/pubsongs/v2/get_other_list_file_nofilt"].contains($0.url.path) })
        let count = requests.count
        authorization.revoke()
        let revoked = try await provider.syncWrite(mutation, credential: credential, guard: authorization)
        if case .notDispatched = revoked {} else { XCTFail("revoked grant") }
        requests = await http.recorded(); XCTAssertEqual(requests.count, count)
    }

    func testKugouSubscribeUsesVerifiedAuthorHeaderAndTypeOneRelationshipWithoutReadingMembers() async throws {
        let clock = MusicAccountTestClock(), reads = MusicKugouTestHTTP(clock: clock), http = SyncKugouHTTP(reads)
        let remoteName = String(repeating: "T", count: 201)
        // Synthetic identities in the data[] shape independently observed by
        // the Windows peer's 2026-11-09 anonymous /v3/get_list_info evidence.
        await http.configure(subscriptionHeader: try MusicKugouFixture.response(["status": 1, "data": [[
            "global_collection_id": "opaque-global", "list_create_userid": 99, "list_create_listid": 87,
            "name": remoteName, "count": 2001, "pic": ""
        ]]]))
        let provider = KugouMusicAccountAdapter(transport: http, now: { clock.now() })
        let credential = try MusicKugouFixture.credential(), authorization = MusicAccountSyncAuthorization {}
        let snapshot = try await provider.syncReadSubscriptions(credential: credential, guard: authorization)
        let result = try await provider.syncWrite(.init(action: .subscribe, collectionID: "opaque-global", name: remoteName,
            directoryRevision: snapshot.revision), credential: credential, guard: authorization)
        if case .acknowledged = result {} else { XCTFail("subscription dispatch") }
        let requests = await http.recorded()
        let header = try XCTUnwrap(requests.first { $0.url.path == "/v3/get_list_info" })
        XCTAssertEqual(header.headers["x-router"], "pubsongs.kugou.com")
        let lookup = try MusicQQWire.decode(XCTUnwrap(header.body))
        XCTAssertEqual(lookup["data"] as? [[String: String]], [["global_collection_id": "opaque-global"]])
        let writes = requests.filter { $0.url.path == "/cloudlist.service/v5/add_list" }
        XCTAssertEqual(writes.count, 1)
        let write = try XCTUnwrap(writes.first), body = try MusicQQWire.decode(XCTUnwrap(write.body))
        XCTAssertEqual(Set(body.keys), ["userid", "token", "total_ver", "name", "type", "source", "is_pri",
            "list_create_userid", "list_create_listid", "list_create_gid", "from_shupinmv"])
        XCTAssertEqual(body["userid"] as? String, "42")
        XCTAssertEqual(body["name"] as? String, remoteName)
        XCTAssertEqual(body["type"] as? Int, 1); XCTAssertEqual(body["is_pri"] as? Int, 0)
        XCTAssertEqual(body["list_create_userid"] as? String, "99")
        XCTAssertEqual(body["list_create_listid"] as? String, "87")
        XCTAssertEqual(body["list_create_gid"] as? String, "opaque-global")
        let query = try MusicKugouFixture.fields(write)
        XCTAssertNil(query["last_time"]); XCTAssertNil(query["last_area"])
        XCTAssertFalse(requests.contains { ["/v2/delete_list", "/v4/get_list_all_file_v3",
            "/pubsongs/v2/get_other_list_file_nofilt", "/v1/audio/audio"].contains($0.url.path) })
    }

}
