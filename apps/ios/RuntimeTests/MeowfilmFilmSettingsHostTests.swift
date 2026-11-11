import XCTest
import UIKit
@testable import MeowFilmFeature
import MeowRuntime
import MeowStorage
import MeowUI
@testable import Meowfilm

extension XCTestCase {
    @MainActor
    func hostResources() async throws -> MeowfilmResources {
        let app = try XCTUnwrap(UIApplication.shared.delegate as? MeowfilmAppDelegate)
        let ready = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated { app.launch.phase == .ready }
        }, evaluatedWith: nil)
        await fulfillment(of: [ready], timeout: 10)
        let assembly = try XCTUnwrap(app.readyAssembly())
        await assembly.waitForStartup()
        return try XCTUnwrap(assembly.resources)
    }
}

@MainActor
final class MeowfilmFilmSettingsHostTests: XCTestCase {
    func testFilmSettingsConfirmationAndFailureRenderInForegroundApp() async throws {
        let resources = try await hostResources()
        let app = try XCTUnwrap(UIApplication.shared.delegate as? MeowfilmAppDelegate)
        let assembly = try XCTUnwrap(app.readyAssembly())
        await waitForFilmUI {
            UIApplication.shared.connectedScenes.contains { $0.activationState == .foregroundActive }
        }
        let scene = try XCTUnwrap(UIApplication.shared.connectedScenes
            .compactMap { $0 as? UIWindowScene }.first { $0.activationState == .foregroundActive })
        let previousWindow = scene.windows.first { $0.isKeyWindow }
        let page = FilmCatSourcesViewController(service: resources.filmSourceSettings, themeProvider: assembly.appearance)
        let window = UIWindow(windowScene: scene)
        window.frame = scene.coordinateSpace.bounds
        window.windowLevel = .normal + 1
        window.rootViewController = page
        window.makeKeyAndVisible()
        defer {
            PageViewController.invalidatePages(in: page)
            window.isHidden = true; window.rootViewController = nil
            previousWindow?.makeKey()
        }
        await waitForFilmUI { page.canPresentPageAlert }
        page.confirm("删除猫源？", message: "关联视频源也将删除。") { XCTFail("Presentation is not deletion consent") }
        await waitForFilmUI { page.presentedViewController != nil && page.presentedViewController?.isBeingPresented == false }
        let confirmation = try XCTUnwrap(page.presentedViewController as? UIAlertController)
        XCTAssertEqual(confirmation.title, "删除猫源？")
        XCTAssertEqual(confirmation.actions.map(\.style), [.cancel, .destructive])
        attachFilmWindow(window, "film-cats-delete-confirmation-foreground")
        let dismissed = expectation(description: "Confirmation dismissed without an action")
        confirmation.dismiss(animated: false) { dismissed.fulfill() }
        await fulfillment(of: [dismissed], timeout: 5)
        await waitForFilmUI { page.canPresentPageAlert }
        // Inject the file reader's real failure at the presentation boundary;
        // file reading and retry dispatch are covered by the component tests.
        page.failure(ImportedFileError.changed, retry: { XCTFail("Presentation must not retry automatically") })
        await waitForFilmUI { page.presentedViewController != nil && page.presentedViewController?.isBeingPresented == false }
        let failure = try XCTUnwrap(page.presentedViewController as? UIAlertController)
        XCTAssertEqual(failure.message, ImportedFileError.changed.localizedDescription)
        XCTAssertEqual(failure.actions.map(\.title), ["取消", "重试"])
        attachFilmWindow(window, "film-file-changed-failure-foreground")
    }

    private func waitForFilmUI(_ predicate: @escaping @MainActor () -> Bool) async {
        let ready = expectation(for: NSPredicate { _, _ in MainActor.assumeIsolated { predicate() } }, evaluatedWith: nil)
        await fulfillment(of: [ready], timeout: 10)
        XCTAssertTrue(predicate())
    }

    private func attachFilmWindow(_ window: UIWindow, _ name: String) {
        XCTAssertEqual(window.windowScene?.activationState, .foregroundActive)
        window.layoutIfNeeded()
        var rendered = false
        let image = UIGraphicsImageRenderer(bounds: window.bounds).image { _ in
            rendered = window.drawHierarchy(in: window.bounds, afterScreenUpdates: true)
        }
        XCTAssertTrue(rendered, "Foreground App window must render the complete alert")
        let attachment = XCTAttachment(image: image)
        attachment.name = name; attachment.lifetime = .keepAlways
        add(attachment)
    }

