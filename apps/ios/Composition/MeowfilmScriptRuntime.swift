import Foundation
import NodeMobile
import MeowRuntime

/// The App's single embedded Node Mobile runtime. Node starts at most once per
/// process. App startup requests Cat restoration through its shared session;
/// merely starting the host does not execute music scripts.
enum MeowfilmScriptRuntime {
    static func launcher(settings: RuntimeSettingsAccess) -> any RuntimeLaunching {
        MeowfilmRuntimeLauncher(runtime: RuntimeEmbeddedLauncher(
        payload: {
            guard let resources = Bundle.main.resourceURL else { throw RuntimeLaunchError.payloadUnavailable }
            return try RuntimePayload(root: resources.appendingPathComponent("Runtime", isDirectory: true))
        },
        dataDirectory: dataDirectory,
        settings: settings,
        start: { arguments in
            RuntimeEmbeddedLauncher.withCArguments(arguments) { count, argv in node_start(count, argv) }
        }
        ))
    }

    /// App-owned script data (music installations and Cat private volumes), never a script-selected path.
    static var dataDirectory: URL {
        let support = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? URL(fileURLWithPath: NSTemporaryDirectory(), isDirectory: true)
        return support.resolvingSymlinksInPath().appendingPathComponent("meowfilm/script-runtime", isDirectory: true)
    }
}

/// Only the App's nine-module assembly may publish a usable connection.
/// A script startup failure still leaves native music/local film data usable.
private struct MeowfilmRuntimeLauncher: RuntimeLaunching {
    let runtime: RuntimeEmbeddedLauncher
    func connect() async throws -> RuntimeHTTPClient {
        let client = try await runtime.connect()
        do {
            try await client.validateAppModules()
            return client
        } catch {
            await runtime.close()
            throw error
        }
    }
    func close() async { await runtime.close() }
    func waitForExit() async { await runtime.waitForExit() }
}
