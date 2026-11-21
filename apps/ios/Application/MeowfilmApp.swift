import UIKit
import Combine
import Darwin
import MeowDebug

@main
@MainActor
final class MeowfilmAppDelegate: UIResponder, UIApplicationDelegate {
    let launch: MeowfilmLaunchController
    private let debugConfig: DebugLaunchConfiguration?
    private(set) var debugHost: MeowfilmDebugHost?
    private(set) var assembly: MeowfilmAssembly?

    override init() {
        let parsed: DebugLaunchConfiguration?
        do {
            parsed = try DebugLaunchConfiguration.parse()
        } catch {
            fputs("meowfilm: invalid debug argument: \(error.localizedDescription)\n", stderr)
            fflush(stderr)
            Darwin.exit(2)
        }
        self.debugConfig = parsed
        if let dataRootOverride = parsed?.dataRootOverride {
            self.launch = MeowfilmLaunchController(factory: {
                try MeowfilmUserDataReset.isolated(dataRoot: dataRootOverride)
            })
        } else {
            self.launch = MeowfilmLaunchController()
        }
        super.init()
    }

    func application(
        _ application: UIApplication,
        didFinishLaunchingWithOptions launchOptions: [UIApplication.LaunchOptionsKey: Any]? = nil
    ) -> Bool {
        if let debugConfig {
            do {
                let host = try MeowfilmDebugHost(configuration: debugConfig, appDelegate: self)
                try host.start()
                self.debugHost = host
                DebugRuntime.log(.lifecycle, sub: "app", event: "did_finish_launching", data: .object([
                    "bind": .string(debugConfig.bindAddress),
                    "port": .int(Int(debugConfig.port)),
                    "dataRootOverride": .optional(debugConfig.dataRootOverride?.path)
                ]))
            } catch {
                fputs("meowfilm: failed to start debug mode: \(error.localizedDescription)\n", stderr)
                fflush(stderr)
                Darwin.exit(2)
            }
        }
        launch.start()
        return true
    }

    func applicationWillTerminate(_ application: UIApplication) {
        debugHost?.shutdown(code: 0, clean: true)
    }

    func readyAssembly() -> MeowfilmAssembly? {
        guard launch.phase == .ready else { return nil }
        if let assembly { return assembly }
        let value = MeowfilmAssembly(dataRootOverride: debugConfig?.dataRootOverride)
        assembly = value
        value.start()
        return value
    }

    func requestUserDataReset() async throws {
        try await launch.requestReset { [weak self] in self?.assembly?.shutdown() }
    }

    func application(
        _ application: UIApplication,
        configurationForConnecting connectingSceneSession: UISceneSession,
        options: UIScene.ConnectionOptions
    ) -> UISceneConfiguration {
        let configuration = UISceneConfiguration(
            name: "Meowfilm",
            sessionRole: connectingSceneSession.role
        )
        configuration.delegateClass = MeowfilmSceneDelegate.self
        return configuration
    }
}

@MainActor
final class MeowfilmSceneDelegate: UIResponder, UIWindowSceneDelegate {
    var window: UIWindow?
    private var coordinator: MeowfilmCoordinator?
    private var launchObservation: AnyCancellable?

    func scene(
        _ scene: UIScene,
        willConnectTo session: UISceneSession,
        options connectionOptions: UIScene.ConnectionOptions
    ) {
        guard let windowScene = scene as? UIWindowScene,
              let app = UIApplication.shared.delegate as? MeowfilmAppDelegate else { return }
        let window = UIWindow(windowScene: windowScene)
        self.window = window
        launchObservation = app.launch.$phase.receive(on: RunLoop.main).sink { [weak self, weak app] phase in
            guard let self, let app else { return }
            if phase == .ready, let assembly = app.readyAssembly() {
                guard self.coordinator == nil else { return }
                let coordinator = MeowfilmCoordinator(window: window, assembly: assembly,
                    resetUserData: { [weak app] in
                        guard let app else { throw MeowfilmUserDataReset.Failure.request }
                        try await app.requestUserDataReset()
                    })
                self.coordinator = coordinator
                coordinator.start()
                app.debugHost?.attach(window: window, coordinator: coordinator)
            } else {
                self.coordinator = nil
                window.rootViewController?.dismiss(animated: false)
                window.rootViewController = MeowfilmLaunchViewController(phase: phase, retry: { [weak app] in
                    app?.launch.start()
                })
                window.makeKeyAndVisible()
                app.debugHost?.attach(window: window, coordinator: nil)
            }
        }
    }

    func sceneDidBecomeActive(_ scene: UIScene) {
        coordinator?.becameActive()
        (window?.rootViewController as? MeowfilmLaunchViewController)?.showStatus()
        DebugRuntime.log(.lifecycle, sub: "scene", event: "did_become_active")
    }

    func sceneDidEnterBackground(_ scene: UIScene) {
        DebugRuntime.log(.lifecycle, sub: "scene", event: "did_enter_background")
        DebugRuntime.current?.logger.flush()
    }
}
