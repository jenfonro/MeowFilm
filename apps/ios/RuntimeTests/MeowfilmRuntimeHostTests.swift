import XCTest
import UIKit
import Combine
import MeowContracts
@testable import MeowPlayback
@testable import MeowMusicFeature
import MeowMusicNativeService
import MeowFilmFeature
import MeowStorage
import MeowRuntime
import MeowUI
@testable import Meowfilm

/// Registered by the test bundle, independent of the selected test classes.
@objc(MeowfilmRuntimeTestBundle)
final class MeowfilmRuntimeTestBundle: NSObject {
    private static let observer = Observer()

    override init() {
        super.init()
        _ = Self.observer
    }

    private final class Observer: NSObject, XCTestObservation {
        private let lock = NSLock()
        private var finished = false

        override init() {
            super.init()
            XCTestObservationCenter.shared.addTestObserver(self)
        }

        func testBundleDidFinish(_ testBundle: Bundle) {
            guard testBundle == Bundle(for: MeowfilmRuntimeTestBundle.self) else { return }
            lock.lock()
            guard !finished else { lock.unlock(); return }
            finished = true
            lock.unlock()

            let done = DispatchSemaphore(value: 0)
            Task { @MainActor in
                let message: String
                if let app = UIApplication.shared.delegate as? MeowfilmAppDelegate, let assembly = app.assembly {
                    assembly.shutdown()
                    await assembly.waitForShutdown()
                    if let error = assembly.shutdownError {
                        message = "MEOWFILM_RUNTIME_BUNDLE_SHUTDOWN_FAILED \(error)"
                    } else {
                        message = "MEOWFILM_RUNTIME_BUNDLE_SHUTDOWN_COMPLETE"
                    }
                } else {
                    message = "MEOWFILM_RUNTIME_BUNDLE_SHUTDOWN_NO_ASSEMBLY"
                }
                FileHandle.standardError.write(Data((message + "\n").utf8))
                done.signal()
            }
            if Thread.isMainThread {
                // Async shutdown must keep its MainActor and URLSession work runnable.
                while done.wait(timeout: .now()) == .timedOut {
                    _ = RunLoop.current.run(mode: .default, before: Date(timeIntervalSinceNow: 0.01))
                }
            } else {
                done.wait()
            }
        }
    }
}

/// Runs inside the real Meowfilm App process (TEST_HOST): the App's embedded
/// Node Mobile framework, its signed runtime payload and the native script owner.
final class MeowfilmRuntimeHostTests: XCTestCase {
    private static let script = """
    /**
     * @name Runtime host fixture
     * @version 3
     */
    lx.on(lx.EVENT_NAMES.request, async ({ source, action, info }, context) => {
      const count = ((await context.storage.get('count')) || 0) + 1;
      await context.storage.set('count', count);
      if (action === 'lyric') return { lyric: '[00:00.00]' + source, tlyric: '', rlyric: '' };
      return 'http://media.invalid/' + source + '/' + info.type + '?n=' + count;
    });
    lx.send(lx.EVENT_NAMES.inited, { sources: {
      tx: { name: 'Fixture', type: 'music', actions: ['musicUrl', 'lyric'], qualitys: ['128k', '320k'] },
    } });
    """

    func testAppBundleEmbedsTheSignedRuntimeAssembly() throws {
        let root = try XCTUnwrap(Bundle.main.resourceURL).appendingPathComponent("Runtime", isDirectory: true)
        let payload = try RuntimePayload(root: root)
        XCTAssertEqual(payload.entry.lastPathComponent, "aggregate.mjs")
        let data = try Data(contentsOf: root.appendingPathComponent("runtime-manifest.json"))
        let manifest = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
        XCTAssertEqual(manifest["assemblyProfile"] as? String, "common-native")
        XCTAssertEqual(manifest["transport"] as? String, "runtime.http.v1")
        XCTAssertEqual(manifest["modules"] as? [String], ["music", "cat", "source", "pan", "tmdb", "douban", "film", "netease"])
        let files = try XCTUnwrap(manifest["files"] as? [[String: Any]])
        XCTAssertFalse(files.contains { ($0["path"] as? String)?.lowercased().hasSuffix(".wasm") == true })
        let frameworks = try XCTUnwrap(Bundle.main.privateFrameworksURL)
        XCTAssertTrue(FileManager.default.fileExists(
            atPath: frameworks.appendingPathComponent("NodeMobile.framework/NodeMobile").path))
        XCTAssertTrue(FileManager.default.fileExists(
            atPath: frameworks.appendingPathComponent("MeowScriptHost.framework/MeowScriptHost").path))
        XCTAssertNotNil(Bundle.main.url(forResource: "nodejs-mobile-LICENSE", withExtension: "txt"))
        let nativeLicense = try XCTUnwrap(Bundle.main.url(forResource: "quickjs-ng-LICENSE", withExtension: "txt"))
        XCTAssertTrue(try String(contentsOf: nativeLicense, encoding: .utf8).contains("Fabrice Bellard"))
        let unicodeLicense = try XCTUnwrap(Bundle.main.url(forResource: "Unicode-3.0", withExtension: "txt"))
        XCTAssertTrue(try String(contentsOf: unicodeLicense, encoding: .utf8).contains("1991-2026 Unicode, Inc."))
    }

    func testAppBundleAllowsArbitraryHTTPWithoutScopedATSOverrides() throws {
        let transport = try XCTUnwrap(
            Bundle.main.object(forInfoDictionaryKey: "NSAppTransportSecurity") as? [String: Bool])
        XCTAssertEqual(transport, ["NSAllowsArbitraryLoads": true])
    }

