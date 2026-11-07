#if os(iOS)
import UIKit
import MeowUI

@MainActor
public final class FilmSettingsViewController: PageViewController {
    private let model: FilmSettingsModel
    private var list: FilmSettingsMenuController?

    public init(model: FilmSettingsModel, themeProvider: any PageThemeProviding) {
        self.model = model
        super.init(themeProvider: themeProvider)
        pageTitle = "影视设置"
    }

    public override func viewDidLoad() {
        super.viewDidLoad()
        let list = FilmSettingsMenuController(themeProvider: themeProvider)
        self.list = list
        list.open = { [weak self] destination in
            guard let self else { return }
            let page: UIViewController
            switch destination {
            case 0: page = FilmRulesViewController(owner: model.rules, themeProvider: themeProvider)
            case 1: page = FilmCatSourcesViewController(service: model.sources, themeProvider: themeProvider)
            default: page = FilmVideoSourcesViewController(service: model.sources, themeProvider: themeProvider)
            }
            navigationController?.pushViewController(page, animated: true)
        }
        addChild(list); view.addSubview(list.view)
        list.view.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            list.view.topAnchor.constraint(equalTo: contentTopAnchor),
            list.view.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            list.view.trailingAnchor.constraint(equalTo: view.trailingAnchor),
            list.view.bottomAnchor.constraint(equalTo: view.bottomAnchor),
        ])
        list.didMove(toParent: self)
        observe(model) { [weak self] in self?.showLoadError() }
    }
    private var displayedLoadError: String?
    public override func pageDidFinishAppearing() { showLoadError() }
    private func showLoadError() {
        guard let error = model.loadError, error != displayedLoadError, canPresentPageAlert else { return }
        displayedLoadError = error
        let alert = UIAlertController(title: "错误", message: error, preferredStyle: .alert)
        alert.addAction(UIAlertAction(title: "取消", style: .cancel))
        alert.addAction(UIAlertAction(title: "重试", style: .default) { [weak self] _ in
            self?.displayedLoadError = nil; self?.model.start()
        })
        present(alert, animated: true)
    }
}

@MainActor
private final class FilmSettingsMenuController: FilmSettingsListController {
    var open: ((Int) -> Void)?
    override var usesPageHeader: Bool { false }
    override func viewDidLoad() {
        super.viewDidLoad()
        sections = [.init(title: "", rows: zip(["魔法规则", "猫源", "视频源"], ["rules", "cats", "videos"])
            .enumerated().map { index, pair in
                .init(title: pair.0, id: "film.settings.\(pair.1)", disclosure: true,
                      action: { [weak self] in self?.open?(index) })
            })]
        render()
    }
}
#endif
