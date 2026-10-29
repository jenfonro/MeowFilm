import UIKit

@main
@MainActor
final class MeowfilmAppDelegate: UIResponder, UIApplicationDelegate {
    let assembly = MeowfilmAssembly()

    func application(
        _ application: UIApplication,
        didFinishLaunchingWithOptions launchOptions: [UIApplication.LaunchOptionsKey: Any]? = nil
    ) -> Bool {
        assembly.start()
        return true
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

    func scene(
        _ scene: UIScene,
        willConnectTo session: UISceneSession,
        options connectionOptions: UIScene.ConnectionOptions
    ) {
        guard let windowScene = scene as? UIWindowScene,
              let app = UIApplication.shared.delegate as? MeowfilmAppDelegate else { return }
        let window = UIWindow(windowScene: windowScene)
        let coordinator = MeowfilmCoordinator(window: window, assembly: app.assembly)
        self.window = window
        self.coordinator = coordinator
        coordinator.start()
    }

    func sceneDidBecomeActive(_ scene: UIScene) {
        coordinator?.becameActive()
    }
}
