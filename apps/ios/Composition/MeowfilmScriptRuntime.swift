import Foundation
import NodeMobile
import MeowDebug
import MeowRuntime

/// The App's single embedded Node Mobile runtime. Node starts at most once per
/// process. App startup requests Cat restoration through its shared session;
/// merely starting the host does not execute music scripts.
enum MeowfilmScriptRuntime {
    static func launcher(dataRoot: URL, settings: RuntimeSettingsAccess,
                         debugHandshake: DebugNodeHandshake? = nil) -> any RuntimeLaunching {
        RuntimeEmbeddedLauncher(
            payload: {
                guard let resources = Bundle.main.resourceURL else { throw RuntimeLaunchError.payloadUnavailable }
                return try RuntimePayload(root: resources.appendingPathComponent("Runtime", isDirectory: true))
            },
            dataDirectory: dataRoot,
            settings: settings,
            debugHandshake: debugHandshake,
            start: { arguments in
                RuntimeEmbeddedLauncher.withCArguments(arguments) { count, argv in node_start(count, argv) }
            }
        )
    }
}
