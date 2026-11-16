import Foundation
import CryptoKit
import XCTest
import MeowRuntime
@testable import MeowFilmFeature
@testable import Meowfilm

@MainActor
final class MeowfilmNativeSettingsHostTests: XCTestCase {
    func testLiveNativeSettingsApplyOverThePrivateChannel() async throws {
        try NativeSettingsHost.requireSimulator()
        let resources = try await hostResources()
        let client = try await resources.scriptSession.connection()
        try await client.validateAppModules()
        let original = try NativeSettingsHost.originals(resources)
        try NativeSettingsHost.requireEmptyCredentials(original)
        let run = UUID().uuidString.lowercased()
        addTeardownBlock { @MainActor in
            try await self.restoreNativeSettings(original, resources: resources, client: client)
        }

        try await NativeSettingsHost.save(resources, run: run)
        try NativeSettingsHost.requireSaved(resources, original: original, run: run, panWrites: 1)
        for block in NativeSettingsHost.appliedBlocks {
            try await waitForNativeRuntimeSettings(block, resources: resources)
        }
        try await waitForNativePan(resources, client: client)
        try NativeSettingsHost.requireProtectedFiles(run: run)

        let writes: [(FilmSettingsBlock, RuntimeModule, String, RuntimeValue)] = [
            (.tmdb, .tmdb, "settings.update", .object(["language": .string("must-not-save")])),
            (.douban, .douban, "settings.update", .object(["cookie": .string("synthetic-denied-cookie")])),
            (.rules, .film, "rules.set", .object(["rules": try NativeSettingsHost.json(FilmRuleSet())])),
            (.preferences, .film, "settings.set", NativeSettingsHost.preferences(run).value),
        ]
        for (block, module, operation, input) in writes {
            let before = try resources.filmSettings[block].snapshot().fingerprint
            do {
                _ = try await client.call(module: module, operation: operation, parameters: input)
                XCTFail("The legacy configuration writer must be denied: \(block.rawValue)")
                throw NativeSettingsHostFailure.legacyWriteAllowed
            } catch RuntimeHTTPError.remote(let code) {
                guard code == "PERMISSION_DENIED" else { throw NativeSettingsHostFailure.legacyWriteAllowed }
            }
            XCTAssertTrue(try resources.filmSettings[block].snapshot().fingerprint == before,
                          "A denied runtime write changed the native document: \(block.rawValue)")
            try await waitForNativeRuntimeSettings(block, resources: resources)
        }
        try NativeSettingsHost.requireSaved(resources, original: original, run: run, panWrites: 1)
        XCTAssertFalse(client.isClosed)
    }

    func testNativeSettingsPreserveExactJSONThroughNodeMobile() async throws {
        try NativeSettingsHost.requireSimulator()
        let resources = try await hostResources()
        let document = resources.filmSettings[.tmdb]
        let original = try document.read() ?? .object([
            "version": .number(1), "settings": FilmTMDBSettings.defaultValue,
        ])
        let originalSettings = try FilmTMDBSettings(XCTUnwrap(original.catField("settings")))
        guard originalSettings.apiKey.isEmpty, originalSettings.accessToken.isEmpty else {
            throw NativeSettingsHostFailure.unsafeBaseline
        }
        let client = try await resources.scriptSession.connection()
        try await client.validateAppModules()
        addTeardownBlock { @MainActor in
            _ = try document.update { _ in original }
            try await self.waitForNativeRuntimeSettings(.tmdb, resources: resources)
            XCTAssertEqual(try document.read(), original)
        }

        var extensionFields = try RuntimeJSON.decode(Data(
            #"{"decimal":1.234567890123456789,"largeInteger":9007199254740993,"exponent":1e400,"é":"composed","e\u0301":"decomposed"}"#.utf8
        )).filmRequiredObject()
        extensionFields["run"] = .string(UUID().uuidString.lowercased())
        let exact = RuntimeValue.object(extensionFields)
        // Prime the actual Node snapshot before the write: startup loading is
        // not evidence that a subsequent native-change notification was applied.
        let before = try await client.call(module: .tmdb, operation: "settings.get")
        XCTAssertNotEqual(before.catField("syntheticExtension"), exact)
        var settings = FilmTMDBSettings.defaultValue.filmObject!
        settings["apiBase"] = .string("http://127.0.0.1:9/synthetic-exact-json/api")
        settings["imageBase"] = .string("http://127.0.0.1:9/synthetic-exact-json/images")
        settings["language"] = .string("en-US"); settings["region"] = .string("US")
        settings["includeAdult"] = .bool(true); settings["syntheticExtension"] = exact
        let saved = try document.update { current in
            var root = current?.filmObject ?? ["version": .number(1)]
            root["settings"] = .object(settings)
            return .object(root)
        }
        try await waitForNativeRuntimeSettings(.tmdb, resources: resources)
        let response = try await client.call(module: .tmdb, operation: "settings.get")
        let returned = try XCTUnwrap(response.catField("syntheticExtension"))
        XCTAssertEqual(returned, exact)
        XCTAssertEqual(try RuntimeJSON.encode(returned), try RuntimeJSON.encode(exact))
        XCTAssertEqual(response.catField("language"), .string("en-US"))
        XCTAssertEqual(response.catField("region"), .string("US"))
        XCTAssertEqual(response.catField("includeAdult"), .bool(true))
        XCTAssertEqual(try document.read(), saved, "Runtime reads must not rewrite native settings")
        XCTAssertFalse(client.isClosed)
    }