    @MainActor
    func testEmbeddedNodeServesAnLXScriptThroughTheNativeOwner() async throws {
        let resources = try await hostResources()
        let service = resources.musicScripts
        let connection = try await resources.scriptSession.connection()
        try await connection.validateAppModules()
        let description = try await connection.describe()
        guard case .object(let fields) = description, case .array(let modules)? = fields["modules"] else {
            return XCTFail("Missing runtime module description")
        }
        XCTAssertEqual(fields["protocol"], .string("runtime.http.v1"))
        XCTAssertEqual(modules.compactMap { value -> String? in
            guard case .object(let module) = value, case .string(let id)? = module["id"] else { return nil }
            return id
        }, ["music", "cat", "source", "pan", "tmdb", "douban", "film", "netease"])
        XCTAssertTrue(modules.contains {
            guard case .object(let module) = $0 else { return false }
            return module["id"] == .string("music") && module["version"] == .string("0.8.0")
        })

        let imported = try await service.importScriptSource(Data(Self.script.utf8), fileName: "fixture.js")
        addTeardownBlock {
            if try await service.scriptSources().contains(where: { $0.id == imported.id }) {
                try await service.removeScriptSource(imported.id)
            }
        }
        XCTAssertEqual(imported.name, "Runtime host fixture")
        XCTAssertEqual(imported.version, "3")
        XCTAssertEqual(imported.state, .disabled)
        let enabled = try await service.setScriptSourceEnabled(imported.id, enabled: true)
        XCTAssertEqual(enabled.state, .ready)
        XCTAssertEqual(enabled.platforms, [.qq])

        let sources = try await service.sources()
        let source = try XCTUnwrap(sources.first { $0.installationID == imported.id })
        XCTAssertEqual(source.installationID, imported.id)
        XCTAssertEqual(source.capabilities.map(\.siteID), ["tx"])
        let request = MusicScriptRequest(source: source, siteID: "tx", sourceKey: "fixture", quality: "320k",
            musicInfo: Data(#"{"source":"tx","songmid":"fixture"}"#.utf8))
        // Request-bound storage persists in the shared module between calls.
        let first = try await service.resolve(request: request)
        XCTAssertEqual(first.value.url.absoluteString, "https://media.invalid/tx/320k?n=1")
        XCTAssertEqual(first.source.identity, source.identity)
        XCTAssertEqual(first.source.generation, source.generation)
        let second = try await service.resolve(request: request)
        XCTAssertEqual(second.value.url.absoluteString, "https://media.invalid/tx/320k?n=2")
        XCTAssertEqual(second.source.generation, first.source.generation)
        let lyrics = try await service.lyrics(request: request)
        XCTAssertEqual(lyrics.value.text, "[00:00.00]tx")
        XCTAssertEqual(lyrics.source.generation, first.source.generation)
        let disabled = try await service.setScriptSourceEnabled(imported.id, enabled: false)
        XCTAssertEqual(disabled.state, .disabled)
        do {
            _ = try await service.resolve(request: request)
            XCTFail("A disabled script must not execute")
        } catch { XCTAssertEqual(error as? MusicServiceError, .sourceUnavailable) }
        try await service.removeScriptSource(imported.id)
        let remaining = try await service.scriptSources()
        XCTAssertFalse(remaining.contains { $0.id == imported.id })
        let shared = try await resources.scriptSession.connection()
        XCTAssertTrue(connection === shared)
        guard case .object(let status) = try await shared.call(module: .cat, operation: "status") else {
            return XCTFail("Missing Cat status")
        }
        guard case .array? = status["instances"] else { return XCTFail("Missing direct Cat status value") }
    }

    @MainActor
    func testNativeSourceAndNodeRulesUseIndependentRuntimesInTheSameApp() async throws {
        let resources = try await hostResources()
        let client = try await resources.scriptSession.connection()
        let sourceID = "ios-source-" + UUID().uuidString.lowercased()
        let source = #"""
        export default {
          async searchContent(keyword) {
            await Promise.resolve();
            return {
              list: [{ vod_id: keyword, vod_name: keyword }],
              digest: CryptoJS.MD5('abc').toString(),
              '\u00e9': 'composed', 'e\u0301': 'decomposed',
              unicode: { upper: 'ß中文'.toUpperCase(), lower: 'İK'.toLowerCase(),
                nfkc: 'Ａ①ﬃ'.normalize('NFKC') },
              token: 'business-token', blob: { value: 'business-value' }
            };
          }
        };
        """#
        _ = try await client.upload(module: .source, installationID: sourceID,
            sourceRevision: 1, source: Data(source.utf8), basename: "source.js")
        let loaded = try await client.call(module: .source, operation: "load", parameters: .object([
            "installationId": .string(sourceID), "sourceRevision": .number(1),
            "revision": .number(1), "expectedRevision": .number(0), "protocol": .string("cat")
        ]))
        guard case .object(let load) = loaded, case .object(let instance)? = load["instance"],
              case .number(let generation)? = instance["generation"] else {
            _ = try? await client.call(module: .source, operation: "unload",
                parameters: .object(["installationId": .string(sourceID), "revision": .number(1)]))
            return XCTFail("Missing native source identity")
        }
        addTeardownBlock {
            _ = try await client.call(module: .source, operation: "unload", parameters: .object([
                "installationId": .string(sourceID), "revision": .number(1), "generation": .number(generation)
            ]))
        }

        let rules = FilmRuleService(connection: { client })
        do {
            try await rules.initialize()
            // Source and film rules share the signed native carrier while
            // retaining independent QuickJS Runtime/Context lifetimes.
            async let rule = rules.process(.init(section: .movie, text: "电影"),
                draft: .init(movieRules: [.init(pattern: "电影", replace: "影片", flags: "u")]))
            async let script = client.call(module: .source, operation: "invoke", parameters: .object([
                "installationId": .string(sourceID), "revision": .number(1), "generation": .number(generation),
                "action": .string("search"), "payload": .object(["keyword": .string("书源中文")])
            ]))
            let (ruleResult, sourceResult) = try await (rule, script)
            XCTAssertEqual(ruleResult.outputText.displayString, "影片")
            guard case .object(let value) = sourceResult else {
                await rules.close()
                return XCTFail("Source result is not an object")
            }
            XCTAssertEqual(value["digest"], .string("900150983cd24fb0d6963f7d28e17f72"))
            XCTAssertEqual(value["\u{00E9}"], .string("composed"))
            XCTAssertEqual(value["e\u{0301}"], .string("decomposed"))
            XCTAssertEqual(value["token"], .string("business-token"))
            XCTAssertEqual(value["blob"], .object(["value": .string("business-value")]))
            XCTAssertEqual(value["unicode"], .object(["upper": .string("SS中文"),
                "lower": .string("i\u{0307}k"), "nfkc": .string("A1ffi")]))
            XCTAssertEqual(value["list"], .array([.object([
                "vod_id": .string("书源中文"), "vod_name": .string("书源中文")
            ])]))
            await rules.close()
        } catch {
            await rules.close()
            throw error
        }
    }

    @MainActor
    func testPortableCatPreprocessingFailurePreservesCurrentGeneration() async throws {
        let resources = try await hostResources()
        let client = try await resources.scriptSession.connection()
        let id = "ios-preprocess-cat-" + UUID().uuidString.lowercased()
        let source = """
        let count = 0;
        export default {
          searchContent(keyword) { return { source: 'original', count: ++count, keyword }; }
        };
        """
        addTeardownBlock {
            if let current = try await Self.portableSourceInstance(client, id: id) {
                _ = try await client.call(module: .source, operation: "unload", parameters: .object([
                    "installationId": .string(id),
                    "revision": try XCTUnwrap(current["revision"]),
                    "generation": try XCTUnwrap(current["generation"])
                ]))
            }
        }
        _ = try await client.upload(module: .source, installationID: id, sourceRevision: 1,
            source: Data(source.utf8), basename: "preprocess.js")
        let loaded = try await client.call(module: .source, operation: "load", parameters: .object([
            "installationId": .string(id), "sourceRevision": .number(1),
            "revision": .number(1), "expectedRevision": .number(0), "protocol": .string("cat")
        ]))
        guard case .object(let load) = loaded, case .object(let instance)? = load["instance"],
              case .number(let generation)? = instance["generation"] else {
            return XCTFail("Missing Cat preprocessing instance")
        }
        XCTAssertEqual(instance["state"], .string("ready"))
        let first = try await client.call(module: .source, operation: "invoke", parameters: .object([
            "installationId": .string(id), "revision": .number(1), "generation": .number(generation),
            "action": .string("search"), "payload": .object(["keyword": .string("before")])
        ]))
        XCTAssertEqual(first, .object([
            "source": .string("original"), "count": .number(1), "keyword": .string("before")
        ]))

        let exhausted = "export default " + String(repeating: "(", count: 4096) + "{}"
            + String(repeating: ")", count: 4096) + ";"
        // The source HTTP contract maps SCRIPT_PROTOCOL_ERROR to SOURCE_FAILURE
        // and RESOURCE_LIMIT to LIMIT; neither failure may publish the candidate.
        let candidates = [("export default {", "SOURCE_FAILURE"), (exhausted, "LIMIT")]
        for (index, candidate) in candidates.enumerated() {
            let revision = index + 2
            _ = try await client.upload(module: .source, installationID: id, sourceRevision: revision,
                source: Data(candidate.0.utf8), basename: "preprocess.js")
            do {
                _ = try await client.call(module: .source, operation: "load", parameters: .object([
                    "installationId": .string(id), "sourceRevision": .number(Double(revision)),
                    "revision": .number(Double(revision)), "expectedRevision": .number(1),
                    "protocol": .string("cat")
                ]))
                XCTFail("An invalid preprocessing candidate must not load")
            } catch {
                XCTAssertEqual(error as? RuntimeHTTPError, .remote(candidate.1))
            }
            let current = try await Self.portableSourceInstance(client, id: id)
            XCTAssertEqual(current, instance, "Failed preprocessing must preserve the exact published identity")
            let result = try await client.call(module: .source, operation: "invoke", parameters: .object([
                "installationId": .string(id), "revision": .number(1), "generation": .number(generation),
                "action": .string("search"), "payload": .object(["keyword": .string(candidate.1)])
            ]))
            XCTAssertEqual(result, .object([
                "source": .string("original"), "count": .number(Double(index + 2)),
                "keyword": .string(candidate.1)
            ]), "The original source and its state must survive the failed candidate")
        }
        let unloaded = try await client.call(module: .source, operation: "unload", parameters: .object([
            "installationId": .string(id), "revision": .number(1), "generation": .number(generation)
        ]))
        XCTAssertEqual(unloaded, .object(["unloaded": .bool(true), "cleanupComplete": .bool(true)]))
        let removed = try await Self.portableSourceInstance(client, id: id)
        XCTAssertNil(removed)
    }

    @MainActor
    func testPortableLegadoPreprocessingExhaustionRecoversSameGeneration() async throws {
        let resources = try await hostResources()
        let client = try await resources.scriptSession.connection()
        let exhausted = String(repeating: "(", count: 4096) + "1"
            + String(repeating: ")", count: 4096)
        for (script, code) in [("function (", "SOURCE_FAILURE"), (exhausted, "LIMIT")] {
            let id = "ios-preprocess-legado-" + UUID().uuidString.lowercased()
            let definition = RuntimeValue.object([
                "bookSourceName": .string("Preprocessing fixture"),
                "bookSourceUrl": .string("https://preprocess.invalid/"),
                "coverDecodeJs": .string(script),
                "exploreUrl": .string("""
                @js: function ready() { return "Ready::https://preprocess.invalid/ready"; } ready()
                """)
            ])
            addTeardownBlock {
                if let current = try await Self.portableSourceInstance(client, id: id) {
                    _ = try await client.call(module: .source, operation: "unload", parameters: .object([
                        "installationId": .string(id),
                        "revision": try XCTUnwrap(current["revision"]),
                        "generation": try XCTUnwrap(current["generation"])
                    ]))
                }
            }
            _ = try await client.upload(module: .source, installationID: id, sourceRevision: 1,
                source: RuntimeJSON.encode(definition), basename: "preprocess.json")
            let loaded = try await client.call(module: .source, operation: "load", parameters: .object([
                "installationId": .string(id), "sourceRevision": .number(1),
                "revision": .number(1), "expectedRevision": .number(0), "protocol": .string("legado")
            ]))
            guard case .object(let load) = loaded, case .object(let instance)? = load["instance"],
                  case .number(let generation)? = instance["generation"] else {
                return XCTFail("Missing Legado preprocessing instance")
            }
            XCTAssertEqual(instance["state"], .string("ready"))
            let expected = RuntimeValue.object(["kinds": .array([.object([
                "title": .string("Ready"), "url": .string("https://preprocess.invalid/ready"),
                "style": .object([:])
            ])])])
            let before = try await client.call(module: .source, operation: "invoke", parameters: .object([
                "installationId": .string(id), "revision": .number(1), "generation": .number(generation),
                "action": .string("exploreKinds"), "payload": .object([:])
            ]))
            XCTAssertEqual(before, expected)
            do {
                _ = try await client.call(module: .source, operation: "invoke", parameters: .object([
                    "installationId": .string(id), "revision": .number(1), "generation": .number(generation),
                    "action": .string("image"), "payload": .object([:])
                ]))
                XCTFail("Invalid coverDecodeJs must fail preprocessing")
            } catch {
                XCTAssertEqual(error as? RuntimeHTTPError, .remote(code))
            }
            let current = try await Self.portableSourceInstance(client, id: id)
            XCTAssertEqual(current, instance, "A preprocessing failure must not replace or retire the owner")
            let after = try await client.call(module: .source, operation: "invoke", parameters: .object([
                "installationId": .string(id), "revision": .number(1), "generation": .number(generation),
                "action": .string("exploreKinds"), "payload": .object([:])
            ]))
            XCTAssertEqual(after, expected)
            let unloaded = try await client.call(module: .source, operation: "unload", parameters: .object([
                "installationId": .string(id), "revision": .number(1), "generation": .number(generation)
            ]))
            XCTAssertEqual(unloaded, .object(["unloaded": .bool(true), "cleanupComplete": .bool(true)]))
            let removed = try await Self.portableSourceInstance(client, id: id)
            XCTAssertNil(removed)
        }
    }

    private static func portableSourceInstance(_ client: RuntimeHTTPClient, id: String) async throws -> RuntimeObject? {
        guard case .object(let status) = try await client.call(module: .source, operation: "status"),
              case .array(let instances)? = status["instances"] else {
            throw RuntimeHTTPError.invalidResponse
        }
        for value in instances {
            guard case .object(let instance) = value else { throw RuntimeHTTPError.invalidResponse }
            if instance["installationId"] == .string(id) { return instance }
        }
        return nil
    }

    @MainActor
    func testEmbeddedReaderReturnsNormalizedBooksWithoutAnotherHost() async throws {
        let resources = try await hostResources()
        let client = try await resources.scriptSession.connection()
        let id = "ios-reader-" + UUID().uuidString.lowercased()
        let definition = RuntimeValue.object([
            "bookSourceName": .string("Reader fixture"),
            "bookSourceUrl": .string("https://books.invalid/"),
            "html": .string("""
            <script>
              async function search(keyword, page) {
                await Promise.resolve();
                return [{name: keyword, author: 'Fixture Author', bookUrl: '/book/' + page}];
              }
            </script>
            """)
        ])
        _ = try await client.upload(module: .source, installationID: id, sourceRevision: 1,
            source: RuntimeJSON.encode(definition), basename: "reader.json")
        let loaded = try await client.call(module: .source, operation: "load", parameters: .object([
            "installationId": .string(id), "sourceRevision": .number(1),
            "revision": .number(1), "expectedRevision": .number(0), "protocol": .string("qysg")
        ]))
        addTeardownBlock {
            _ = try await client.call(module: .source, operation: "unload",
                parameters: .object(["installationId": .string(id), "revision": .number(1)]))
        }
        guard case .object(let root) = loaded, case .object(let instance)? = root["instance"],
              case .number(let generation)? = instance["generation"] else {
            return XCTFail("Missing Reader instance")
        }
        let result = try await client.call(module: .source, operation: "invoke", parameters: .object([
            "installationId": .string(id), "revision": .number(1), "generation": .number(generation),
            "action": .string("search"),
            "payload": .object(["keyword": .string("测试书籍"), "page": .number(2)])
        ]))
        guard case .object(let value) = result, case .array(let books)? = value["books"],
              books.count == 1, case .object(let book) = books[0] else {
            return XCTFail("Reader did not return one normalized book")
        }
        XCTAssertEqual(value["page"], .number(2))
        XCTAssertEqual(book["name"], .string("测试书籍"))
        XCTAssertEqual(book["author"], .string("Fixture Author"))
        XCTAssertEqual(book["bookUrl"], .string("https://books.invalid/book/2"))
        XCTAssertEqual(book["tocUrl"], book["bookUrl"])
        XCTAssertEqual(book["originName"], .string("Reader fixture"))
        let shared = try await resources.scriptSession.connection()
        XCTAssertTrue(client === shared)
    }
}

/// Run in its own xcodebuild invocation: App shutdown is terminal for this process.
@MainActor
final class MeowfilmScriptShutdownHostTests: XCTestCase {
    func testMusicCloseLeavesCatAvailableUntilAppShutdown() async throws {
        guard ProcessInfo.processInfo.environment["MEOWFILM_SCRIPT_SHUTDOWN"] == "1" else {
            throw XCTSkip("Run this terminal App lifecycle scenario in its own process")
        }
        let resources = try await hostResources()
        let app = try XCTUnwrap(UIApplication.shared.delegate as? MeowfilmAppDelegate)
        let assembly = try XCTUnwrap(app.readyAssembly())
        let connection = try await resources.scriptSession.connection()
        _ = try await resources.musicScripts.scriptSources()
        try await resources.musicScripts.close()
        try await resources.musicScripts.close()
        XCTAssertFalse(connection.isClosed)
        guard case .object(let status) = try await connection.call(module: .cat, operation: "status") else {
            return XCTFail("Missing Cat status after music shutdown")
        }
        guard case .array? = status["instances"] else { return XCTFail("Missing direct Cat status value") }
        assembly.shutdown()
        await assembly.waitForShutdown()
        XCTAssertNil(assembly.shutdownError)
        XCTAssertTrue(connection.isClosed)
        do {
            _ = try await resources.scriptSession.connection()
            XCTFail("App shutdown must close the shared session")
        } catch { XCTAssertEqual(error as? RuntimeHTTPError, .closed) }
    }
}

/// Uses the App's real publication path with synthetic local stores and a
/// transport that rejects every request. No Keychain or runtime is opened.
@MainActor
final class MeowfilmMusicStartupHostTests: XCTestCase {
    func testLocalPreparationHoldsPublicationAndCacheMissEntersBeforeLibraryLoad() async {
        let fixture = MusicStartupFixture()
        addTeardownBlock { await fixture.close() }
        let preparation = MusicStartupPreparation()
        defer { preparation.release() }
        var publications = 0
        let observation = fixture.assembly.$music.sink { model in
            guard let model else { return }
            publications += 1
            XCTAssertTrue(model.isAvailable)
            XCTAssertFalse(model.hasLoadedLibrary, "Full library loading must follow publication")
            XCTAssertGreaterThan(fixture.document.reads, 0, "The durable source must be read first")
            XCTAssertEqual(fixture.home.reads, 1, "A cache miss is still a completed local read")
        }
        defer { observation.cancel() }
        let startup = Task { await fixture.prepare { await preparation.wait() } }
        await fulfillment(of: [preparation.entered], timeout: 5)
        XCTAssertNil(fixture.assembly.music)
        XCTAssertFalse(fixture.assembly.startupFailed)
        XCTAssertEqual(fixture.modelsCreated, 0)
        XCTAssertEqual(fixture.document.reads, 0)
        XCTAssertEqual(fixture.home.reads, 0)
        XCTAssertEqual(publications, 0)

        preparation.release()
        await startup.value
        XCTAssertNotNil(fixture.assembly.music)
        XCTAssertFalse(fixture.assembly.startupFailed)
        XCTAssertEqual(fixture.modelsCreated, 1)
        XCTAssertEqual(publications, 1)
        let networkCalls = await fixture.network.calls
        XCTAssertEqual(networkCalls, 0, "Local preparation must not verify an account or fetch home")
    }

    func testLocalAccountFailureDoesNotPublishOrReadHomeAsGuest() async {
        let fixture = MusicStartupFixture()
        addTeardownBlock { await fixture.close() }
        await fixture.prepare { throw MusicServiceError.storageFailed }
        XCTAssertTrue(fixture.assembly.startupFailed)
        XCTAssertNil(fixture.assembly.music)
        XCTAssertEqual(fixture.modelsCreated, 0)
        XCTAssertEqual(fixture.document.reads, 0)
        XCTAssertEqual(fixture.home.reads, 0)
        let networkCalls = await fixture.network.calls
        XCTAssertEqual(networkCalls, 0)
    }

    func testPreparedHomeSnapshotIsAvailableToThePublishedModelWithoutAnotherDiskRead() async throws {
        let snapshot = Data(#"""
        {"schema":3,"source":"wy","sections":[{"id":"saved","title":"最近首页","layout":"list","items":[
          {"id":"playlist:42","title":"合成歌单","subtitle":"","artwork":null,"kind":"collection",
           "collection":{"id":"42","kind":"playlist","title":"合成歌单","creator":"","artwork":null,"count":2}}
        ]}]}
        """#.utf8)
        let fixture = MusicStartupFixture(homeData: snapshot)
        addTeardownBlock { await fixture.close() }
        let observation = fixture.assembly.$music.sink { model in
            if model != nil { XCTAssertEqual(fixture.home.reads, 1) }
        }
        defer { observation.cancel() }
        await fixture.prepare {}
        let model = try XCTUnwrap(fixture.assembly.music)
        let browser = try XCTUnwrap(model.publicBrowser as? any MusicHomeBrowsing)
        let restored = try await browser.restoredHome()
        let page = try XCTUnwrap(restored)
        XCTAssertEqual(page.sections.first?.title, "最近首页")
        XCTAssertEqual(page.sections.first?.items.first?.title, "合成歌单")
        XCTAssertEqual(fixture.home.reads, 1, "The first page consumes the App's pre-read snapshot")
        let networkCalls = await fixture.network.calls
        XCTAssertEqual(networkCalls, 0)
        XCTAssertFalse(fixture.assembly.startupFailed)
    }

    func testSourceReadFailureDoesNotPublishADefaultHome() async {
        let fixture = MusicStartupFixture(documentFailure: .storageFailed)
        addTeardownBlock { await fixture.close() }
        var accountsPrepared = false
        await fixture.prepare { accountsPrepared = true }
        XCTAssertTrue(accountsPrepared)
        XCTAssertTrue(fixture.assembly.startupFailed)
        XCTAssertNil(fixture.assembly.music)
        XCTAssertEqual(fixture.modelsCreated, 0)
        XCTAssertEqual(fixture.document.reads, 1)
        XCTAssertEqual(fixture.home.reads, 0)
        let networkCalls = await fixture.network.calls
        XCTAssertEqual(networkCalls, 0)
    }

    func testCancelledPreparationCannotPublishALateSuccessOrFailure() async {
        for fails in [false, true] {
            let fixture = MusicStartupFixture()
            addTeardownBlock { await fixture.close() }
            let preparation = MusicStartupPreparation()
            let startup = Task {
                await fixture.prepare {
                    await preparation.wait()
                    if fails { throw MusicServiceError.storageFailed }
                }
            }
            await fulfillment(of: [preparation.entered], timeout: 5)
            startup.cancel()
            preparation.release()
            await startup.value
            XCTAssertNil(fixture.assembly.music)
            XCTAssertFalse(fixture.assembly.startupFailed)
            XCTAssertEqual(fixture.modelsCreated, 0)
            XCTAssertEqual(fixture.home.reads, 0)
        }
    }

    func testShutdownDuringPreparationClosesServiceAndRejectsLateCompletion() async {
        for fails in [false, true] {
            let fixture = MusicStartupFixture()
            let preparation = MusicStartupPreparation()
            let startup = Task {
                await fixture.prepare {
                    await preparation.wait()
                    if fails { throw MusicServiceError.storageFailed }
                }
            }
            await fulfillment(of: [preparation.entered], timeout: 5)
            await fixture.close()
            preparation.release()
            await startup.value
            XCTAssertNil(fixture.assembly.music)
            XCTAssertFalse(fixture.assembly.startupFailed)
            XCTAssertEqual(fixture.modelsCreated, 0)
            XCTAssertEqual(fixture.home.reads, 0)
            do {
                _ = try await fixture.service.homeSourceSelection()
                XCTFail("Shutdown must close the native owner, not just hide publication")
            } catch { XCTAssertEqual(error as? MusicServiceError, .closed) }
        }
    }

    func testStartupFailureUsesAlertAndPreservesTheOpenSettingsRoute() async throws {
        let fixture = MusicStartupFixture()
        addTeardownBlock { await fixture.close() }
        await waitForStartupUI {
            UIApplication.shared.connectedScenes.contains { $0.activationState == .foregroundActive }
        }
        let scene = try XCTUnwrap(UIApplication.shared.connectedScenes
            .compactMap { $0 as? UIWindowScene }.first { $0.activationState == .foregroundActive })
        let previousWindow = scene.windows.first { $0.isKeyWindow }
        let window = UIWindow(windowScene: scene)
        window.frame = scene.coordinateSpace.bounds
        window.windowLevel = .normal + 1
        var resetRequests = 0
        let coordinator = MeowfilmCoordinator(window: window, assembly: fixture.assembly,
                                              resetUserData: { resetRequests += 1 })
        coordinator.start()
        defer {
            if let root = window.rootViewController { PageViewController.invalidatePages(in: root) }
            window.isHidden = true; window.rootViewController = nil
            previousWindow?.makeKey()
        }
        let root = try XCTUnwrap(window.rootViewController as? MeowfilmRootContainerViewController)
        let startupPage = try XCTUnwrap(root.navigation.viewControllers.first as? PageViewController)
        await waitForStartupUI { startupPage.canPresentPageAlert }
        let activity = try XCTUnwrap(startupDescendants(startupPage.view)
            .first { $0.accessibilityIdentifier == "startup.loading" } as? UIActivityIndicatorView)
        XCTAssertTrue(activity.isAnimating)
        XCTAssertNil(root.dock)
        attachStartupWindow(window, name: "music-startup-local-preparation")
        let settings = try XCTUnwrap(startupDescendants(startupPage.view)
            .first { $0.accessibilityIdentifier == "startup.settings" } as? UIControl)
        settings.sendActions(for: .touchUpInside)
        await waitForStartupUI { root.navigation.topViewController is MeowfilmSettingsViewController }
        let settingsPage = try XCTUnwrap(root.navigation.topViewController as? MeowfilmSettingsViewController)
        await waitForStartupUI { settingsPage.canPresentPageAlert }
        let table = try XCTUnwrap(startupDescendants(settingsPage.view).compactMap { $0 as? UITableView }.first)
        XCTAssertEqual(table.numberOfRows(inSection: 0), 2)

        await fixture.prepare { throw MusicServiceError.storageFailed }
        await waitForStartupUI {
            root.presentedViewController is UIAlertController &&
                root.presentedViewController?.isBeingPresented == false
        }
        let alert = try XCTUnwrap(root.presentedViewController as? UIAlertController)
        XCTAssertEqual(alert.message, "本地数据不可用")
        XCTAssertEqual(alert.actions.map(\.title), ["确定"])
        XCTAssertTrue(root.navigation.viewControllers.first === startupPage)
        XCTAssertTrue(root.navigation.topViewController === settingsPage)
        XCTAssertFalse(activity.isAnimating)
        XCTAssertTrue(settings.isEnabled)
        XCTAssertEqual(table.numberOfRows(inSection: 0), 2)
        XCTAssertEqual(resetRequests, 0)
        XCTAssertFalse(startupDescendants(startupPage.view).compactMap { ($0 as? UILabel)?.text }
            .contains("本地数据不可用"), "The error belongs only to the alert")
        XCTAssertNil(root.dock)
        XCTAssertNil(fixture.assembly.music)
        attachStartupWindow(window, name: "music-startup-local-failure")
        withExtendedLifetime(coordinator) {}
    }

    private func attachStartupWindow(_ window: UIWindow, name: String) {
        XCTAssertEqual(window.windowScene?.activationState, .foregroundActive)
        window.layoutIfNeeded()
        var rendered = false
        let image = UIGraphicsImageRenderer(bounds: window.bounds).image { _ in
            rendered = window.drawHierarchy(in: window.bounds, afterScreenUpdates: true)
        }
        XCTAssertTrue(rendered)
        let attachment = XCTAttachment(image: image)
        attachment.name = name; attachment.lifetime = .keepAlways
        add(attachment)
    }

    private func waitForStartupUI(_ predicate: @escaping @MainActor () -> Bool) async {
        let ready = expectation(for: NSPredicate { _, _ in MainActor.assumeIsolated { predicate() } },
                                evaluatedWith: nil)
        await fulfillment(of: [ready], timeout: 5)
        XCTAssertTrue(predicate())
    }

    private func startupDescendants(_ view: UIView) -> [UIView] {
        [view] + view.subviews.flatMap(startupDescendants)
    }
}

/// Real foreground scene required for presentation-layer animation and native frames.
@MainActor
final class MeowfilmMusicPlayerMorphHostTests: XCTestCase {
    func testCardMorphKeepsButtonsStationaryDuringExpansionAndReversal() async throws {
        XCTAssertFalse(UIAccessibility.isReduceMotionEnabled, "This case exercises the animated morph")
        XCTAssertTrue(UIView.areAnimationsEnabled)
        let foreground = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated {
                UIApplication.shared.connectedScenes.contains { $0.activationState == .foregroundActive }
            }
        }, evaluatedWith: nil)
        await fulfillment(of: [foreground], timeout: 5)
        let scene = try XCTUnwrap(UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }
            .first { $0.activationState == .foregroundActive })
        let network = MusicStartupNoNetwork()
        let service = NativeMusicService(document: MusicStartupDocument(failure: nil),
                                        qq: network, transport: network)
        let playback = PlaybackController()
        let model = MusicViewModel(service: service, playback: playback)
        model.nativeAttempt = .init(id: UUID(), resourceID: "music:dock")
        model.playbackState.queue = [.init(id: "dock", track: .init(ref: .init(kind: .queue, id: "dock"),
            songId: "dock", title: "Goodbye", artist: "Ramsey"))]
        model.playbackState.state = .init(currentId: "dock", total: 1)
        model.playbackState.phase = .paused
        let content = MusicPlayerContentModel(model: model, playback: playback,
            appearance: MusicAppearancePreferencesStore(defaults: UserDefaults(suiteName: UUID().uuidString)!),
            presentation: MusicMiniPlayerPresentation())
        addTeardownBlock { await model.close(); await service.close() }
        let surface = MusicPlayerSurface(content: content)
        let root = PageViewController(themeProvider: content.appearance)
        let previousWindow = scene.windows.first { $0.isKeyWindow }
        let window = UIWindow(windowScene: scene)
        window.frame = scene.coordinateSpace.bounds
        window.windowLevel = .normal + 1
        window.rootViewController = root
        window.makeKeyAndVisible()
        defer {
            window.isHidden = true; window.rootViewController = nil
            previousWindow?.makeKey()
        }
        root.view.addSubview(surface)
        let bottom = root.view.bounds.height - 86
        func layoutDock() {
            surface.frame = CGRect(x: 14, y: bottom - surface.dockHeight,
                                   width: root.view.bounds.width - 28, height: surface.dockHeight)
        }
        surface.onSizeChanged = layoutDock
        defer { surface.onSizeChanged = nil }
        layoutDock()
        root.view.layoutIfNeeded()
        func expandDock() async {
            let intent = content.presentation.beginHomePlayback()
            XCTAssertTrue(content.presentation.finishHomePlayback(intent, playback: .init(
                occurrenceID: "dock", nativeAttemptID: model.activeNativeAttemptID!,
                accessGeneration: model.accountAccessGeneration)))
            await withCheckedContinuation { continuation in
                RunLoop.main.perform { continuation.resume() }
            }
            XCTAssertTrue(content.dockExpanded)
        }
        func descendants(_ view: UIView) -> [UIView] {
            view.subviews.flatMap { [$0] + descendants($0) }
        }
        try await Task.sleep(nanoseconds: 50_000_000)
        let buttons = try ["favorite", "previous", "card.play", "next", "card.queue", "play", "queue"].map { name in
            try XCTUnwrap(descendants(surface).compactMap { $0 as? UIButton }.first {
                $0.accessibilityIdentifier == "music.miniPlayer." + name
            })
        }
        let frames = buttons.map { $0.convert($0.bounds, to: window) }
        let compactHeight = surface.bounds.height
        func checkSlots() throws {
            for (button, expected) in zip(buttons, frames) {
                let layer = try XCTUnwrap(button.layer.presentation())
                let actual = layer.convert(layer.bounds, to: try XCTUnwrap(window.layer.presentation()))
                XCTAssertEqual(actual.minX, expected.minX, accuracy: 0.5)
                XCTAssertEqual(actual.minY, expected.minY, accuracy: 0.5)
                XCTAssertEqual(actual.width, expected.width, accuracy: 0.5)
                XCTAssertEqual(actual.height, expected.height, accuracy: 0.5)
            }
        }
        func attachFrame(_ name: String) throws {
            XCTAssertEqual(window.windowScene?.activationState, .foregroundActive)
            let layer = try XCTUnwrap(window.layer.presentation())
            let image = UIGraphicsImageRenderer(size: window.bounds.size).image { context in
                layer.render(in: context.cgContext)
            }
            let attachment = XCTAttachment(image: image)
            attachment.name = name; attachment.lifetime = .keepAlways
            add(attachment)
        }
        try attachFrame("dock-morph-compact")
        await expandDock()
        for index in 0..<4 {
            try await Task.sleep(nanoseconds: 50_000_000)
            try checkSlots()
            if index == 0 {
                let height = try XCTUnwrap(surface.layer.presentation()).bounds.height
                XCTAssertGreaterThan(height, compactHeight)
                XCTAssertLessThan(height, surface.dockHeight)
            }
            if index == 1 { try attachFrame("dock-morph-expanding") }
        }
        surface.collapse()
        try await Task.sleep(nanoseconds: 80_000_000)
        try checkSlots()
        try attachFrame("dock-morph-reversing")
        await expandDock()
        for _ in 0..<4 {
            try await Task.sleep(nanoseconds: 50_000_000)
            try checkSlots()
        }
        try await Task.sleep(nanoseconds: 700_000_000)
        try checkSlots()
        try attachFrame("dock-morph-card")
        XCTAssertEqual(surface.form, .card)
        surface.collapse()
        try await Task.sleep(nanoseconds: 500_000_000)
        try checkSlots()
        XCTAssertEqual(surface.form, .compact)
        XCTAssertTrue(buttons.prefix(5).allSatisfy { $0.alpha == 0 && !$0.isUserInteractionEnabled })
        XCTAssertTrue(buttons.suffix(2).allSatisfy { $0.alpha == 1 && $0.isUserInteractionEnabled })
    }

    func testCapsuleTitleTracksDragSettlementAndReturnInForegroundScene() async throws {
        XCTAssertFalse(UIAccessibility.isReduceMotionEnabled, "This case observes animated native frames")
        XCTAssertTrue(UIView.areAnimationsEnabled)
        let foreground = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated {
                UIApplication.shared.connectedScenes.contains { $0.activationState == .foregroundActive }
            }
        }, evaluatedWith: nil)
        await fulfillment(of: [foreground], timeout: 5)
        let scene = try XCTUnwrap(UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }
            .first { $0.activationState == .foregroundActive })
        let network = MusicStartupNoNetwork()
        let service = NativeMusicService(document: MusicStartupDocument(failure: nil),
                                        qq: MusicCapsuleHostCatalog(), transport: network)
        try await service.setAccess(available: true, generation: 1)
        let page = try await service.search(platform: "tx", query: "Fixture", page: 1, limit: 3)
        _ = try await service.changeQueue(.replace(page.items.map(\.ref), index: 0), expectedRevision: 0)
        let playback = PlaybackController()
        let model = MusicViewModel(service: service, playback: playback)
        await model.load()
        let origin = try XCTUnwrap(model.current?.id)
        let nextID = try XCTUnwrap(model.recordPreview(.next)?.id)
        let content = MusicPlayerContentModel(model: model, playback: playback,
            appearance: MusicAppearancePreferencesStore(defaults: UserDefaults(suiteName: UUID().uuidString)!),
            presentation: MusicMiniPlayerPresentation())
        addTeardownBlock { await model.close(); await service.close() }
        let surface = MusicPlayerSurface(content: content)
        let root = PageViewController(themeProvider: content.appearance)
        let previousWindow = scene.windows.first { $0.isKeyWindow }
        let window = UIWindow(windowScene: scene)
        window.frame = scene.coordinateSpace.bounds
        window.windowLevel = .normal + 1
        window.rootViewController = root
        window.makeKeyAndVisible()
        defer {
            window.isHidden = true; window.rootViewController = nil
            previousWindow?.makeKey()
        }
        root.view.addSubview(surface)
        surface.frame = CGRect(x: 14, y: root.view.bounds.height - 86 - surface.dockHeight,
                               width: root.view.bounds.width - 28, height: surface.dockHeight)
        root.view.layoutIfNeeded()
        func descendants(_ view: UIView) -> [UIView] { view.subviews.flatMap { [$0] + descendants($0) } }
        let views = descendants(surface)
        let viewport = try XCTUnwrap(views.first { $0.accessibilityIdentifier == "music.miniPlayer.titleSwipe" })
        let incoming = try XCTUnwrap(views.compactMap { $0 as? UILabel }.first {
            $0.accessibilityIdentifier == "music.miniPlayer.titleSwipe.next"
        })
        let outgoing = try XCTUnwrap(views.compactMap { $0 as? UILabel }.first {
            $0.accessibilityIdentifier == "music.miniPlayer.titleSwipe.current"
        })
        let track = try XCTUnwrap(incoming.superview)
        let title = try XCTUnwrap(descendants(viewport).compactMap { $0 as? LoopingTextView }.first)
        let buttons = try ["open", "play", "queue"].map { name in
            try XCTUnwrap(views.compactMap { $0 as? UIButton }.first {
                $0.accessibilityIdentifier == "music.miniPlayer." + name
            })
        }
        buttons[0].setImage(UIGraphicsImageRenderer(size: CGSize(width: 42, height: 42)).image { context in
            UIColor.systemTeal.setFill(); context.fill(CGRect(x: 0, y: 0, width: 42, height: 42))
            UIColor.white.setFill(); context.fill(CGRect(x: 12, y: 8, width: 6, height: 26))
        }, for: .normal)
        let frames = buttons.map { $0.convert($0.bounds, to: window) }
        func checkFixedControls() throws {
            for (button, expected) in zip(buttons, frames) {
                let layer = try XCTUnwrap(button.layer.presentation())
                let actual = layer.convert(layer.bounds, to: try XCTUnwrap(window.layer.presentation()))
                XCTAssertEqual(actual.minX, expected.minX, accuracy: 0.5)
                XCTAssertEqual(actual.minY, expected.minY, accuracy: 0.5)
                XCTAssertEqual(actual.size.width, expected.width, accuracy: 0.5)
                XCTAssertEqual(actual.size.height, expected.height, accuracy: 0.5)
            }
        }
        func attachFrame(_ name: String) throws {
            XCTAssertEqual(window.windowScene?.activationState, .foregroundActive)
            let layer = try XCTUnwrap(window.layer.presentation())
            let image = UIGraphicsImageRenderer(size: window.bounds.size).image { layer.render(in: $0.cgContext) }
            let attachment = XCTAttachment(image: image)
            attachment.name = name; attachment.lifetime = .keepAlways; add(attachment)
        }
        let input = MusicCapsuleHostPan()
        for (index, dx) in [CGFloat(-64), CGFloat(64)].enumerated() {
            input.sampleTranslation = CGPoint(x: dx, y: 0)
            XCTAssertTrue(surface.gestureRecognizerShouldBegin(input))
            input.sampleState = .began; surface.panTitle(input)
            input.sampleState = .changed; surface.panTitle(input)
            try await Task.sleep(nanoseconds: 50_000_000)
            XCTAssertFalse(title.isHidden, "Dragging pauses the actual title rather than replacing its marquee")
            let draggingX = try XCTUnwrap(track.layer.presentation()).affineTransform().tx
            XCTAssertEqual(draggingX, dx, accuracy: 0.5)
            try checkFixedControls()
            try attachFrame(index == 0 ? "capsule-drag-next" : "capsule-drag-previous")
            let oldText = outgoing.text
            input.sampleState = .ended; surface.panTitle(input)
            try await Task.sleep(nanoseconds: 50_000_000)
            let settlingX = abs(try XCTUnwrap(track.layer.presentation()).affineTransform().tx)
            XCTAssertGreaterThan(settlingX, abs(dx))
            XCTAssertLessThan(settlingX, viewport.bounds.width + 16)
            XCTAssertEqual(outgoing.text, oldText, "The outgoing title cannot become the newly selected song")
            try checkFixedControls()
            try attachFrame(index == 0 ? "capsule-settling-next" : "capsule-settling-previous")
            let settled = expectation(for: NSPredicate { _, _ in
                MainActor.assumeIsolated {
                    !model.isNavigating && track.transform == .identity && !title.isHidden &&
                        model.current?.id == (index == 0 ? nextID : origin)
                }
            }, evaluatedWith: nil)
            await fulfillment(of: [settled], timeout: 5)
            XCTAssertEqual(model.current?.id, index == 0 ? nextID : origin)
            XCTAssertEqual(title.accessibilityLabel, MusicMiniPlayerText.compactTitle(model.displayTrack))
            try checkFixedControls()
        }
        model.playbackState.state = .init(revision: model.state.revision, currentId: origin, total: 3)
        input.sampleTranslation = CGPoint(x: -64, y: 0)
        input.sampleState = .began; surface.panTitle(input)
        input.sampleState = .ended; surface.panTitle(input)
        try await Task.sleep(nanoseconds: 50_000_000)
        let returningX = try XCTUnwrap(track.layer.presentation()).affineTransform().tx
        XCTAssertGreaterThan(returningX, -64)
        XCTAssertLessThan(returningX, 0)
        try checkFixedControls()
        try attachFrame("capsule-return-unavailable")
        try await Task.sleep(nanoseconds: 250_000_000)
        XCTAssertEqual(track.transform, .identity)
        XCTAssertEqual(model.current?.id, origin)
        XCTAssertFalse(model.isNavigating)
        try attachFrame("capsule-returned")
    }

}

@MainActor
private final class MusicCapsuleHostPan: UIPanGestureRecognizer {
    var sampleState: UIGestureRecognizer.State = .possible
    var sampleTranslation = CGPoint.zero
    override var state: UIGestureRecognizer.State {
        get { sampleState }
        set { sampleState = newValue }
    }
    override func translation(in view: UIView?) -> CGPoint { sampleTranslation }
}

private actor MusicCapsuleHostCatalog: MusicPublicQQProviding {
    func search(query: String, page: Int, limit: Int) throws -> MusicNativeTrackPage {
        let items = try ["First light", "Evening breeze", "Quiet morning"].enumerated().map { index, title in
            MusicNativeTrackData(platform: "tx", data: try JSONSerialization.data(withJSONObject: [
                "songmid": "CapsuleFixture\(index)", "name": title, "singer": "Fixture",
                "duration": 180, "types": [["type": "128k"]]
            ]))
        }
        return .init(items: items)
    }
    func lyrics(mid: String) -> MusicNativeLyrics { .init(text: "") }
}

/// Exercises the real Assembly shutdown entry with only synthetic local media
/// and a feedback peer deliberately held until after product teardown.
@MainActor
final class MeowfilmMusicShutdownHostTests: XCTestCase {
    func testAssemblyReleaseClosesPublishedModelWithoutExplicitShutdown() async throws {
        let document = MusicStartupDocument(failure: nil)
        let accounts = MusicShutdownAccounts(), player = MusicShutdownPlayer(), network = MusicStartupNoNetwork()
        let service = NativeMusicService(document: document, accounts: accounts, qq: accounts, transport: network)
        var assembly: MeowfilmAssembly? = MeowfilmAssembly()
        await assembly?.prepareMusic(service, prepareAccounts: {}, makeModel: {
            MusicViewModel(service: service, player: player)
        })
        let model = try XCTUnwrap(assembly?.music)
        weak var released = assembly
        assembly = nil
        XCTAssertNil(released, "The published model's teardown must not retain its Assembly")
        let closed = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated { model.isClosed }
        }, evaluatedWith: nil)
        await fulfillment(of: [closed], timeout: 3)
        await service.close()
        let calls = await network.calls; XCTAssertEqual(calls, 0)
    }

    func testShutdownStopsAndDrainsNativeFactsBeforeRevokingAccessWithoutWaitingForACK() async throws {
        let assembly = MeowfilmAssembly(), document = MusicStartupDocument(failure: nil)
        let accounts = MusicShutdownAccounts(), player = MusicShutdownPlayer(), network = MusicStartupNoNetwork()
        let service = NativeMusicService(document: document, accounts: accounts, qq: accounts,
            transport: network, monotonicClock: { 0 })
        addTeardownBlock {
            await accounts.releaseStart()
            await MainActor.run { assembly.shutdown() }
            await assembly.waitForShutdown()
        }
        await assembly.prepareMusic(service, prepareAccounts: {}, makeModel: {
            MusicViewModel(service: service, player: player)
        })
        let model = try XCTUnwrap(assembly.music)
        player.onEvent = { [weak model] in model?.onPlaybackEvent($0) }
        _ = try await service.updateSettings(.init(ifRevision: 0, lyricsEnabled: false, requestQuality: "128k"))
        _ = try await service.setPlaybackFeedback(platform: .qq, enabled: true, expectedRevision: 1)
        let page = try await service.search(platform: "tx", query: "SyntheticMID", page: 1, limit: 30)
        _ = try await service.changeQueue(.replace(page.items.map(\.ref), index: 0), expectedRevision: 0)
        await model.load()
        await model.switchQuality("128k")
        await model.flushPlaybackFacts()
        await fulfillment(of: [accounts.startDispatched], timeout: 3)
        let active = await accounts.activeCount(); XCTAssertEqual(active, 1)
        let occurrence = try XCTUnwrap(model.activeOccurrenceID)
        var stopKeptOwner = false
        player.onStop = { [weak model] in
            stopKeptOwner = model?.isAvailable == true && model?.isClosed == false &&
                model?.activeOccurrenceID == occurrence
        }
        await accounts.observeClose { try? await service.state() }
        player.position = 7
        assembly.shutdown()
        XCTAssertEqual(player.stops, 1)
        XCTAssertTrue(stopKeptOwner, "Native stop must precede setAvailable(false) or candidate retirement")
        XCTAssertFalse(model.canNavigate)
        assembly.shutdown()
        XCTAssertEqual(player.stops, 1)
        let ended = expectation(description: "Actual Assembly shutdown completes while feedback ACK is held")
        let closing = Task { await assembly.waitForShutdown(); ended.fulfill() }
        await fulfillment(of: [ended], timeout: 3)
        let settledBeforeACK = model.isClosed
        let drained = await accounts.stateAtClose()
        XCTAssertEqual(drained?.currentId, occurrence)
        XCTAssertEqual(drained?.position, 7, "The final native fact must drain before access revocation")
        let data = try XCTUnwrap(document.read())
        let root = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
        let records = try XCTUnwrap(root["records"] as? [String: Any])
        let playback = try XCTUnwrap(records["[\"meowfilm\",\"music\",1,\"device\",\"playback\"]"] as? [String: Any])
        let position = try XCTUnwrap(playback["position"] as? [String: Any])
        XCTAssertEqual(position["occurrenceId"] as? String, occurrence)
        XCTAssertEqual(position["seconds"] as? Double, 0, "The existing queue contract does not restore elapsed media time")
        XCTAssertTrue(settledBeforeACK)
        let revoked = await accounts.activeCount(); XCTAssertEqual(revoked, 0)
        do { _ = try await service.state(); XCTFail("Product access must close after the local drain") }
        catch { XCTAssertEqual(error as? MusicServiceError, .closed) }
        await accounts.releaseStart(); await closing.value
        let reports = await accounts.recorded()
        XCTAssertEqual(reports.map(\.event), [.start], "A receipt arriving after access revocation cannot send an end")
        XCTAssertNil(assembly.shutdownError)
        await model.close()
        XCTAssertEqual(player.stops, 1)
        let calls = await network.calls; XCTAssertEqual(calls, 0)
    }
}

@MainActor
private final class MusicShutdownPlayer: MusicPlaybackHandoffDriving {
    var onEvent: ((PlaybackEvent) -> Void)?
    var onStop: (() -> Void)?
    var position = 0.0
    var stops = 0
    func play(_ resource: PlaybackResource) -> MusicPlayerAttempt {
        let native = MusicPlayerAttempt(id: UUID(), resourceID: resource.id)
        onEvent?(.init(attempt: .init(id: native.id, owner: "music", resourceID: resource.id), kind: .played,
            snapshot: .init(owner: "music", resourceID: resource.id, phase: .playing, position: position, duration: 180),
            wantsPlayback: true, observedAt: position, occurredAt: 1000 + position))
        return native
    }
    func preparePlayback(_ resource: PlaybackResource, autoplay: Bool) async throws -> MusicPreparedPlayback {
        .init(duration: 180, synchronize: {}, commit: { self.play(resource) }, cancel: {})
    }
    func prepareReplacement(_ resource: PlaybackResource, replacing: MusicPlayerAttempt) async throws -> MusicPreparedPlayback {
        .init(duration: 180, synchronize: {}, commit: { self.play(resource) }, cancel: {})
    }
    func stop(_ attempt: MusicPlayerAttempt) {
        stops += 1; onStop?()
        onEvent?(.init(attempt: .init(id: attempt.id, owner: "music", resourceID: attempt.resourceID), kind: .stopped,
            snapshot: .init(owner: "music", resourceID: attempt.resourceID, phase: .idle, position: position, duration: 180),
            wantsPlayback: false, observedAt: position, occurredAt: 1000 + position))
    }
    func pause(_ attempt: MusicPlayerAttempt) {}
    func resume(_ attempt: MusicPlayerAttempt) {}
    func seek(_ position: Double, attempt: MusicPlayerAttempt) -> Bool { true }
}

private actor MusicShutdownAccounts: MusicNativeAccountProviding, MusicNativeFeedbackProviding,
    MusicNativeFeedbackLifecycleProviding, MusicPublicQQProviding {
    nonisolated let startDispatched = XCTestExpectation(description: "Synthetic feedback start reached its peer")
    private var receipt: CheckedContinuation<Void, Never>?
    private var leases: [MusicPlaybackFeedbackLease] = []
    private var reports: [MusicPlaybackFeedbackReport] = []
    private var closeObservation: (@Sendable () async -> MusicPlaybackState?)?
    private var closedState: MusicPlaybackState?
    func activeCount() -> Int { leases.filter { $0.authorization.isActive }.count }
    func recorded() -> [MusicPlaybackFeedbackReport] { reports }
    func releaseStart() { receipt?.resume(); receipt = nil }
    func observeClose(_ read: @escaping @Sendable () async -> MusicPlaybackState?) { closeObservation = read }
    func stateAtClose() -> MusicPlaybackState? { closedState }
    func configurePlaybackFeedback(_ settings: MusicPlaybackFeedbackSettings) {}
    nonisolated func cancelPendingPlaybackFeedback(platform: MusicAccountPlatform?) {}
    nonisolated func stopPlaybackFeedbackDispatchForClose() {}
    func preparePlaybackFeedbackForClose() async {
        let observation = closeObservation; closeObservation = nil
        if let observation { closedState = await observation() }
    }
    func beginPlaybackFeedback(platform: MusicAccountPlatform,
                               authorization: MusicPlaybackFeedbackAuthorization) async throws -> MusicPlaybackFeedbackLease {
        try await authorization.check()
        let own = MusicPlaybackFeedbackAuthorization { try await authorization.check() }
        let lease = MusicPlaybackFeedbackLease(platform: platform, scope: "synthetic-shutdown-account",
            generation: UUID(), authorization: own, send: { [weak self] value in
                guard let self else { throw MusicServiceError.closed }
                return try await self.report(value, authorization: own)
            })
        leases.append(lease); return lease
    }
    private func report(_ value: MusicPlaybackFeedbackReport,
                        authorization: MusicPlaybackFeedbackAuthorization) async throws -> MusicPlaybackFeedbackReceipt {
        try await authorization.check()
        reports.append(value)
        if value.event == .start {
            await withCheckedContinuation {
                receipt = $0; startDispatched.fulfill()
            }
        }
        try await authorization.check()
        return .confirmed
    }
    private func page() -> MusicNativeTrackPage {
        let data = Data(#"{"songmid":"SyntheticMID","name":"Synthetic shutdown","singer":"Fixture","albumName":"Synthetic","duration":180,"types":[{"type":"128k"}]}"#.utf8)
        return .init(items: [.init(platform: "tx", data: data)])
    }
    func search(query: String, page: Int, limit: Int) -> MusicNativeTrackPage { self.page() }
    func lyrics(mid: String) -> MusicNativeLyrics { .init(text: "") }
    func nativeRecommendations(platform: MusicAccountPlatform, kind: String, cursor: String?) -> MusicNativeTrackPage { page() }
    func nativeCollections(platform: MusicAccountPlatform, cursor: String?) -> MusicAccountCollectionPage { .init(items: [], cursor: nil) }
    func nativeTracks(platform: MusicAccountPlatform, collectionID: String, cursor: String?) -> MusicNativeTrackPage { page() }
    func nativeOptions(platform: MusicAccountPlatform, nativeID: String, sourceKey: String) -> [MusicPlaybackOption] {
        [.init(installationId: "official:synthetic-shutdown", generation: 1, siteId: "tx", sourceKey: sourceKey,
            qualities: ["128k"], title: "Synthetic", version: "1", contentHash: String(repeating: "a", count: 64))]
    }
    func nativeResolve(platform: MusicAccountPlatform, nativeID: String, selection: MusicPlaybackSelection) -> MusicNativeAudio {
        .init(url: URL(string: "https://media.invalid/not-requested")!,
            delivery: .init(quality: "128k", evidence: .officialResponse, duration: 180))
    }
}

@MainActor
private final class MusicStartupFixture {
    let assembly = MeowfilmAssembly()
    let document: MusicStartupDocument
    let home: MusicStartupHomeSnapshot
    let network = MusicStartupNoNetwork()
    let service: NativeMusicService
    private(set) var modelsCreated = 0

    init(documentFailure: MusicServiceError? = nil, homeData: Data? = nil) {
        document = MusicStartupDocument(failure: documentFailure)
        home = MusicStartupHomeSnapshot(data: homeData)
        service = NativeMusicService(document: document, qq: network, homeSnapshot: home, transport: network)
    }

    func prepare(accounts: () async throws -> Void) async {
        await assembly.prepareMusic(service, prepareAccounts: accounts, makeModel: {
            modelsCreated += 1
            return MusicViewModel(service: service, playback: assembly.playback)
        })
    }

    func close() async {
        assembly.shutdown()
        await assembly.waitForShutdown()
    }
}

@MainActor
private final class MusicStartupPreparation {
    let entered = XCTestExpectation(description: "Local account preparation held")
    private var continuation: CheckedContinuation<Void, Never>?
    func wait() async {
        await withCheckedContinuation {
            continuation = $0
            entered.fulfill()
        }
    }
    func release() {
        continuation?.resume()
        continuation = nil
    }
}

private final class MusicStartupDocument: MusicNativeDocumentStoring, @unchecked Sendable {
    private let lock = NSLock()
    private let failure: MusicServiceError?
    private var value: Data?
    private var count = 0
    init(failure: MusicServiceError?) { self.failure = failure }
    var reads: Int { lock.lock(); defer { lock.unlock() }; return count }
    func read() throws -> Data? {
        lock.lock(); defer { lock.unlock() }
        count += 1
        if let failure { throw failure }
        return value
    }
    func compareAndSwap(expected: Data?, replacement: Data) throws {
        lock.lock(); defer { lock.unlock() }
        guard value == expected else { throw MusicServiceError.storageConflict }
        value = replacement
    }
}

private final class MusicStartupHomeSnapshot: MusicHomeSnapshotStoring, @unchecked Sendable {
    private let lock = NSLock()
    private let data: Data?
    private var count = 0
    init(data: Data?) { self.data = data }
    var reads: Int { lock.lock(); defer { lock.unlock() }; return count }
    func read() -> Data? { lock.lock(); defer { lock.unlock() }; count += 1; return data }
    func write(_ data: Data) { XCTFail("Local startup must not replace the last successful home") }
    func clear() { XCTFail("Local startup must not erase the last successful home") }
}

private actor MusicStartupNoNetwork: MusicNativeHTTPTransport, MusicPublicQQProviding {
    private(set) var calls = 0
    func send(_ request: MusicNativeHTTPRequest) throws -> MusicNativeHTTPResponse {
        calls += 1; throw MusicServiceError.unavailable
    }
    func search(query: String, page: Int, limit: Int) throws -> MusicNativeTrackPage {
        calls += 1; throw MusicServiceError.unavailable
    }
    func lyrics(mid: String) throws -> MusicNativeLyrics {
        calls += 1; throw MusicServiceError.unavailable
    }
}
