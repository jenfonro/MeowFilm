import UIKit
import Combine

@main
@MainActor
final class MeowfilmAppDelegate: UIResponder, UIApplicationDelegate {
    let launch = MeowfilmLaunchController()
    private(set) var assembly: MeowfilmAssembly?

    func application(
        _ application: UIApplication,
        didFinishLaunchingWithOptions launchOptions: [UIApplication.LaunchOptionsKey: Any]? = nil
    ) -> Bool {
        launch.start()
        return true
    }

    func readyAssembly() -> MeowfilmAssembly? {
        guard launch.phase == .ready else { return nil }
        if let assembly { return assembly }
        let value = MeowfilmAssembly()
        assembly = value
        value.start()
        return value
    }

    func requestUserDataReset() async throws {
        try await launch.requestReset { [weak self] in self?.assembly?.shutdown() }
        await assembly?.waitForShutdown()
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
            } else {
                self.coordinator = nil
                window.rootViewController?.dismiss(animated: false)
                window.rootViewController = MeowfilmLaunchViewController(phase: phase, retry: { [weak app] in
                    app?.launch.start()
                })
                window.makeKeyAndVisible()
            }
        }
    }

    func sceneDidBecomeActive(_ scene: UIScene) {
        coordinator?.becameActive()
        (window?.rootViewController as? MeowfilmLaunchViewController)?.showStatus()
    }
}