    func testNativeSettingsChannelDoesNotRetainItsRuntimeSession() throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: directory) }
        let settings = try MeowfilmNativeSettings(documentsRoot: directory)
        var session: RuntimeSession? = RuntimeSession(launcher: NativeSettingsLifetimeLauncher(settings: settings.access))
        settings.attach(try XCTUnwrap(session))
        weak var released = session
        session = nil
        XCTAssertNil(released)
        let preferences: RuntimeValue = .object([
            "version": .number(1), "preferences": FilmPlaybackPreferences.defaults.value,
        ])
        _ = try settings[.preferences].update { _ in preferences }
        XCTAssertEqual(try settings[.preferences].read(), preferences)
        XCTAssertNil(released, "An offline document save must not recreate a released runtime session")
    }
}

private struct NativeSettingsLifetimeLauncher: RuntimeLaunching {
    let settings: RuntimeSettingsAccess
    func connect() async throws -> RuntimeHTTPClient {
        XCTFail("Saving native settings must not launch a runtime")
        throw RuntimeHTTPError.closed
    }
    func close() async {}
    func waitForExit() async {}
}

@MainActor
final class MeowfilmNativeSettingsColdStartTests: XCTestCase {
    func testRepeatedAppProcessesRestoreNativeSettingsWithoutReinstalling() async throws {
        try NativeSettingsHost.requireSimulator()
        guard let value = ProcessInfo.processInfo.environment["MEOWFILM_NATIVE_SETTINGS_COLD_RUN_ID"] else {
            throw XCTSkip("Requires two repetitions in distinct App processes")
        }
        guard let id = UUID(uuidString: value) else { throw NativeSettingsHostFailure.phaseMismatch }
        let run = id.uuidString.lowercased()
        let restoring = FileManager.default.fileExists(atPath: NativeSettingsHost.markerURL.path)
        let phase = restoring ? "restore" : "prepare"
        addTeardownBlock { @MainActor [self] in
            let attachment = XCTAttachment(data: try NativeSettingsHost.evidence(
                run: run, phase: phase, succeeded: testRun?.totalFailureCount == 0),
                uniformTypeIdentifier: "public.json")
            attachment.name = "native-settings-cold-\(run)-\(phase).json"
            attachment.lifetime = .keepAlways
            add(attachment)
        }
        if restoring { try await restoreNativeSettingsFromPreviousProcess(run: run) }
        else { try await prepareNativeSettingsForNextProcess(run: run) }
    }
}

