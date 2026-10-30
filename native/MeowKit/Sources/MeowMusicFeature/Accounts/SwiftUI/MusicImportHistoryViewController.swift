#if os(iOS)
import UIKit
import MeowUI

@MainActor
final class MusicImportHistoryViewController: HostingPageViewController<MusicImportHistoryContent> {
    private let owner: MusicViewModel
    private let model: MusicImportHistoryViewModel
    private var loadGeneration: UUID?
    private var rendered: (MusicImportHistoryState, PageTheme)?
    private weak var confirmationAlert: UIAlertController?

    init(
        owner: MusicViewModel,
        service: any MusicImportHistoryProviding,
        appearance: MusicAppearancePreferencesStore
    ) {
        self.owner = owner
        model = MusicImportHistoryViewModel(service: service) { [weak owner] in
            await owner?.load()
        }
        super.init(
            themeProvider: appearance,
            content: MusicImportHistoryContent(theme: PageTheme(palette: appearance.pageColorway))
        )
        title = "导入记录"
    }

    public override func viewDidLoad() {
        super.viewDidLoad()
        observe(model) { [weak self] in self?.render() }
        observe(owner.accessChanges) { [weak self] in self?.syncAccess() }
        render()
    }

    public override func pageDidBecomeActive() {
        model.setForeground(true)
        syncAccess()
    }

    public override func pageDidBecomeVisible() { render() }

    public override func pageDidFinishAppearing() {
        if model.error != nil { render() }
    }

    public override func pageDidBecomeInactive() {
        confirmationAlert = nil
        model.close()
    }

    public override func applyTheme() {
        super.applyTheme()
        render()
    }

    private func syncAccess() {
        guard isPageActive else { return }
        let available = owner.isAvailable && !owner.isClosed
        let generation = owner.accountAccessGeneration
        if !available || loadGeneration != generation {
            confirmationAlert?.dismiss(animated: true)
            confirmationAlert = nil
        }
        model.access(available: available, generation: generation)
        if !available {
            loadGeneration = nil
        } else if loadGeneration != generation {
            loadGeneration = generation
            runPageTask { [weak owner, weak model] in
                guard owner?.accountAccessGeneration == generation else { return }
                await model?.load()
            }
        }
        render()
    }

    private func render() {
        guard isViewLoaded else { return }
        let currentAccess = loadGeneration == owner.accountAccessGeneration && owner.isAvailable && !owner.isClosed
        let state = MusicImportHistoryState(
            snapshot: currentAccess ? model.snapshot : .empty,
            available: currentAccess,
            busy: currentAccess && model.busy
        )
        if rendered?.0 != state || rendered?.1 != theme {
            rendered = (state, theme)
            setContent(MusicImportHistoryContent(
                state: state,
                theme: theme,
                rollback: { [weak self] in self?.requestRollback() }
            ))
        }
        if currentAccess, let error = model.error {
            let access = owner.accountAccessGeneration
            showError(error, acknowledged: { [weak self] in
                guard let self, self.owner.accountAccessGeneration == access,
                      self.model.error == error else { return }
                self.model.error = nil
            })
        }
    }

    private func requestRollback() {
        guard isPageActive, loadGeneration == owner.accountAccessGeneration,
              owner.isAvailable, !owner.isClosed, let id = model.requestRollback() else { return }
        let access = owner.accountAccessGeneration
        let alert = UIAlertController(title: "回滚这次导入？", message: nil, preferredStyle: .alert)
        alert.addAction(UIAlertAction(title: "取消", style: .cancel) { [weak model] _ in
            model?.cancelConfirmation()
        })
        alert.addAction(UIAlertAction(title: "回滚", style: .destructive) { [weak self] _ in
            self?.runPageTask { [weak owner = self?.owner, weak model = self?.model] in
                guard owner?.accountAccessGeneration == access else { return }
                await model?.confirmRollback(id)
            }
        })
        confirmationAlert = alert
        present(alert, animated: true)
    }
}
#endif
