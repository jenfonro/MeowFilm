#if os(iOS)
import UIKit
import MeowUI

@MainActor
public final class MusicDailyStyleViewController: HostingPageViewController<MusicDailyStyleContent> {
    private let model: MusicDailyStyleViewModel
    private var rendered: (MusicDailyStyleState, PageTheme)?

    public init(
        page: MusicPublicBrowseViewModel,
        source: MusicRemoteQueueSource,
        appearance: MusicAppearancePreferencesStore
    ) {
        model = MusicDailyStyleViewModel(page: page, source: source)
        super.init(
            themeProvider: appearance,
            content: MusicDailyStyleContent(theme: PageTheme(palette: appearance.pageColorway))
        )
        title = "选择推荐风格"
    }

    public override func viewDidLoad() {
        super.viewDidLoad()
        navigationItem.leftBarButtonItem = UIBarButtonItem(
            barButtonSystemItem: .close, target: self, action: #selector(close)
        )
        navigationItem.rightBarButtonItem = UIBarButtonItem(
            title: "完成", style: .done, target: self, action: #selector(save)
        )
        observe(model) { [weak self] in self?.render() }
        render()
    }

    public override func pageDidBecomeActive() { model.loadIfNeeded() }

    public override func pageDidBecomeVisible() { render() }

    public override func pageDidFinishAppearing() {
        if model.error != nil { render() }
    }

    public override func pageDidBecomeInactive() { model.close() }

    public override func applyTheme() {
        super.applyTheme()
        render()
    }

    private func render() {
        guard isViewLoaded else { return }
        let state = MusicDailyStyleState(
            categories: model.available ? model.categories : [],
            category: model.category,
            selected: model.selected,
            available: model.available,
            busy: model.working,
            canRetry: model.canRetry
        )
        if rendered?.0 != state || rendered?.1 != theme {
            rendered = (state, theme)
            setContent(MusicDailyStyleContent(
                state: state,
                theme: theme,
                selectCategory: { [weak model] in model?.selectCategory($0) },
                selectTag: { [weak model] in model?.selectTag($0) },
                retry: { [weak model] in model?.retry() }
            ))
        }
        navigationItem.rightBarButtonItem?.isEnabled = model.canSave
        if let error = model.error {
            showError(error, acknowledged: { [weak model] in model?.dismissError(error) })
        }
        if isPageVisible, model.saved {
            model.acknowledgeSave()
            dismiss(animated: true)
        }
    }

    @objc private func save() { model.save() }
    @objc private func close() { dismiss(animated: true) }
}
#endif