    func testNodeMobileRulesUseSameSavedAndDraftEntryAndKeepIsolatedUTF16() async throws {
        let resources = try await hostResources(), client = try await resources.scriptSession.connection()
        let service = FilmRuleService(connection: { client })
        let original = try await client.call(module: .film, operation: "rules.get").filmRequiredObject().filmValue("rules")
        addTeardownBlock {
            _ = try await client.call(module: .film, operation: "rules.set", parameters: .object(["rules": original]))
            await service.close()
        }
        do {
            try await service.initialize()
            let rules = FilmRuleSet(episodeCleanRegexRules: ["^广告"],
                episodeRules: [.init(pattern: "第([0-9]+)集", replace: "第$1集", flags: "u")],
                movieRules: [.init(pattern: "电影", replace: "影片", flags: "g")], aggregateRegexRules: ["高清"])
            _ = try await service.save(rules)
            for request in [FilmRuleRequest(section: .episode, text: "广告第12集"),
                            .init(section: .movie, text: "电影"), .init(section: .search, text: "高清斗破", query: "斗破")] {
                let saved = try await service.process(request)
                let draft = try await service.process(request, draft: rules)
                XCTAssertEqual(saved, draft)
            }
            let bytes = try await client.call(module: .film, operation: "rules.get")
            _ = try await service.process(.init(section: .movie, text: "电影"), draft: .init(movieRules: [.init(pattern: "[")]))
            let unchanged = try await client.call(module: .film, operation: "rules.get")
            XCTAssertEqual(unchanged, bytes)
            // Actual NodeMobile and Foundation JSON boundary. Without u a
            // legal JS replacement can split a surrogate pair.
            let utf16Rules = FilmRuleSet(movieRules: [.init(pattern: "^.", replace: "x")])
            let utf16Draft = try await service.process(.init(section: .movie, text: "🎬"), draft: utf16Rules)
            XCTAssertEqual(utf16Draft.type, "success"); XCTAssertTrue(utf16Draft.matched)
            XCTAssertEqual(utf16Draft.rewrittenText?.utf16, [0x78, 0xDFAC])
            XCTAssertEqual(utf16Draft.outputText.utf16, [0x78, 0xDFAC])
            XCTAssertEqual(utf16Draft.text.utf16, Array("命中第 1 条：x".utf16) + [0xDFAC])
            XCTAssertEqual(utf16Draft.inputText.utf16, [0xD83C, 0xDFAC])
            XCTAssertEqual(utf16Draft.outputText.displayString, "x\u{FFFD}")
            let stillUnchanged = try await client.call(module: .film, operation: "rules.get")
            XCTAssertEqual(stillUnchanged, bytes)
            _ = try await service.save(utf16Rules)
            let utf16Saved = try await service.process(.init(section: .movie, text: "🎬"))
            XCTAssertEqual(utf16Saved, utf16Draft)
            XCTAssertEqual(try JSONDecoder().decode(FilmRuleResult.self, from: JSONEncoder().encode(utf16Saved)), utf16Saved)
            let unicode = try await service.process(.init(section: .movie, text: "🎬"),
                draft: .init(movieRules: [.init(pattern: "^.", replace: "x", flags: "u")]))
            XCTAssertEqual(unicode.outputText.utf16, [0x78])
            let chinese = try await service.process(.init(section: .movie, text: "中文"), draft: utf16Rules)
            XCTAssertEqual(chinese.outputText.utf16, [0x78, 0x6587])
            let savedBeforeCleaning = try await client.call(module: .film, operation: "rules.get")
            let search = try await service.process(.init(section: .search, text: "\u{20000}"),
                draft: .init(aggregateRegexRules: ["^."]))
            XCTAssertEqual(search.type, "success")
            XCTAssertEqual(search.processedText.utf16, [0xDC00])
            XCTAssertEqual(search.groupKey.utf16, [0xDC00])
            XCTAssertEqual(search.previews.map(\.utf16), [[0xDC00]])
            XCTAssertEqual(search.outputText.utf16, [0xDC00])
            let episode = try await service.process(.init(section: .episode, text: "\u{20000}"),
                draft: .init(episodeCleanRegexRules: [".$"], episodeRules: [.init(pattern: ".")]))
            XCTAssertEqual(episode.type, "success")
            XCTAssertEqual(episode.processedText.utf16, [0xD840])
            XCTAssertEqual(episode.previews.map(\.utf16), [[0xD840]])
            XCTAssertNil(episode.rewrittenText)
            XCTAssertEqual(episode.outputText.utf16, [0xD840])
            let savedAfterCleaning = try await client.call(module: .film, operation: "rules.get")
            XCTAssertEqual(savedAfterCleaning, savedBeforeCleaning)
            await service.close()
        } catch { await service.close(); throw error }
    }