@MainActor
private extension XCTestCase {
    func prepareNativeSettingsForNextProcess(run: String) async throws {
        guard !FileManager.default.fileExists(atPath: NativeSettingsHost.markerURL.path) else {
            throw NativeSettingsHostFailure.checkpointAlreadyExists
        }
        let resources = try await hostResources()
        let original = try NativeSettingsHost.originals(resources)
        try NativeSettingsHost.requireColdBaseline(original)
        let client = try await resources.scriptSession.connection()
        var prepared = false
        addTeardownBlock { @MainActor in
            if !prepared {
                try await self.restoreNativeSettings(original, resources: resources, client: nil)
                try NativeSettingsHost.removeMarker(ifOwnedBy: run)
            }
        }

        // close() includes launcher.waitForExit() and all admitted notifications.
        // Do not construct another launcher: NodeMobile is single-start per App.
        await resources.scriptSession.close()
        try await NativeSettingsHost.requireClosed(resources, client: client)
        try await NativeSettingsHost.save(resources, run: run, suffix: "-superseded")
        try await NativeSettingsHost.requireClosed(resources, client: client)
        try await NativeSettingsHost.save(resources, run: run)
        let id = NativeSettingsHost.catID(run)
        _ = try await resources.filmSourceSettings.importLocal(name: id,
            source: NativeSettingsHost.catScript(run), installationId: id)
        _ = try await resources.filmSourceSettings.enableCat(id, enabled: false)
        let cat = try await resources.filmSourceSettings.enableCat(id, enabled: true)
        guard cat.state == "stopped", cat.generation == 0, cat.sites.isEmpty, !cat.canDetect else {
            throw NativeSettingsHostFailure.unexpectedNativeSettings
        }
        try NativeSettingsHost.requireSaved(resources, original: original, run: run, panWrites: 2, cat: true)
        try await resources.filmRules.initialize()
        let rules = try await resources.filmRules.savedSnapshot()
        XCTAssertEqual(rules, NativeSettingsHost.rules(run))
        let preferences = NativeSettingsHost.preferenceModel(resources)
        try await preferences.loadPreferences()
        XCTAssertTrue(preferences.preferences == NativeSettingsHost.preferences(run))
        let nativePan = try NativeSettingsHost.pan(resources).accounts()
        guard nativePan.count == 7, nativePan[0].configured == true, nativePan[0].status == .unverified else {
            throw NativeSettingsHostFailure.unexpectedNativeSettings
        }
        try NativeSettingsHost.requireProtectedFiles(run: run)
        try await NativeSettingsHost.requireClosed(resources, client: client)

        // Only a successful prepare may authorize the next process to restore.
        guard testRun?.totalFailureCount == 0 else { throw NativeSettingsHostFailure.unexpectedNativeSettings }
        try NativeSettingsHost.writeMarker(run: run, original: original)
        prepared = true
        print("EVIDENCE native-settings phase=prepare pid=\(ProcessInfo.processInfo.processIdentifier) blocks=6 executorExited=true")
    }

    func restoreNativeSettingsFromPreviousProcess(run: String) async throws {
        let original = try NativeSettingsHost.readMarker(run: run)
        let resources = try await hostResources()
        // Establish ownership before registering any restoration or deletion.
        try NativeSettingsHost.requireSaved(resources, original: original, run: run, panWrites: 2, cat: true)
        var connection: RuntimeHTTPClient?
        addTeardownBlock { @MainActor in
            try await self.restoreNativeSettings(original, resources: resources, client: connection,
                                                 removedCat: NativeSettingsHost.catID(run))
            try NativeSettingsHost.removeMarker(ifOwnedBy: run)
        }
        let client = try await resources.scriptSession.connection()
        connection = client
        let before = try NativeSettingsHost.fingerprints(resources)
        for block in NativeSettingsHost.appliedBlocks {
            try await waitForNativeRuntimeSettings(block, resources: resources)
        }
        try await waitForNativePan(resources, client: client)
        let accounts = try NativeSettingsHost.pan(resources).accounts()
        guard accounts.count == 7, accounts[0].configured == true, accounts[0].status == .unverified else {
            throw NativeSettingsHostFailure.unexpectedNativeSettings
        }
        let cat = try await waitForNativeCat(NativeSettingsHost.catID(run), service: resources.filmSourceSettings)
        guard cat.canDetect, cat.inputRevision == 1, cat.sourceType == "local",
              cat.sourceHash == NativeSettingsHost.digest(NativeSettingsHost.catScript(run)),
              cat.sites.count == 1, cat.sites[0].key == "native-settings",
              cat.sites[0].name == NativeSettingsHost.catID(run) + " starts=1" else {
            throw NativeSettingsHostFailure.unexpectedNativeSettings
        }
        let snapshot = await resources.filmSourceSettings.snapshot
        XCTAssertEqual(snapshot.cats.map(\.installationId), [NativeSettingsHost.catID(run)])
        XCTAssertEqual(snapshot.videos.map(\.key), [.init(installationId: cat.installationId, siteKey: "native-settings")])
        XCTAssertTrue(try NativeSettingsHost.fingerprints(resources) == before,
                      "Cold reads must not replay offline changes or rewrite native settings")
        try NativeSettingsHost.requireProtectedFiles(run: run)
        print("EVIDENCE native-settings phase=restore pid=\(ProcessInfo.processInfo.processIdentifier) blocks=6 sites=1")
    }
}

private enum NativeSettingsHostFailure: Error {
    case simulatorRequired, phaseMismatch, unsafeBaseline, checkpointAlreadyExists
    case invalidCheckpoint, distinctProcessRequired, changedAppIdentity, unexpectedNativeSettings
    case runtimeNotClosed, plaintextCredentials, legacyWriteAllowed, panNotApplied, catNotRemoved
}

