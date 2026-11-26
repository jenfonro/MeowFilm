import Foundation
import CoreImage
import MeowAccounts
import MeowDebug
import MeowMusicFeature
import MeowRuntime

/// Borrows the one platform module; the native coordinator owns every durable
/// account and admits publication only after its actual secure-store receipt.
actor MusicNodeAccountProvider {
    let platform: MusicAccountPlatform
    let client: MusicPlatformClient
    private var collectionTargets: [String: RuntimeObject] = [:]
    private var epoch = UUID()
    private var challenges = Set<UUID>()
    private var smsWork: [UUID: Task<RuntimeValue, Error>] = [:]
    private(set) var revokePublicationFailed = false
    init(platform: MusicAccountPlatform, client: MusicPlatformClient) {
        self.platform = platform; self.client = client
    }
    func call(_ operation: String, _ parameters: RuntimeObject = [:], candidate: RuntimeValue? = nil,
              authorize: (@Sendable () throws -> Void)? = nil) async throws -> RuntimeValue {
        try await client.request(platform: platform.rawValue, operation: operation, parameters: parameters,
                                 candidate: candidate, authorize: authorize)
    }
    func publish(_ credential: AccountCredential, scope: String, generation: Int,
                 authorization: MusicQueueAuthorization) async throws {
        var fields = try MusicAccountCredential.read(credential)
        guard case .object(var value) = fields else { throw MusicServiceError.invalidResponse }
        value["scope"] = .string(scope); value["generation"] = .number(Double(generation))
        fields = .object(value)
        try await client.publish(platform: platform.rawValue, session: fields,
                                 authorize: { try authorization.check() })
        revokePublicationFailed = false
    }
    func nativeID(_ credential: AccountCredential) throws -> String {
        try MusicAccountCredential.read(credential)[music: "userId"].musicRequiredText()
    }
    private func verified(_ value: RuntimeValue, reusing original: AccountCredential? = nil) throws -> MusicAccountVerified {
        let id = try value[music: "userId"].musicRequiredText()
        let cookie = try value[music: "cookie"].musicRequiredText()
        let credential: AccountCredential
        if let original, case .object(let fields) = try MusicAccountCredential.read(original),
           fields["platform"] == .string(platform.rawValue), fields["userId"] == .string(id),
           fields["cookie"] == .string(cookie) {
            credential = original
        } else {
            credential = try MusicAccountCredential.make(platform: platform, userID: id, cookie: cookie)
        }
        return .init(credential: credential, nickname: value[music: "profile"][music: "displayName"].musicText, nativeID: id)
    }
    func verify(_ credential: AccountCredential) async throws -> MusicAccountVerified {
        try verified(await call("account.verify", candidate: MusicAccountCredential.read(credential)), reusing: credential)
    }
    func refresh(_ credential: AccountCredential) async throws -> MusicAccountVerified {
        try verified(await call("account.refresh", candidate: MusicAccountCredential.read(credential)), reusing: credential)
    }
    func accountProfile() async throws -> MusicAccountProfileSnapshot {
        let value = try await call("account.profile"), profile = value[music: "profile"]
        let rights = value[music: "entitlements"], membership = rights[music: "membership"]
        let vip: Bool? = { if case .bool(let value) = membership[music: "vip"] { return value }; return nil }()
        return .init(profile: .init(nickname: profile[music: "displayName"].musicText,
            avatarURL: MusicArtworkURL.normalized(profile[music: "avatar"].musicText),
            membership: .init(vip: vip, tier: membership[music: "tier"].musicText,
                              expiresAt: MusicPlatformDTO.date(membership[music: "expiresAt"]))),
            recheckAt: MusicPlatformDTO.date(rights[music: "recheckAt"]) ?? .distantPast)
    }
    func createQR(id: UUID) async throws -> MusicLoginChallenge {
        try Task.checkCancellation()
        challenges.insert(id)
        do {
            let value = try await call("qr.create", ["challengeId": .string(id.uuidString.lowercased())], candidate: .null)
            let image: Data
            if case .string(let text) = value[music: "imageBase64"] {
                guard let bytes = Data(base64Encoded: text) else { throw MusicServiceError.upstreamInvalid }; image = bytes
            } else {
                image = try MusicLoginQRCode.encode(value[music: "url"].musicRequiredText())
            }
            guard challenges.contains(id) else { throw MusicServiceError.staleResult }
            guard let expires = MusicPlatformDTO.date(value[music: "expiresAt"]) else { throw MusicServiceError.upstreamInvalid }
            return .init(id: id, platform: platform, image: image, expiresAt: expires)
        } catch { await cancelQR(id); throw error }
    }
    func pollQR(id: UUID) async throws -> MusicAccountLoginResult {
        guard challenges.contains(id) else { throw MusicServiceError.staleResult }
        let value = try await call("qr.poll", ["challengeId": .string(id.uuidString.lowercased())], candidate: .null)
        guard challenges.contains(id) else { throw MusicServiceError.staleResult }
        switch try value[music: "status"].musicRequiredText() {
        case "waiting": return .init(progress: .waiting, verified: nil)
        case "confirming": return .init(progress: .scanned, verified: nil)
        case "authenticated":
            challenges.remove(id); return .init(progress: .confirmed, verified: try verified(value[music: "account"]))
        case "expired", "refused": challenges.remove(id); return .init(progress: .expired, verified: nil)
        default: throw MusicServiceError.upstreamInvalid
        }
    }
    func cancelQR(_ id: UUID) async {
        guard challenges.remove(id) != nil else { return }
        await cancelQRRequest(id)
    }
    private func cancelQRRequest(_ id: UUID) async {
        // A retired challenge still needs its real Node cancel when the caller
        // is already cancelled. Keep that cleanup independent and await it.
        await Task.detached { [self] in
            _ = try? await call("qr.cancel", ["challengeId": .string(id.uuidString.lowercased())], candidate: .null)
        }.value
    }
    func revoke() async {
        epoch = UUID()
        collectionTargets.removeAll()
        let retired = challenges; challenges.removeAll()
        let sms = Array(smsWork.values)
        for work in sms { work.cancel() }
        // Admit the reset before suspending on retired work, so a later login's
        // publication cannot be overtaken by this revocation.
        do {
            try await client.publish(platform: platform.rawValue, session: nil)
            revokePublicationFailed = false
        } catch {
            revokePublicationFailed = true
            DebugRuntime.logError(.musicAccount, sub: platform.rawValue, event: "revoke.publishFailed", error: error)
        }
        for id in retired { await cancelQRRequest(id) }
        // smsWork, the native operation slot and the client retain physical
        // settlement. Revoking admission must not await a dispatched send.
    }
    private func sms(_ operation: String, parameters: RuntimeObject, attemptID: UUID) async throws -> RuntimeValue {
        guard smsWork[attemptID] == nil else { throw MusicServiceError.busy }
        let client = client, platform = platform
        let task = Task { try await client.request(platform: platform.rawValue, operation: operation,
                                                    parameters: parameters, candidate: .null) }
        smsWork[attemptID] = task
        defer { smsWork.removeValue(forKey: attemptID) }
        return try await withTaskCancellationHandler { try await task.value } onCancel: { task.cancel() }
    }
    func sendSMS(countryCode: String, phone: String, attemptID: UUID) async throws -> MusicSMSSendReceipt {
        do {
            let value = try await sms("sms.send", parameters: ["phone": .string(phone), "countryCode": .string(countryCode)],
                                     attemptID: attemptID)
            guard value[music: "confirmed"].musicBool else { throw MusicSMSFailure(reason: .commitUnconfirmed) }
            return .init(retryAt: Date().addingTimeInterval(value[music: "retryAfterSeconds"].musicNumber ?? 60))
        } catch let failure as MusicSMSFailure where failure.reason == .commitUnconfirmed {
            throw MusicSMSFailure(reason: failure.reason, retryAt: Date().addingTimeInterval(60), detail: failure.detail)
        }
    }
    func loginSMS(countryCode: String, phone: String, code: String, attemptID: UUID) async throws -> MusicAccountVerified {
        try verified(await sms("sms.login", parameters: ["phone": .string(phone), "countryCode": .string(countryCode),
                                             "captcha": .string(code)], attemptID: attemptID))
    }
    func cancelSMS(_ id: UUID) async {
        smsWork[id]?.cancel()
    }
    func recommendations(kind: String, page: Int) async throws -> MusicAccountRecommendation {
        let operation = platform == .netease ? kind : "recommendations"
        return try MusicPlatformDTO.recommendation(await call(operation,
            ["kind": .string(kind), "page": .number(Double(page))]))
    }
    func collectionRows() async throws -> [RuntimeValue] {
        let own = epoch
        var result: [RuntimeValue] = [], page = 1
        var continuation: RuntimeValue = .null
        var targets: [String: RuntimeObject] = [:]
        while true {
            let value = try await call("personalPlaylists", ["page": .number(Double(page)),
                "kind": .string("all"), "continuation": continuation])
            guard own == epoch else { throw MusicServiceError.staleResult }
            for row in value[music: "items"].musicArray {
                let id = row[music: "id"].musicText
                let action = row[music: "action"]
                var target: RuntimeObject = ["id": action[music: "id"] == .null ? .string(id) : action[music: "id"]]
                if row[music: "owned"].musicBool, row[music: "locator"] != .null {
                    target["personal"] = row[music: "locator"]
                }
                if !id.isEmpty, action[music: "type"] != .string("unsupported") { targets[id] = target }
                result.append(.object(["id": .string(id),
                    "title": .string(row[music: "name"].musicText), "owned": .bool(row[music: "owned"].musicBool),
                    "count": row[music: "count"]]))
            }
            continuation = value[music: "continuation"]
            guard value[music: "more"].musicBool,
                  let next = MusicPlatformDTO.optionalCount(continuation[music: "page"]), next > page else {
                collectionTargets = targets; return result
            }
            page = next
        }
    }
    func tracks(collectionID: String) async throws -> [RuntimeValue] {
        let own = epoch
        // A restored private collection needs its real cloud/public locator,
        // not a comparison with the contents of an earlier page.
        if platform == .kugou, collectionTargets[collectionID] == nil, collectionID.contains(":") {
            _ = try await collectionRows()
            guard own == epoch else { throw MusicServiceError.staleResult }
            guard collectionTargets[collectionID] != nil else { throw MusicServiceError.notFound }
        }
        return try MusicPlatformDTO.rows(await call("playlist.complete",
            collectionTargets[collectionID] ?? ["id": .string(collectionID)]))
    }
    func capabilities(mid: String) async throws -> [String] {
        try await call("audio.options", ["id": .string(mid)])[music: "qualities"].musicRequiredArray().map { try $0.musicRequiredText() }
    }
    func resolve(mid: String, quality: String) async throws -> RuntimeValue {
        try await call("audio", ["id": .string(mid), "quality": .string(quality)])
    }
    func chorus(nativeID: String) async throws -> MusicChorusPoint? {
        let value = try await call("chorus", ["id": .string(nativeID)])[music: "point"]
        guard value != .null else { return nil }
        guard let start = value[music: "start"].musicNumber, let end = value[music: "end"].musicNumber else {
            throw MusicServiceError.upstreamInvalid
        }
        return .init(start: start, end: end)
    }
    func homePayload(_ request: MusicNativeHomeRequest,
                     authorize: @escaping @Sendable () async throws -> Void) async throws -> Data {
        try await authorize()
        let (operation, params) = try MusicPlatformDTO.homeRequest(request)
        let value = try await call(operation, params)
        try await authorize(); return try value.musicData()
    }
    func fm(_ action: MusicHomeAction, preview: Bool, unplaySongIds: [String] = [],
            authorize: @escaping @Sendable () async throws -> Void) async throws -> MusicNativeFMResponse {
        try await authorize(); let (operation, params) = MusicPlatformDTO.action(action, preview: preview, unplaySongIds: unplaySongIds)
        let value = try await call(operation, params); try await authorize()
        return .init(tracks: value[music: "items"] == .null ? [] : try MusicPlatformDTO.tracks(value),
            artwork: MusicArtworkURL.normalized(value[music: "cover"].musicText))
    }
    func homeAction(_ action: MusicHomeAction,
                    authorize: @escaping @Sendable () async throws -> Void) async throws -> MusicAccountRecommendation {
        try await authorize(); let (operation, params) = MusicPlatformDTO.action(action)
        let value = try await call(operation, params); try await authorize()
        return try MusicPlatformDTO.recommendation(value)
    }
    func similarMode(_ request: MusicNativeSimilarModeRequest,
                     authorize: @escaping @Sendable () async throws -> Void) async throws -> MusicNativeSimilarModeBatch {
        try await authorize()
        let operation: String, params: RuntimeObject
        switch request {
        case .heartbeat(let last, let fromHome, let context):
            operation = "heartbeat"
            params = ["songId": last.map(RuntimeValue.string) ?? .null,
                "entry": .string(fromHome ? "favorites" : "song"),
                "continuation": context.map { .object(["playlistId": .string($0.playlistID),
                    "startMusicId": .string($0.seedID), "count": .number(Double($0.count))]) } ?? .null]
        case .songRoaming(let ids), .artistRoaming(let ids):
            operation = "similar"; var source = "song"
            if case .artistRoaming = request { source = "artist" }
            params = ["sourceIds": .array(ids.map(RuntimeValue.string)), "sourceType": .string(source)]
        case .songRecommendations(let seed, let offset):
            operation = "queueRecommendations"; params = ["seedSongId": .string(seed), "offset": .number(Double(offset))]
        }
        let value = try await call(operation, params); try await authorize()
        let context = value[music: "continuation"], heartbeat: MusicSimilarHeartbeatContext?
        if case .heartbeat = request, context != .null {
            heartbeat = .init(playlistID: context[music: "playlistId"].musicText,
                seedID: context[music: "startMusicId"].musicText,
                count: MusicPlatformDTO.optionalCount(context[music: "count"]) ?? 0)
        } else { heartbeat = nil }
        return .init(items: try MusicPlatformDTO.tracks(value), heartbeatContext: heartbeat,
            hasMore: { if case .bool(let result) = value[music: "hasMore"] { return result }; return nil }(),
            offset: MusicPlatformDTO.optionalCount(value[music: "offset"]))
    }
    func dailyStyleCategories(authorize: @escaping @Sendable () async throws -> Void) async throws -> [MusicDailyStyleCategory] {
        try await authorize(); let value = try await call("dailyStyle.get"); try await authorize()
        return value[music: "categories"].musicArray.map { row in
            .init(id: row[music: "id"].musicText, name: row[music: "name"].musicText,
                  tags: row[music: "tags"].musicArray.map {
                      .init(id: $0[music: "id"].musicText, name: $0[music: "name"].musicText)
                  })
        }
    }
    func saveDailyStyle(_ selection: MusicDailyStyleSelection,
        authorization: MusicQueueAuthorization) async throws -> MusicAccountDailyStyleSave {
        try authorization.check()
        let value = try await call("dailyStyle.save", ["categoryId": .string(selection.category),
            "tagIds": .array(selection.tags.map { .string($0.id) })], authorize: { try authorization.check() })
        if value[music: "status"] == .string("notSent") || value[music: "status"] == .string("rejected") {
            throw MusicPlatformClient.error(RuntimeHTTPError.remote(value[music: "errorCode"].musicText))
        }
        let confirmed = value[music: "status"] == .string("confirmed"), readback = value[music: "readback"]
        // Readback is optional and cannot retract the actual write receipt.
        let songs = (try? MusicPlatformDTO.rows(readback)) ?? []
        let returned = try? MusicDailyStyleSelection(category: readback[music: "category"].musicRequiredText(),
            tags: readback[music: "tags"].musicRequiredArray().map {
                .init(id: try $0[music: "id"].musicRequiredText(), name: $0[music: "name"].musicText)
            })
        return .init(songs: .init(rows: songs, nextPage: nil, expiresAt: MusicPlatformDTO.date(readback[music: "expiresAt"]),
                                  dailyStyle: returned), saveConfirmed: confirmed,
                     selectionChanged: value[music: "selectionMatches"] == .bool(false))
    }
}

enum MusicLoginQRCode {
    private static let context = CIContext(options: [.cacheIntermediates: false])

    static func encode(_ address: String) throws -> Data {
        guard let filter = CIFilter(name: "CIQRCodeGenerator") else { throw MusicServiceError.unavailable }
        filter.setValue(Data(address.utf8), forKey: "inputMessage"); filter.setValue("M", forKey: "inputCorrectionLevel")
        guard let image = filter.outputImage?.transformed(by: .init(scaleX: 8, y: 8)),
              let bytes = context.pngRepresentation(of: image, format: .RGBA8,
                  colorSpace: CGColorSpaceCreateDeviceRGB()) else { throw MusicServiceError.unavailable }
        return bytes
    }
}
