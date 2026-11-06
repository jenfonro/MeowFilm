import Foundation
import NodeMobile
import MeowRuntime
import MeowMusicNativeService

/// The App's single embedded Node Mobile runtime. Node starts at most once per
/// process, at App startup; its payload is a signed resource.
enum MeowfilmScriptRuntime {
    static let launcher = RuntimeEmbeddedLauncher(
        payload: {
            guard let resources = Bundle.main.resourceURL else { throw RuntimeLaunchError.payloadUnavailable }
            return try RuntimePayload(root: resources.appendingPathComponent("Runtime", isDirectory: true))
        },
        dataDirectory: dataDirectory,
        start: { arguments in
            RuntimeEmbeddedLauncher.withCArguments(arguments) { count, argv in node_start(count, argv) }
        }
    )

    static let musicScripts = NativeMusicScriptService(launcher: launcher)

    /// App-owned script data (music installations and Cat private volumes), never a script-selected path.
    private static var dataDirectory: URL {
        let support = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? URL(fileURLWithPath: NSTemporaryDirectory(), isDirectory: true)
        return support.appendingPathComponent("meowfilm/script-runtime", isDirectory: true)
    }
}