@MainActor
private enum NativeSettingsHost {
    static let appliedBlocks: [FilmSettingsBlock] = [.tmdb, .douban, .rules, .preferences]
    static var markerURL: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("MeowfilmNativeSettingsHostTests/cold.json")
    }
    static let emptyCats: RuntimeValue = .object([
        "version": .number(1), "port": .number(0), "providers": .object([:]),
        "mappings": .array([]), "scripts": .array([]),
    ])

    static func requireSimulator() throws {
        #if !targetEnvironment(simulator)
        throw NativeSettingsHostFailure.simulatorRequired
        #endif
    }
    static func json<Value: Encodable>(_ value: Value) throws -> RuntimeValue {
        try RuntimeJSON.decode(JSONEncoder().encode(value))
    }
    static func appIdentity() throws -> RuntimeObject {
        guard let identifier = Bundle.main.bundleIdentifier else { throw NativeSettingsHostFailure.changedAppIdentity }
        return ["dataContainerPath": .string(URL(fileURLWithPath: NSHomeDirectory()).resolvingSymlinksInPath().path),
                "appBundlePath": .string(Bundle.main.bundleURL.resolvingSymlinksInPath().path),
                "appBundleIdentifier": .string(identifier)]
    }
    static func evidence(run: String, phase: String, succeeded: Bool) throws -> Data {
        var fields = try appIdentity()
        fields["run"] = .string(run); fields["phase"] = .string(phase)
        fields["pid"] = .number(Double(ProcessInfo.processInfo.processIdentifier))
        fields["succeeded"] = .bool(succeeded)
        return try RuntimeJSON.encode(.object(fields))
    }
    static func originals(_ resources: MeowfilmResources) throws -> [FilmSettingsBlock: RuntimeValue] {
        let defaults: [FilmSettingsBlock: RuntimeValue] = [
            .tmdb: .object(["version": .number(1), "settings": FilmTMDBSettings.defaultValue]),
            .douban: .object(["version": .number(1), "settings": FilmDoubanSettings.defaultValue]),
            .rules: .object(["version": .number(1), "rules": try json(FilmRuleSettings.initial)]),
            .preferences: .object(["version": .number(1), "preferences": FilmPlaybackPreferences.defaults.value]),
            .panAccounts: PanSettingsStore.empty, .catSources: emptyCats,
        ]
        return try Dictionary(uniqueKeysWithValues: FilmSettingsBlock.allCases.map {
            ($0, try resources.filmSettings[$0].read() ?? defaults[$0]!)
        })
    }
    static func requireEmptyCredentials(_ original: [FilmSettingsBlock: RuntimeValue]) throws {
        guard original[.tmdb]?.catField("settings")?.catField("apiKey") == .string(""),
              original[.tmdb]?.catField("settings")?.catField("accessToken") == .string(""),
              original[.douban]?.catField("settings")?.catField("cookie") == .string(""),
              let pan = original[.panAccounts] else { throw NativeSettingsHostFailure.unsafeBaseline }
        try PanAccountService.validateSettingsDocument(pan)
        guard let records = pan.catField("accounts")?.filmObject,
              records.values.allSatisfy({ record in
                  record.catField("credentials")?.filmObject?.values.allSatisfy {
                      $0 == .string("") || $0 == .number(0)
                  } == true
              }) else { throw NativeSettingsHostFailure.unsafeBaseline }
    }
    static func requireColdBaseline(_ original: [FilmSettingsBlock: RuntimeValue]) throws {
        try requireSimulator()
        try requireEmptyCredentials(original)
        guard Set(original.keys) == Set(FilmSettingsBlock.allCases), original[.catSources] == emptyCats else {
            throw NativeSettingsHostFailure.unsafeBaseline
        }
        // The checkpoint may contain only known settings fields, never opaque
        // extension branches or credentials hidden in URLs.
        for (block, defaults) in [(FilmSettingsBlock.tmdb, FilmTMDBSettings.defaultValue),
                                  (.douban, FilmDoubanSettings.defaultValue)] {
            guard let root = original[block]?.filmObject, Set(root.keys) == ["version", "settings"],
                  root["version"] == .number(1), let settings = root["settings"]?.filmObject,
                  Set(settings.keys) == Set(defaults.filmObject!.keys) else {
                throw NativeSettingsHostFailure.unsafeBaseline
            }
            for (key, value) in settings {
                if key.hasSuffix("Base") || key.hasSuffix("Proxy") {
                    guard let text = value.filmText,
                          text.isEmpty && key.hasSuffix("Proxy") || safeEndpoint(text) else {
                        throw NativeSettingsHostFailure.unsafeBaseline
                    }
                } else if key == "includeAdult" {
                    guard case .bool = value else { throw NativeSettingsHostFailure.unsafeBaseline }
                } else if value.filmText == nil { throw NativeSettingsHostFailure.unsafeBaseline }
            }
        }
        guard let root = original[.rules]?.filmObject, Set(root.keys) == ["version", "rules"],
              root["version"] == .number(1), let rules = root["rules"],
              Set(rules.filmObject?.keys ?? []) ==
                ["episodeCleanRegexRules", "episodeRules", "movieRules", "aggregateRegexRules"],
              try json(JSONDecoder().decode(FilmRuleSet.self, from: RuntimeJSON.encode(rules))) == rules,
              let preferencesRoot = original[.preferences]?.filmObject,
              Set(preferencesRoot.keys) == ["version", "preferences"],
              preferencesRoot["version"] == .number(1),
              let preferences = preferencesRoot["preferences"]?.filmObject,
              Set(preferences.keys) == ["ruleOrder", "panOrder", "keywords", "siteCleanKeywords", "iosAutoPrepare"],
              case .bool? = preferences["iosAutoPrepare"] else { throw NativeSettingsHostFailure.unsafeBaseline }
        for key in ["ruleOrder", "panOrder", "keywords", "siteCleanKeywords"] {
            guard let rows = preferences[key]?.filmArray, rows.allSatisfy({ $0.filmText != nil }) else {
                throw NativeSettingsHostFailure.unsafeBaseline
            }
        }
    }
    private static func safeEndpoint(_ text: String) -> Bool {
        guard let url = URLComponents(string: text), ["http", "https"].contains(url.scheme ?? ""),
              url.host?.isEmpty == false, url.user == nil, url.password == nil, url.fragment == nil else { return false }
        return url.queryItems?.allSatisfy { ["{url}", "{encodedUrl}"].contains($0.value ?? "") } ?? true
    }
    static func pan(_ resources: MeowfilmResources) -> PanAccountService {
        PanAccountService(document: resources.filmSettings[.panAccounts],
                          connection: { try await resources.scriptSession.connection() })
    }
    static func preferenceModel(_ resources: MeowfilmResources) -> FilmPlaybackSettingsModel {
        FilmPlaybackSettingsModel(transport: FilmPlaybackTransport(connectionProvider: {
            try await resources.scriptSession.connection()
        }), settingsDocument: resources.filmSettings[.preferences])
    }
    static func secrets(_ run: String, suffix: String = "") -> [String] {
        ["key", "token", "douban-cookie", "pan-cookie"].map { "synthetic-native-settings-\($0)-\(run)\(suffix)" }
    }
    static func metadata(_ block: FilmSettingsBlock, run: String, suffix: String = "") -> RuntimeObject {
        var values = (block == .tmdb ? FilmTMDBSettings.defaultValue : FilmDoubanSettings.defaultValue).filmObject!
        let base = "http://127.0.0.1:9/native-settings/\(run)\(suffix)"
        values["apiBase"] = .string(base + "/api"); values["imageBase"] = .string(base + "/images")
        if block == .tmdb {
            values["apiKey"] = .string(secrets(run, suffix: suffix)[0])
            values["accessToken"] = .string(secrets(run, suffix: suffix)[1])
            values["language"] = .string("en-US"); values["region"] = .string("US")
        } else {
            values["searchBase"] = .string(base + "/search")
            values["cookie"] = .string(secrets(run, suffix: suffix)[2])
        }
        return values
    }
    static func rules(_ tag: String) -> FilmRuleSet {
        .init(episodeCleanRegexRules: ["native-clean-" + tag],
              episodeRules: [.init(pattern: "^native-" + tag, replace: "E1")],
              movieRules: [.init(pattern: "^native-movie-" + tag, flags: "i")],
              aggregateRegexRules: ["native-search-" + tag])
    }
    static func preferences(_ tag: String) -> FilmPlaybackPreferences {
        .init(ruleOrder: [.keyword, .quality, .pan], panOrder: [.baidu, .uc, .quark, .tianyi, .yidong],
              keywords: ["native-keyword-" + tag], siteCleanKeywords: ["native-site-" + tag], iosAutoPrepare: false)
    }
    static func save(_ resources: MeowfilmResources, run: String, suffix: String = "") async throws {
        _ = try await resources.filmMetadata.updateSettings(metadata(.tmdb, run: run, suffix: suffix))
        _ = try await resources.filmDoubanSettings.updateSettings(metadata(.douban, run: run, suffix: suffix))
        _ = try await resources.filmRules.save(rules(run + suffix))
        let model = preferenceModel(resources)
        try await model.loadPreferences()
        model.edit { $0 = preferences(run + suffix) }
        try await model.savePreferences()
        let account = try pan(resources).replace(.baidu, credentials: .object(["cookie": .string(secrets(run, suffix: suffix)[3])]))
        guard account.configured == true, account.status == .unverified else {
            throw NativeSettingsHostFailure.unexpectedNativeSettings
        }
    }
    static func requireSaved(_ resources: MeowfilmResources, original: [FilmSettingsBlock: RuntimeValue],
                             run: String, panWrites: Int64, cat: Bool = false) throws {
        var expected = original
        for block in [FilmSettingsBlock.tmdb, .douban] {
            var fields = original[block]!.filmObject!
            fields["settings"] = .object(metadata(block, run: run))
            expected[block] = .object(fields)
        }
        for (block, key, value) in [(FilmSettingsBlock.rules, "rules", try json(rules(run))),
                                    (.preferences, "preferences", preferences(run).value)] {
            var fields = original[block]!.filmObject!, values = fields[key]!.filmObject!
            for (name, item) in value.filmObject! { values[name] = item }
            fields[key] = .object(values)
            expected[block] = .object(fields)
        }
        var accounts = original[.panAccounts]!.catField("accounts")!.filmObject!
        let revision = accounts["baidu"]?.catField("revision")?.panInteger ?? 0
        accounts["baidu"] = .object(["revision": .number(Double(revision + panWrites)),
                                     "credentials": .object(["cookie": .string(secrets(run)[3])])])
        expected[.panAccounts] = .object(["version": .number(1), "accounts": .object(accounts)])
        if cat {
            var fields = emptyCats.filmObject!
            fields["scripts"] = .array([.object([
                "installationId": .string(catID(run)), "name": .string(catID(run)), "sourceType": .string("local"),
                "sourceUrl": .null, "localPath": .null, "localHash": .string(digest(catScript(run))),
                "enabled": .bool(true), "inputRevision": .number(1),
            ])])
            expected[.catSources] = .object(fields)
        }
        for block in FilmSettingsBlock.allCases {
            let actual = try resources.filmSettings[block].read() ?? original[block]!
            guard actual == expected[block] else { throw NativeSettingsHostFailure.unexpectedNativeSettings }
        }
    }
    static func fingerprints(_ resources: MeowfilmResources) throws -> [FilmSettingsBlock: RuntimeValue] {
        try Dictionary(uniqueKeysWithValues: FilmSettingsBlock.allCases.map {
            ($0, try resources.filmSettings[$0].snapshot().fingerprint)
        })
    }
    static func requireClosed(_ resources: MeowfilmResources, client: RuntimeHTTPClient) async throws {
        guard client.isClosed, await resources.scriptSession.connectionIfStarted() == nil else {
            throw NativeSettingsHostFailure.runtimeNotClosed
        }
        do {
            _ = try await resources.scriptSession.connection()
            throw NativeSettingsHostFailure.runtimeNotClosed
        } catch RuntimeHTTPError.closed {}
    }
    static func digest(_ value: String) -> String {
        SHA256.hash(data: Data(value.utf8)).map { String(format: "%02x", $0) }.joined()
    }
    static func requireProtectedFiles(run: String) throws {
        let support = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        let documents = support.appendingPathComponent("meowfilm/documents")
        let needles = (secrets(run) + secrets(run, suffix: "-superseded")).map { Data($0.utf8) }
        for block in FilmSettingsBlock.allCases {
            let plain = documents.appendingPathComponent(block.namespace + "/settings.json")
            let url = block.isSensitive ? documents.appendingPathComponent(
                "protected-settings/n-\(digest(block.namespace))/d-\(digest(FilmSettingsDocument.key)).meowenc") : plain
            if block == .catSources, !FileManager.default.fileExists(atPath: url.path) { continue }
            let bytes = try Data(contentsOf: url)
            guard needles.allSatisfy({ bytes.range(of: $0) == nil }),
                  !block.isSensitive || bytes.starts(with: Data("MEOWEDOC".utf8)) &&
                    !FileManager.default.fileExists(atPath: plain.path) else {
                throw NativeSettingsHostFailure.plaintextCredentials
            }
        }
        for path in ["tmdb/settings.json", "douban/settings.json", "pan/accounts.json"] {
            guard !FileManager.default.fileExists(atPath: MeowfilmScriptRuntime.dataDirectory.appendingPathComponent(path).path) else {
                throw NativeSettingsHostFailure.plaintextCredentials
            }
        }
    }
    static func writeMarker(run: String, original: [FilmSettingsBlock: RuntimeValue]) throws {
        try requireColdBaseline(original)
        var backup = RuntimeObject(uniqueKeysWithValues: original.map { ($0.key.rawValue, $0.value) })
        // Persist no credential fields, even empty ones. Pan needs only its
        // revisions and empty-field names to reconstruct the prior empty state.
        for (block, names) in [(FilmSettingsBlock.tmdb, ["apiKey", "accessToken"]), (.douban, ["cookie"])] {
            var fields = original[block]!.filmObject!, settings = fields["settings"]!.filmObject!
            for name in names { settings[name] = nil }
            fields["settings"] = .object(settings); backup[block.rawValue] = .object(fields)
        }
        var accounts = RuntimeObject()
        for (id, record) in original[.panAccounts]!.catField("accounts")!.filmObject! {
            accounts[id] = .object(["revision": record.catField("revision")!,
                "emptyFields": .array(record.catField("credentials")!.filmObject!.keys.sorted().map(RuntimeValue.string))])
        }
        backup[FilmSettingsBlock.panAccounts.rawValue] = .object(["version": .number(1), "accounts": .object(accounts)])
        var fields = try appIdentity()
        fields["version"] = .number(1); fields["run"] = .string(run)
        fields["pid"] = .number(Double(ProcessInfo.processInfo.processIdentifier))
        fields["executorExited"] = .bool(true); fields["original"] = .object(backup)
        let bytes = try RuntimeJSON.encode(.object(fields))
        guard secrets(run).allSatisfy({ bytes.range(of: Data($0.utf8)) == nil }) else {
            throw NativeSettingsHostFailure.plaintextCredentials
        }
        try FileManager.default.createDirectory(at: markerURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        try bytes.write(to: markerURL, options: [.atomic, .completeFileProtectionUntilFirstUserAuthentication])
    }
    static func readMarker(run expectedRun: String) throws -> [FilmSettingsBlock: RuntimeValue] {
        let fields = try RuntimeJSON.decode(Data(contentsOf: markerURL)).filmRequiredObject()
        guard Set(fields.keys) == ["version", "run", "pid", "executorExited", "original",
                                  "dataContainerPath", "appBundlePath", "appBundleIdentifier"],
              fields["version"] == .number(1), fields["executorExited"] == .bool(true),
              fields["run"] == .string(expectedRun),
              let pid = fields["pid"]?.panInteger, pid > 0,
              let values = fields["original"]?.filmObject,
              Set(values.keys) == Set(FilmSettingsBlock.allCases.map(\.rawValue)) else {
            throw NativeSettingsHostFailure.invalidCheckpoint
        }
        guard pid != Int64(ProcessInfo.processInfo.processIdentifier) else {
            throw NativeSettingsHostFailure.distinctProcessRequired
        }
        guard try appIdentity().allSatisfy({ fields[$0.key] == $0.value }) else {
            throw NativeSettingsHostFailure.changedAppIdentity
        }
        var original = Dictionary(uniqueKeysWithValues: values.map { (FilmSettingsBlock(rawValue: $0.key)!, $0.value) })
        for (block, names) in [(FilmSettingsBlock.tmdb, ["apiKey", "accessToken"]), (.douban, ["cookie"])] {
            guard var root = original[block]?.filmObject, var settings = root["settings"]?.filmObject,
                  names.allSatisfy({ settings[$0] == nil }) else { throw NativeSettingsHostFailure.invalidCheckpoint }
            for name in names { settings[name] = .string("") }
            root["settings"] = .object(settings); original[block] = .object(root)
        }
        guard let pan = original[.panAccounts]?.filmObject, Set(pan.keys) == ["version", "accounts"],
              pan["version"] == .number(1), let records = pan["accounts"]?.filmObject else {
            throw NativeSettingsHostFailure.invalidCheckpoint
        }
        var accounts = RuntimeObject()
        for (id, record) in records {
            guard let provider = PanProvider.all.first(where: { $0.id.rawValue == id }),
                  let fields = record.filmObject, Set(fields.keys) == ["revision", "emptyFields"],
                  let names = fields["emptyFields"]?.filmArray else { throw NativeSettingsHostFailure.invalidCheckpoint }
            var credentials = RuntimeObject()
            for name in names {
                guard let text = name.filmText, credentials[text] == nil,
                      let field = provider.credentialFields.first(where: { $0.rawValue == text }) else {
                    throw NativeSettingsHostFailure.invalidCheckpoint
                }
                credentials[text] = field.isNumber ? .number(0) : .string("")
            }
            accounts[id] = .object(["revision": try fields.filmValue("revision"), "credentials": .object(credentials)])
        }
        original[.panAccounts] = .object(["version": .number(1), "accounts": .object(accounts)])
        try requireColdBaseline(original)
        return original
    }
    static func removeMarker(ifOwnedBy run: String) throws {
        guard FileManager.default.fileExists(atPath: markerURL.path) else { return }
        let value = try RuntimeJSON.decode(Data(contentsOf: markerURL))
        guard value.catField("run") == .string(run) else { throw NativeSettingsHostFailure.invalidCheckpoint }
        try FileManager.default.removeItem(at: markerURL)
    }
    static func catID(_ run: String) -> String { "ios-native-settings-cold-" + run }
    static func catScript(_ run: String) -> String {
        """
        const http = require('http'), fs = require('fs');
        const starts = (fs.existsSync('/starts.txt') ? Number(fs.readFileSync('/starts.txt', 'utf8')) : 0) + 1;
        fs.writeFileSync('/starts.txt', String(starts));
        let server;
        module.exports.start = () => new Promise(resolve => {
          server = http.createServer((request, response) => {
            response.setHeader('content-type', 'application/json');
            if (request.url === '/config' || request.url === '/full-config') {
              response.end(JSON.stringify({ video: { sites: [{ key: 'native-settings',
                name: '\(catID(run)) starts=' + starts, api: '/spider/native-settings/3' }] } }));
            } else { response.statusCode = 404; response.end('{}'); }
          });
          server.listen(Number(process.env.PORT), '127.0.0.1', resolve);
        });
        module.exports.stop = () => { server?.closeAllConnections(); server?.close(); };
        """
    }
}