    /// Fixed dashboard/server expectations, now through the real App RPC path.
    func testNodeMobileEntryMatchesEverySharedReferenceCaseTwice() async throws {
        let resources = try await hostResources(), client = try await resources.scriptSession.connection()
        let service = FilmRuleService(connection: { client })
        let original = try await client.call(module: .film, operation: "rules.get").filmRequiredObject().filmValue("rules")
        addTeardownBlock {
            _ = try await client.call(module: .film, operation: "rules.set", parameters: .object(["rules": original]))
            await service.close()
        }
        try await service.clearData()
        let initial = try await service.savedSnapshot()
        for section in FilmRuleSection.allCases { _ = try await service.restoreDefaults(section) }
        let restored = try await service.savedSnapshot()
        let before = try await client.call(module: .film, operation: "rules.get")
        let fixtures = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(FilmRuleNodeMobileReferenceCases.json.utf8)) as? [[String: Any]])
        XCTAssertFalse(fixtures.isEmpty)
        for fixture in fixtures {
            let name = try XCTUnwrap(fixture["name"] as? String)
            let setup = try XCTUnwrap(fixture["setup"] as? String)
            let base: FilmRuleSet
            switch setup {
            case "initial": base = initial
            case "restored": base = restored
            case "empty": base = FilmRuleSet()
            default: XCTFail("Unknown fixture setup: \(setup)"); continue
            }
            var object = try XCTUnwrap(JSONSerialization.jsonObject(with: JSONEncoder().encode(base)) as? [String: Any])
            if let edits = fixture["rules"] as? [String: Any] {
                for (key, value) in edits {
                    if key == "episodeRules" || key == "movieRules" {
                        let rows = try XCTUnwrap(value as? [[String: Any]])
                        object[key] = rows.map { row -> [String: Any] in
                            var complete: [String: Any] = ["replace": "", "flags": ""]
                            complete.merge(row) { _, new in new }
                            return complete
                        }
                    } else { object[key] = value }
                }
            }
            let rules = try JSONDecoder().decode(FilmRuleSet.self, from: JSONSerialization.data(withJSONObject: object))
            let request = try JSONDecoder().decode(FilmRuleRequest.self, from: JSONSerialization.data(withJSONObject: XCTUnwrap(fixture["request"])))
            let expected = try XCTUnwrap(fixture["expected"] as? [String: Any])
            for _ in 0..<2 {
                let result = try await service.process(request, draft: rules)
                var actual = try XCTUnwrap(JSONSerialization.jsonObject(with: JSONEncoder().encode(result)) as? [String: Any])
                if result.matchedRuleIndex == nil { actual["matchedRuleIndex"] = NSNull() }
                if result.rewrittenText == nil { actual["rewrittenText"] = NSNull() }
                for (key, value) in expected {
                    let observed = try XCTUnwrap(actual[key], "\(name): \(key)")
                    let expectedBytes = try JSONSerialization.data(withJSONObject: value, options: [.fragmentsAllowed, .sortedKeys])
                    let actualBytes = try JSONSerialization.data(withJSONObject: observed, options: [.fragmentsAllowed, .sortedKeys])
                    XCTAssertEqual(actualBytes, expectedBytes, "\(name): \(key)")
                }
            }
        }
        let request = FilmRuleRequest(section: .search, text: "作品2", query: "作品2")
        let saved = try await service.process(request), draft = try await service.process(request, draft: restored)
        XCTAssertEqual(saved, draft)
        let after = try await client.call(module: .film, operation: "rules.get")
        XCTAssertEqual(after, before)
        print("EVIDENCE film-rules shared fixtures=\(fixtures.count); repeated=2; carrier=NodeMobile; entry=FilmRuleService.process")
    }

    func testNodeMobileUnicodeAssertionsCapturesAndStatefulRulesRepeatExactly() async throws {
        let resources = try await hostResources(), client = try await resources.scriptSession.connection()
        let service = FilmRuleService(connection: { client })
        addTeardownBlock { await service.close() }
        let cases: [(String, String, String, String, String)] = [
            (#"(\p{Script=Han}+)"#, "$1!", "gu", "猫🎬第12集", "猫!🎬第!12集!"),
            ("k", "x", "iu", "K", "x"),
            ("(?<=a)(b)(?=c)", "[$1]", "g", "abc", "a[b]c"),
            ("(?<first>a)(b)", "$2$<first>", "", "ab", "ba"),
            ("a", "x", "g", "aa", "xx"),
            ("a", "x", "y", "aa", "xa")
        ]
        for (pattern, replace, flags, input, output) in cases {
            for _ in 0..<2 {
                let result = try await service.process(.init(section: .movie, text: input),
                    draft: .init(movieRules: [.init(pattern: pattern, replace: replace, flags: flags)]))
                XCTAssertEqual(result.type, "success", pattern)
                XCTAssertEqual(result.outputText, FilmRuleText(output), pattern)
                XCTAssertTrue(result.errors.isEmpty, pattern)
            }
        }
    }

    func testNodeMobileRuleCancellationDeadlineCompilationAndGlobalDeletionDrainThenRecover() async throws {
        let resources = try await hostResources(), client = try await resources.scriptSession.connection()
        let service = FilmRuleService(connection: { client })
        addTeardownBlock { await service.close() }
        let cases: [(FilmRuleSection, FilmRuleSet, String)] = [
            (.movie, .init(movieRules: [.init(pattern: "^(a+)+$", replace: "x")]), String(repeating: "a", count: 64) + "!"),
            (.movie, .init(movieRules: [.init(pattern: "(?<=" + String(repeating: "a", count: 1_048_576) + ")b", replace: "x", flags: "i")]), "b"),
            (.search, .init(aggregateRegexRules: ["/(?=a{4096})a/ig"]), String(repeating: "a", count: 1_048_576))
        ]
        for (section, rules, input) in cases {
            for cancel in [true, false] {
                var started = DispatchTime.now().uptimeNanoseconds
                let operation = Task { try await service.process(.init(section: section, text: input), draft: rules) }
                if cancel {
                    // Schedules consumer cancellation; native-entry/uncatchable
                    // interruption is independently verified by native-json tests.
                    try await Task.sleep(nanoseconds: 100_000_000)
                    started = DispatchTime.now().uptimeNanoseconds
                    operation.cancel()
                }
                do { _ = try await operation.value; XCTFail("Native work cannot return a caught syntax error") }
                catch {
                    if cancel { XCTAssertTrue(error is CancellationError, "\(error)") }
                    else { XCTAssertEqual((error as? FilmPlaybackError)?.code, "TIMEOUT") }
                }
                XCTAssertLessThan(Double(DispatchTime.now().uptimeNanoseconds - started) / 1_000_000_000, 5)
                let match = try await service.process(.init(section: .movie, text: "ab"),
                    draft: .init(movieRules: [.init(pattern: "(?<=a)b", replace: "x", flags: "i")]))
                XCTAssertEqual(match.outputText, FilmRuleText("ax")); XCTAssertTrue(match.errors.isEmpty)
                let deletion = try await service.process(.init(section: .search, text: "aaaaab"),
                    draft: .init(aggregateRegexRules: ["/(?=a{4})a/ig"]))
                XCTAssertEqual(deletion.outputText, FilmRuleText("aaab")); XCTAssertTrue(deletion.errors.isEmpty)
                let invalid = try await service.process(.init(section: .movie, text: "b"),
                    draft: .init(movieRules: [.init(pattern: "[")]))
                XCTAssertFalse(invalid.errors.isEmpty)
            }
        }
    }

    func testCatUsesRealNodeMobileRoutesAndClientOwnedPersistence() async throws {
        let resources = try await hostResources()
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let store = try DocumentStore(baseURL: directory, namespace: FilmSourceService.documentNamespace)
        let transport = CatScriptTransport(connectionProvider: { try await resources.scriptSession.connection() })
        let service = FilmSourceService(transport: transport, storeFactory: { store })
        let id = "ios-host-" + UUID().uuidString.lowercased()
        let key = FilmSourceKey(installationId: id, siteKey: "fixture")
        do {
            let installed = try await service.importLocal(name: "真实 Cat", source: filmCatScript("v1"), installationId: id)
            XCTAssertTrue(installed.enabled)
            XCTAssertTrue(installed.canDetect)
            XCTAssertEqual(installed.sourceType, "local")
            let result = try await service.detect(key)
            XCTAssertEqual(result.result, "valid", result.error ?? "")
            XCTAssertEqual(result.steps.map(\.operation), ["home", "category", "detail", "play", "search"])
            XCTAssertTrue(result.playbackResolved)
            XCTAssertFalse(result.mediaPlaybackVerified)
            let options = FilmVideoOptions(enabled: false, homeVisible: false, searchEnabled: false, aggregateImages: true)
            try await service.saveVideoOptions(key, options: options, order: -7)
            let rebuilt = FilmSourceService(transport: transport, storeFactory: { store })
            try await rebuilt.initialize()
            let persisted = await rebuilt.snapshot
            XCTAssertEqual(persisted.videos.first { $0.key == key }?.options, options)
            XCTAssertEqual(persisted.videos.first { $0.key == key }?.detection, result)
            await rebuilt.close() // Borrower close must not close the App's host.
            try await service.deleteVideo(key)
            let restarted = try await service.restartCat(id)
            XCTAssertEqual(restarted.modifiedAt, installed.modifiedAt)
            _ = try await service.updateCat(id, source: filmCatScript("v2"))
            try await service.refresh()
            let deleted = await service.snapshot
            XCTAssertFalse(deleted.videos.contains { $0.key == key })
            _ = try await service.importLocal(name: "真实 Cat", source: filmCatScript("v2"), installationId: id)
            let restored = await service.snapshot
            XCTAssertTrue(restored.videos.contains { $0.key == key })
            _ = try await service.enableCat(id, enabled: false)
            do { _ = try await service.detect(key); XCTFail("Disabled detection") }
            catch { XCTAssertEqual((error as? FilmSourceError)?.code, "DISABLED") }
            do { _ = try await service.restartCat(id); XCTFail("Disabled restart") }
            catch { XCTAssertEqual((error as? FilmSourceError)?.code, "DISABLED") }
            try await service.deleteCat(id)
            await service.close()
            let connection = try await resources.scriptSession.connection()
            XCTAssertFalse(connection.isClosed)
        } catch {
            let snapshot = await service.snapshot
            if snapshot.cats.contains(where: { $0.installationId == id }) {
                do { try await service.deleteCat(id) } catch { XCTFail("Cat cleanup: \(error)") }
            }
            await service.close()
            throw error
        }
    }

    func testRealCatPreservesCanonicallyEquivalentSiteIdentities() async throws {
        let resources = try await hostResources()
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let store = try DocumentStore(baseURL: directory, namespace: FilmSourceService.documentNamespace)
        let transport = CatScriptTransport(connectionProvider: { try await resources.scriptSession.connection() })
        let service = FilmSourceService(transport: transport, storeFactory: { store })
        let id = "ios-identity-" + UUID().uuidString.lowercased()
        let composed = FilmSourceKey(installationId: id, siteKey: "\u{00E9}")
        let decomposed = FilmSourceKey(installationId: id, siteKey: "e\u{0301}")
        let source = #"""
        const http = require('http');
        let server;
        module.exports.start = () => new Promise(resolve => {
          server = http.createServer((request, response) => {
            response.setHeader('content-type', 'application/json; charset=utf-8');
            response.end(JSON.stringify({ video: { sites: [
              { key: '\u00e9', name: 'Composed', api: '/identity/one' },
              { key: 'e\u0301', name: 'Decomposed', api: '/identity/two' }
            ] } }));
          });
          server.listen(Number(process.env.PORT), '127.0.0.1', resolve);
        });
        module.exports.stop = () => { server?.closeAllConnections(); server?.close(); };
        """#
        do {
            let cat = try await service.importLocal(name: "Identity fixture", source: source, installationId: id)
            XCTAssertEqual(cat.sites.count, 2, "Real Cat Map accepts both raw keys")
            var snapshot = await service.snapshot
            XCTAssertEqual(Set(snapshot.videos.filter { $0.key.installationId == id }.map(\.key)), Set([composed, decomposed]))
            let options = FilmVideoOptions(enabled: false, homeVisible: false, searchEnabled: false, aggregateImages: true)
            try await service.saveVideoOptions(decomposed, options: options)
            try await service.deleteVideo(composed)
            _ = try await service.restartCat(id)
            snapshot = await service.snapshot
            let remaining = snapshot.videos.filter { $0.key.installationId == id }
            XCTAssertEqual(remaining.map(\.key), [decomposed])
            XCTAssertEqual(remaining.first?.options, options)
            XCTAssertEqual(remaining.first?.api, "/identity/two")
            let rebuilt = FilmSourceService(transport: transport, storeFactory: { store })
            try await rebuilt.initialize()
            let persisted = await rebuilt.snapshot
            let persistedSites = persisted.videos.filter { $0.key.installationId == id }
            XCTAssertEqual(persistedSites.map(\.key), [decomposed])
            XCTAssertEqual(persistedSites.first?.key.siteKey.utf8.map { $0 }, [0x65, 0xCC, 0x81])
            await rebuilt.close()
            _ = try await service.importLocal(name: "Identity fixture", source: source, installationId: id)
            snapshot = await service.snapshot
            XCTAssertEqual(Set(snapshot.videos.filter { $0.key.installationId == id }.map(\.key)), Set([composed, decomposed]))
            XCTAssertEqual(snapshot.videos.first { $0.key == decomposed }?.options, options)
            try await service.deleteCat(id)
            await service.close()
        } catch {
            // Clean by the runtime installation ID even if parsing its result
            // failed before the local owner could publish the installation.
            _ = try? await transport.call("uninstall", ["installationId": .string(id)])
            await service.close()
            throw error
        }
    }

    func testRealCatGenerationChangeBeforeDetectionCommitRejectsOldResult() async throws {
        let resources = try await hostResources()
        let connection = try await resources.scriptSession.connection()
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let store = try DocumentStore(baseURL: directory, namespace: FilmSourceService.documentNamespace)
        let paused = expectation(description: "Real complete search response received")
        let gate = RealCatResultGate(connection: connection, paused: { paused.fulfill() })
        let transport = CatScriptTransport(request: { operation, parameters in
            try await gate.call(operation, parameters)
        }, upload: { id, revision, bytes in
            try await connection.upload(module: .cat, installationID: id,
                sourceRevision: revision, source: bytes)
        })
        let raw = CatScriptTransport(connectionProvider: { connection })
        let service = FilmSourceService(transport: transport, storeFactory: { store })
        let id = "ios-fence-" + UUID().uuidString.lowercased()
        let key = FilmSourceKey(installationId: id, siteKey: "fixture")
        var detection: Task<FilmSourceDetection, Error>?
        do {
            let installed = try await service.importLocal(name: "Detection fence fixture",
                source: filmCatScript("fence", largeSearchResult: true), installationId: id)
            let options = FilmVideoOptions(enabled: false, homeVisible: false, searchEnabled: false, aggregateImages: true)
            try await service.saveVideoOptions(key, options: options, order: -11)
            let bytes = try store.read(key: FilmSourceService.documentKey)
            let task = Task { try await service.detect(key) }
            detection = task
            await fulfillment(of: [paused], timeout: 15)
            // Deliberately bypass the owner. This is a real host generation
            // change, not an OOM experiment or an owner.refresh cancellation.
            _ = try await raw.call("restart", ["installationId": .string(id)])
            let status = try await raw.call("status")
            let instance = try XCTUnwrap(status.catField("instances")?.catArray?.first {
                $0.catField("installationId") == .string(id)
            })
            let generation = try XCTUnwrap(instance.catField("generation")?.catInteger)
            XCTAssertGreaterThan(Int64(generation), installed.generation)
            let cached = await service.snapshot
            XCTAssertEqual(cached.cats.first { $0.installationId == id }?.generation, installed.generation)
            await gate.release()
            do { _ = try await task.value; XCTFail("Real old-generation result must not become a saved detection") }
            catch { XCTAssertTrue(error is CancellationError, "\(error)") }
            let after = await service.snapshot
            XCTAssertEqual(after.cats.first { $0.installationId == id }?.generation, Int64(generation))
            XCTAssertNil(after.videos.first { $0.key == key }?.detection)
            XCTAssertEqual(after.videos.first { $0.key == key }?.options, options)
            let saved = try XCTUnwrap(store.read(key: FilmSourceService.documentKey))
            // Runtime counters in the source description may change on restart;
            // compare the durable video fields rather than unrelated Cat metadata.
            let document = try JSONDecoder().decode(FilmSourceDocument.self, from: saved)
            let before = try JSONDecoder().decode(FilmSourceDocument.self, from: XCTUnwrap(bytes))
            XCTAssertEqual(document.videos.first { $0.key == key }?.detection,
                before.videos.first { $0.key == key }?.detection)
            XCTAssertEqual(document.videos.first { $0.key == key }?.options, options)
            let receivedBytes = await gate.receivedBytes
            XCTAssertGreaterThan(receivedBytes, 65_536)
            try await service.deleteCat(id)
            await service.close()
        } catch {
            detection?.cancel()
            await gate.release()
            _ = await detection?.result
            _ = try? await raw.call("uninstall", ["installationId": .string(id)])
            await service.close()
            throw error
        }
    }
}

private actor RealCatResultGate {
    let connection: RuntimeHTTPClient
    let paused: @Sendable () -> Void
    private var pending: CheckedContinuation<Void, Never>?
    private(set) var receivedBytes = 0
    private var open = false
    init(connection: RuntimeHTTPClient, paused: @escaping @Sendable () -> Void) {
        self.connection = connection; self.paused = paused
    }
    func release() { open = true; pending?.resume(); pending = nil }

    func call(_ operation: String, _ params: RuntimeValue) async throws -> RuntimeValue {
        let result = try await connection.call(module: .cat, operation: operation, parameters: params)
        if operation == "invoke", params.catField("operation") == .string("search"), !open {
            receivedBytes = try RuntimeJSON.encode(result).count
            await withCheckedContinuation { pending = $0; paused() }
        }
        return result
    }
}

// Run these classes in two separate xcodebuild invocations. The second checks the
// actual App startup, not owner reconstruction within the same NodeMobile process.
@MainActor
final class MeowfilmCatColdStartPrepareTests: XCTestCase {
    func testPrepareEnabledAndDisabledInstallationsForNextProcess() async throws {
        guard ProcessInfo.processInfo.environment["MEOWFILM_CAT_COLD_PHASE"] == "prepare" else {
            throw XCTSkip("Run with the documented two-process cold-start procedure")
        }
        let service = try await hostResources().filmSourceSettings
        for id in filmColdIDs {
            let before = await service.snapshot
            if before.cats.contains(where: { $0.installationId == id }) { try await service.deleteCat(id) }
            let cat = try await service.importLocal(name: id, source: filmCatScript(id), installationId: id)
            XCTAssertEqual(cat.sites.first?.name, "Fixture \(id) starts=1 checks=0")
        }
        let key = FilmSourceKey(installationId: filmColdIDs[0], siteKey: "fixture")
        try await service.deleteVideo(key)
        _ = try await service.enableCat(filmColdIDs[1], enabled: false)
        let disabledKey = FilmSourceKey(installationId: filmColdIDs[1], siteKey: "fixture")
        try await service.saveVideoOptions(disabledKey, options: .init(enabled: false, homeVisible: false,
            searchEnabled: false, aggregateImages: true), order: -9)
    }
}

@MainActor
final class MeowfilmCatColdStartRestoreTests: XCTestCase {
    func testAppColdStartRestoresOnlyEnabledScriptsWithoutProbingOrResurrectingVideos() async throws {
        guard ProcessInfo.processInfo.environment["MEOWFILM_CAT_COLD_PHASE"] == "restore" else {
            throw XCTSkip("Requires the preceding prepare process on this test simulator")
        }
        let service = try await hostResources().filmSourceSettings
        addTeardownBlock {
            for id in filmColdIDs { try await service.deleteCat(id) }
        }
        let snapshot = await service.snapshot
        let enabled = try XCTUnwrap(snapshot.cats.first { $0.installationId == filmColdIDs[0] })
        let disabled = try XCTUnwrap(snapshot.cats.first { $0.installationId == filmColdIDs[1] })
        XCTAssertTrue(enabled.canDetect)
        XCTAssertEqual(enabled.sites.first?.name, "Fixture \(filmColdIDs[0]) starts=2 checks=0")
        XCTAssertEqual(disabled.state, "disabled")
        XCTAssertEqual(disabled.sites.first?.name, "Fixture \(filmColdIDs[1]) starts=1 checks=0")
        XCTAssertFalse(snapshot.videos.contains { $0.key.installationId == enabled.installationId })
        let video = try XCTUnwrap(snapshot.videos.first { $0.key.installationId == disabled.installationId })
        XCTAssertEqual(video.order, -9)
        XCTAssertEqual(video.options, .init(enabled: false, homeVisible: false, searchEnabled: false, aggregateImages: true))
        let resumed = try await service.enableCat(disabled.installationId, enabled: true)
        XCTAssertEqual(resumed.sites.first?.name, "Fixture \(filmColdIDs[1]) starts=2 checks=0")
        let result = try await service.detect(video.key)
        XCTAssertEqual(result.result, "valid", result.error ?? "")
    }
}

private let filmColdIDs = ["ios-film-cold-enabled", "ios-film-cold-disabled"]

/// Synthetic Cat HTTP program. Each route validates the actual wire method and
/// complete payload, including Unicode, punctuation and typed POST fields.
private func filmCatScript(_ revision: String, largeSearchResult: Bool = false) -> String {
    // All callers supply fixed test literals, never external JavaScript input.
    """
    const http = require('http'), fs = require('fs');
    const revision = '\(revision)';
    const API = '/spider/film-fixture/3', TYPE = '电影/中文 & 1';
    const VIDEO = '片/中文?键=一&二', FLAG = '线路 甲', EPISODE = 'episode/中文?token=一&part=2';
    const number = path => fs.existsSync(path) ? Number(fs.readFileSync(path, 'utf8')) : 0;
    const starts = number('/starts.txt') + 1;
    fs.writeFileSync('/starts.txt', String(starts));
    const order = ['home', 'category', 'detail', 'play', 'search'];
    let next = 0, server;
    function requireValue(value, message) { if (!value) throw new Error(message); }
    function expect(operation, actual, expected) {
      requireValue(order[next] === operation, 'action order: ' + operation);
      requireValue(JSON.stringify(Object.keys(actual).sort()) === JSON.stringify(Object.keys(expected).sort()), 'fields');
      for (const key of Object.keys(expected))
        requireValue(JSON.stringify(actual[key]) === JSON.stringify(expected[key]), operation + ' argument ' + key);
      next = (next + 1) % order.length;
    }
    const routes = new Map([
      ['GET ' + API + '/home', input => {
        expect('home', input, {});
        fs.writeFileSync('/checks.txt', String(number('/checks.txt') + 1));
        return { data: { types: [{ type_id: TYPE, type_name: '电影' }] } };
      }],
      ['POST ' + API + '/category', input => {
        expect('category', input, { id: TYPE, page: 1, filter: true, filters: {} });
        return { data: { items: [{ vod_id: VIDEO, vod_name: '受控影片' }] } };
      }],
      ['POST ' + API + '/detail', input => {
        expect('detail', input, { id: VIDEO, vod_id: VIDEO });
        return { list: [{ vod_id: VIDEO, vod_play_from: FLAG, vod_play_url: '第一集$' + EPISODE }] };
      }],
      ['POST ' + API + '/play', input => {
        expect('play', input, { flag: FLAG, id: EPISODE, siteApi: API });
        return { url: ['测试清晰度', 'https://media.invalid/film-fixture.m3u8'] };
      }],
      ['GET ' + API + '/search', input => {
        expect('search', input, { wd: '斗破', page: '1' });
        return { list: [{ vod_id: VIDEO }]\(largeSearchResult ? ", padding: 'x'.repeat(70000)" : "") };
      }]
    ]);
    function json(response, value) {
      response.setHeader('content-type', 'application/json; charset=utf-8'); response.end(JSON.stringify(value));
    }
    async function route(request, response) {
      const url = new URL(request.url, 'http://127.0.0.1');
      if (url.pathname === '/full-config' || url.pathname === '/config') {
        requireValue(request.method === 'GET', 'config method');
        return json(response, { video: { sites: [{ key: 'fixture',
          name: 'Fixture ' + revision + ' starts=' + starts + ' checks=' + number('/checks.txt'), api: API }] } });
      }
      if (!url.pathname.startsWith(API + '/')) { response.statusCode = 404; return json(response, {}); }
      const handler = routes.get(request.method + ' ' + url.pathname);
      requireValue(typeof handler === 'function', 'method or route: ' + request.method + ' ' + url.pathname);
      const chunks = []; for await (const chunk of request) chunks.push(Buffer.from(chunk));
      const body = Buffer.concat(chunks).toString('utf8');
      let input;
      if (request.method === 'GET') {
        requireValue(body.length === 0, 'GET body'); input = Object.fromEntries(url.searchParams);
      } else {
        requireValue(url.search === '' && body.length > 0, 'POST JSON without query'); input = JSON.parse(body);
      }
      json(response, handler(input));
    }
    module.exports.start = () => new Promise(resolve => {
      server = http.createServer((request, response) => {
        route(request, response).catch(error => { response.statusCode = 500; json(response, { error: error.message }); });
      });
      server.listen(Number(process.env.PORT), '127.0.0.1', resolve);
    });
    module.exports.stop = () => { server?.closeAllConnections(); server?.close(); };
    """
}