@MainActor
private extension XCTestCase {
    func waitForNativePan(_ resources: MeowfilmResources, client: RuntimeHTTPClient) async throws {
        let expected = try NativeSettingsHost.pan(resources).accounts()
        let deadline = Date().addingTimeInterval(10)
        repeat {
            let response = try await client.call(module: .pan, operation: "settings.get")
            guard let rows = response.catField("accounts")?.filmArray else { throw PanAccountError.invalidResponse }
            let actual = try rows.map(PanAccount.init)
            // Runtime status is not proof of upstream validation. Compare only
            // the native configuration projection; never call refresh here.
            if actual.count == 7, expected.count == 7, zip(actual, expected).allSatisfy({ pair in
                let (actual, expected) = pair
                return actual.provider == expected.provider && actual.revision == expected.revision &&
                    actual.errorCode == expected.errorCode && actual.configured == expected.configured &&
                    actual.fields.map({ Set($0) }) == expected.fields.map({ Set($0) })
            }) { return }
            try await Task.sleep(nanoseconds: 20_000_000)
        } while Date() < deadline
        throw NativeSettingsHostFailure.panNotApplied
    }
    func restoreNativeSettings(_ original: [FilmSettingsBlock: RuntimeValue], resources: MeowfilmResources,
                               client: RuntimeHTTPClient?, removedCat: String? = nil) async throws {
        var failure: Error?
        for block in FilmSettingsBlock.allCases {
            do { _ = try resources.filmSettings[block].update { _ in original[block]! } }
            catch { failure = failure ?? error }
        }
        do { try await resources.filmRules.initialize() } catch { failure = failure ?? error }
        do { try await resources.filmSourceSettings.initialize() } catch { failure = failure ?? error }
        if let client {
            for block in NativeSettingsHost.appliedBlocks {
                do { try await waitForNativeRuntimeSettings(block, resources: resources) }
                catch { failure = failure ?? error }
            }
            do { try await waitForNativePan(resources, client: client) } catch { failure = failure ?? error }
            if let removedCat {
                do {
                    let deadline = Date().addingTimeInterval(15)
                    while true {
                        try await resources.filmSourceSettings.refresh()
                        let status = try await client.call(module: .cat, operation: "status")
                        guard let instances = status.catField("instances")?.filmArray else {
                            throw FilmSourceError(code: "INVALID_RESPONSE")
                        }
                        if !instances.contains(where: { $0.catField("installationId") == .string(removedCat) }) { break }
                        guard Date() < deadline else { throw NativeSettingsHostFailure.catNotRemoved }
                        try await Task.sleep(nanoseconds: 20_000_000)
                    }
                    let snapshot = await resources.filmSourceSettings.snapshot
                    XCTAssertFalse(snapshot.cats.contains { $0.installationId == removedCat })
                    XCTAssertFalse(snapshot.videos.contains { $0.key.installationId == removedCat })
                } catch { failure = failure ?? error }
            }
        }
        for block in FilmSettingsBlock.allCases {
            do {
                guard try resources.filmSettings[block].read() == original[block] else {
                    throw NativeSettingsHostFailure.unexpectedNativeSettings
                }
            } catch { failure = failure ?? error }
        }
        if let failure { throw failure }
    }
}
