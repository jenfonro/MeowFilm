#if os(iOS)
import UIKit
import MeowUI
import Combine
import UniformTypeIdentifiers

@MainActor
public final class MusicCredentialImportViewController: MusicPageViewController,
    UIDocumentPickerDelegate
{
    private let owner: MusicViewModel
    private let model: MusicCredentialImportViewModel
    private let platform = UISegmentedControl(items: MusicAccountPlatform.allCases.map(\.title))
    private let format = UIButton(type: .system)
    private let input = UITextField()
    private let chooseFile = UIButton(type: .system)
    private let preview = UIButton(type: .system)
    private let commit = UIButton(type: .system)
    private let status = UILabel()

    public init(
        owner: MusicViewModel,
        service: any MusicCredentialsImporting,
        appearance: MusicAppearancePreferencesStore
    ) {
        self.owner = owner
        model = MusicCredentialImportViewModel(service: service)
        super.init(appearance: appearance)
        title = "导入音乐凭据"
    }

    public override func viewDidLoad() {
        super.viewDidLoad()
        platform.selectedSegmentIndex = MusicAccountPlatform.allCases.firstIndex(of: model.platform) ?? 0
        platform.addAction(UIAction { [weak self] _ in self?.selectPlatform() }, for: .valueChanged)

        format.showsMenuAsPrimaryAction = true
        input.placeholder = "凭据文本"
        input.isSecureTextEntry = true
        input.textContentType = nil
        input.autocorrectionType = .no
        input.autocapitalizationType = .none
        input.borderStyle = .roundedRect
        input.accessibilityIdentifier = "music.credentials.input"
        input.addAction(UIAction { [weak self] _ in self?.render() }, for: .editingChanged)

        chooseFile.setTitle("选择文件", for: .normal)
        chooseFile.addAction(UIAction { [weak self] _ in self?.openFile() }, for: .touchUpInside)
        preview.setTitle("预览", for: .normal)
        preview.addAction(UIAction { [weak self] _ in self?.previewText() }, for: .touchUpInside)
        commit.setTitle("导入", for: .normal)
        commit.addAction(UIAction { [weak self] _ in self?.commitImport() }, for: .touchUpInside)
        status.numberOfLines = 0
        status.textAlignment = .center

        let buttons = UIStackView(arrangedSubviews: [chooseFile, preview, commit])
        buttons.axis = .horizontal
        buttons.distribution = .fillEqually
        buttons.spacing = 8
        let stack = UIStackView(arrangedSubviews: [platform, format, input, buttons, status])
        stack.axis = .vertical
        stack.spacing = 14
        stack.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(stack)
        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: view.leadingAnchor, constant: 20),
            stack.trailingAnchor.constraint(equalTo: view.trailingAnchor, constant: -20),
            stack.topAnchor.constraint(equalTo: view.safeAreaLayoutGuide.topAnchor, constant: 22),
            platform.heightAnchor.constraint(equalToConstant: 36),
            format.heightAnchor.constraint(equalToConstant: 44),
            input.heightAnchor.constraint(equalToConstant: 44),
            buttons.heightAnchor.constraint(equalToConstant: 44),
        ])
        observe(model) { [weak self] in self?.render() }
        render()
    }

    public override func pageDidBecomeActive() {
        model.activate(
            isAvailable: owner.isAvailable && !owner.isClosed,
            accessGeneration: owner.accountAccessGeneration
        )
    }

    public override func pageDidBecomeInactive() {
        input.text = ""
        model.deactivate()
    }

    public override func applyTheme() {
        super.applyTheme()
        guard isViewLoaded else { return }
        status.textColor = UIColor(theme.muted)
        platform.selectedSegmentTintColor = UIColor(theme.surface)
        format.tintColor = UIColor(theme.ink)
        for button in [chooseFile, preview, commit] { button.tintColor = UIColor(theme.accent) }
    }

    public func documentPicker(
        _ controller: UIDocumentPickerViewController,
        didPickDocumentsAt urls: [URL]
    ) {
        input.text = ""
        let access = owner.accountAccessGeneration
        controller.dismiss(animated: true) { [weak self] in
            guard let self, self.isPageActive, self.owner.isAvailable, !self.owner.isClosed,
                  self.owner.accountAccessGeneration == access else { return }
            self.model.activate(isAvailable: true, accessGeneration: access)
            if let url = urls.first, urls.count == 1 { self.model.previewFile(url) }
            else { self.model.fileSelectionFailed() }
        }
    }

    public func documentPickerWasCancelled(_ controller: UIDocumentPickerViewController) {}

    private func selectPlatform() {
        let values = MusicAccountPlatform.allCases
        guard values.indices.contains(platform.selectedSegmentIndex) else { return }
        input.text = ""
        model.selectPlatform(values[platform.selectedSegmentIndex])
    }

    private func render() {
        guard isViewLoaded else { return }
        platform.selectedSegmentIndex = MusicAccountPlatform.allCases.firstIndex(of: model.platform) ?? 0
        format.setTitle(model.format.title, for: .normal)
        format.menu = UIMenu(children: model.formats.map { value in
            UIAction(title: value.title, state: value == model.format ? .on : .off) {
                [weak self] _ in
                self?.input.text = ""
                self?.model.selectFormat(value)
            }
        })
        chooseFile.isEnabled = !model.isBusy
        preview.isEnabled = !model.isBusy && !(input.text ?? "").isEmpty
        commit.isHidden = model.preview == nil
        commit.setTitle(model.preview?.requiresReplacement == true ? "替换会话" : "导入", for: .normal)
        if let completed = model.completed {
            status.text = "\(completed.platform.title) · \(completed.phase.title)"
            navigationItem.rightBarButtonItem = UIBarButtonItem(
                title: "完成",
                style: .done,
                target: self,
                action: #selector(close)
            )
        } else if let value = model.preview {
            status.text = [
                value.platform.title,
                value.deviceState.title,
                value.requiresReplacement ? "已有会话" : nil,
            ].compactMap { $0 }.joined(separator: " · ")
        } else {
            status.text = model.error?.localizedDescription
        }
        if let error = model.error {
            showError(error.localizedDescription)
            model.dismissError()
        }
        applyTheme()
    }

    private func previewText() {
        let value = input.text ?? ""
        input.text = ""
        model.previewText(value)
    }

    private func commitImport() {
        guard let value = model.preview else { return }
        if value.requiresReplacement {
            let alert = UIAlertController(title: "替换已保存的会话？", message: nil, preferredStyle: .alert)
            alert.addAction(UIAlertAction(title: "取消", style: .cancel))
            alert.addAction(UIAlertAction(title: "替换并导入", style: .destructive) { [weak self] _ in
                self?.model.commit(replaceExisting: true)
            })
            present(alert, animated: true)
        } else {
            model.commit(replaceExisting: false)
        }
    }

    private func openFile() {
        model.cancel()
        input.text = ""
        let picker = UIDocumentPickerViewController(
            forOpeningContentTypes: [.json, .plainText, .data],
            asCopy: true
        )
        picker.delegate = self
        present(picker, animated: true)
    }

    @objc private func close() {
        navigationController?.popViewController(animated: true)
    }
}

@MainActor
public final class MusicLibraryImportViewController: MusicPageViewController,
    UIDocumentPickerDelegate
{
    private let model: MusicViewModel
    private let choose = UIButton(type: .system)
    private let ownerField = UITextField()
    private let preview = UIButton(type: .system)
    private let confirm = UIButton(type: .system)
    private let cancel = UIButton(type: .system)
    private let history = UIButton(type: .system)
    private let status = UILabel()

    public init(model: MusicViewModel, appearance: MusicAppearancePreferencesStore) {
        self.model = model
        super.init(appearance: appearance)
        title = "导入曲库"
    }

    public override func viewDidLoad() {
        super.viewDidLoad()
        choose.setTitle("选择曲库文件", for: .normal)
        choose.addAction(UIAction { [weak self] _ in self?.openFile() }, for: .touchUpInside)
        ownerField.placeholder = "原曲库所有者 ID"
        ownerField.autocorrectionType = .no
        ownerField.autocapitalizationType = .none
        ownerField.borderStyle = .roundedRect
        ownerField.addAction(UIAction { [weak self] _ in
            self?.model.importOwner = self?.ownerField.text ?? ""
        }, for: .editingChanged)
        preview.setTitle("预检", for: .normal)
        preview.addAction(UIAction { [weak self] _ in
            guard let self else { return }
            self.model.importOwner = self.ownerField.text ?? ""
            self.runPageTask { await self.model.previewImport() }
        }, for: .touchUpInside)
        confirm.setTitle("确认导入", for: .normal)
        confirm.addAction(UIAction { [weak self] _ in
            guard let self else { return }
            self.runPageTask { await self.model.confirmImport() }
        }, for: .touchUpInside)
        cancel.setTitle("取消", for: .normal)
        cancel.addAction(UIAction { [weak self] _ in
            guard let self else { return }
            self.model.cancelImportPresentation()
        }, for: .touchUpInside)
        history.setTitle("导入记录", for: .normal)
        history.addAction(UIAction { [weak self] _ in self?.openHistory() }, for: .touchUpInside)
        status.numberOfLines = 0
        status.textAlignment = .center
        let buttons = UIStackView(arrangedSubviews: [preview, confirm, cancel])
        buttons.axis = .horizontal
        buttons.distribution = .fillEqually
        let stack = UIStackView(arrangedSubviews: [choose, ownerField, buttons, history, status])
        stack.axis = .vertical
        stack.spacing = 12
        stack.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(stack)
        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: view.leadingAnchor, constant: 20),
            stack.trailingAnchor.constraint(equalTo: view.trailingAnchor, constant: -20),
            stack.topAnchor.constraint(equalTo: view.safeAreaLayoutGuide.topAnchor, constant: 22),
            choose.heightAnchor.constraint(equalToConstant: 44),
            ownerField.heightAnchor.constraint(equalToConstant: 44),
            buttons.heightAnchor.constraint(equalToConstant: 44),
            history.heightAnchor.constraint(equalToConstant: 44),
        ])
        observe(model) { [weak self] in self?.render() }
        render()
    }

    public override func pageDidBecomeActive() { render() }
    public override func pageDidBecomeInactive() {
        if model.showingImport { model.cancelImportPresentation() }
    }

    public override func applyTheme() {
        super.applyTheme()
        guard isViewLoaded else { return }
        status.textColor = UIColor(theme.muted)
        for button in [choose, preview, confirm, cancel, history] {
            button.tintColor = UIColor(theme.accent)
        }
    }

    public func documentPicker(
        _ controller: UIDocumentPickerViewController,
        didPickDocumentsAt urls: [URL]
    ) {
        guard let url = urls.first, urls.count == 1 else {
            model.error = "文件读取失败"
            return
        }
        let access = model.accountAccessGeneration
        controller.dismiss(animated: true) { [weak self] in
            guard let self, self.isPageActive, self.model.accountAccessGeneration == access else { return }
            self.runPageTask { await self.model.importFile(url) }
        }
    }

    public func documentPickerWasCancelled(_ controller: UIDocumentPickerViewController) {}

    private func render() {
        guard isViewLoaded else { return }
        let importing = model.showingImport
        ownerField.isHidden = !importing
        preview.isHidden = !importing || model.importPreview != nil
        confirm.isHidden = model.importPreview == nil
        cancel.isHidden = !importing
        choose.isHidden = importing
        if !ownerField.isFirstResponder { ownerField.text = model.importOwner }
        if let value = model.importPreview {
            status.text = "歌曲 \(value.report.songs) · 歌单 \(value.report.collections) · 成员 \(value.report.members)"
        } else if model.isImporting {
            status.text = "正在导入"
        } else {
            status.text = model.error
        }
        if let error = model.error {
            showError(error, acknowledged: { [weak model] in
                if model?.error == error { model?.error = nil }
            })
        }
        applyTheme()
    }

    private func openFile() {
        let picker = UIDocumentPickerViewController(forOpeningContentTypes: [.json], asCopy: true)
        picker.delegate = self
        present(picker, animated: true)
    }

    private func openHistory() {
        guard let service = model.importManagement else { return }
        navigationController?.pushViewController(
            MusicImportHistoryViewController(
                owner: model,
                service: service,
                appearance: appearance
            ),
            animated: true
        )
    }
}

@MainActor
private final class MusicImportHistoryViewController: MusicPageViewController {
    private let owner: MusicViewModel
    private let model: MusicImportHistoryViewModel
    private let status = UILabel()
    private let rollback = UIButton(type: .system)

    public init(
        owner: MusicViewModel,
        service: any MusicImportHistoryProviding,
        appearance: MusicAppearancePreferencesStore
    ) {
        self.owner = owner
        model = MusicImportHistoryViewModel(service: service) { [weak owner] in
            await owner?.load()
        }
        super.init(appearance: appearance)
        title = "导入记录"
    }

    public override func viewDidLoad() {
        super.viewDidLoad()
        status.numberOfLines = 0
        status.textAlignment = .center
        rollback.setTitle("回滚导入", for: .normal)
        rollback.addAction(UIAction { [weak self] _ in self?.requestRollback() }, for: .touchUpInside)
        let stack = UIStackView(arrangedSubviews: [status, rollback])
        stack.axis = .vertical
        stack.spacing = 16
        stack.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(stack)
        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: view.leadingAnchor, constant: 24),
            stack.trailingAnchor.constraint(equalTo: view.trailingAnchor, constant: -24),
            stack.centerYAnchor.constraint(equalTo: view.centerYAnchor),
            rollback.heightAnchor.constraint(equalToConstant: 44),
        ])
        observe(model) { [weak self] in self?.render() }
    }

    public override func pageDidBecomeActive() {
        model.access(
            available: owner.isAvailable && !owner.isClosed,
            generation: owner.accountAccessGeneration
        )
        model.setForeground(true)
        runPageTask { [weak model] in await model?.load() }
    }

    public override func pageDidBecomeInactive() { model.deactivate() }

    public override func applyTheme() {
        super.applyTheme()
        guard isViewLoaded else { return }
        status.textColor = UIColor(theme.muted)
        rollback.tintColor = UIColor(theme.accent)
    }

    private func render() {
        guard isViewLoaded else { return }
        switch model.snapshot {
        case .empty:
            status.text = model.busy ? "正在读取" : "暂无导入记录"
            rollback.isHidden = true
        case .imported(let batch, let date, let canRollback):
            status.text = "批次 \(batch) · \(date.formatted(date: .abbreviated, time: .shortened))"
            rollback.isHidden = !canRollback
        case .rolledBack(let batch):
            status.text = "批次 \(batch) 已回滚"
            rollback.isHidden = true
        }
        if let error = model.error {
            showError(error, acknowledged: { [weak model] in
                if model?.error == error { model?.error = nil }
            })
        }
        applyTheme()
    }

    private func requestRollback() {
        guard let id = model.requestRollback() else { return }
        let alert = UIAlertController(title: "回滚这次导入？", message: nil, preferredStyle: .alert)
        alert.addAction(UIAlertAction(title: "取消", style: .cancel) { [weak self] _ in
            self?.model.cancelConfirmation()
        })
        alert.addAction(UIAlertAction(title: "回滚", style: .destructive) { [weak self] _ in
            guard let self else { return }
            self.runPageTask { await self.model.confirmRollback(id) }
        })
        present(alert, animated: true)
    }
}

@MainActor
public final class MusicFMSettingsViewController: MusicPageViewController,
    UICollectionViewDataSource, UICollectionViewDelegateFlowLayout
{
    private let model: MusicViewModel
    private let collection = UICollectionView(frame: .zero, collectionViewLayout: UICollectionViewFlowLayout())
    private var work: Task<Void, Never>?
    private var failure: String?
    var onSelected: @MainActor () -> Void

    public init(model: MusicViewModel, appearance: MusicAppearancePreferencesStore,
                onSelected: @escaping @MainActor () -> Void = {}) {
        self.model = model; self.onSelected = onSelected
        super.init(appearance: appearance)
        title = "私人漫游"
    }

    public override func viewDidLoad() {
        super.viewDidLoad()
        collection.backgroundColor = .clear
        collection.dataSource = self; collection.delegate = self
        collection.register(MusicFMChoiceCell.self, forCellWithReuseIdentifier: "FM")
        collection.register(MusicFMHeading.self, forSupplementaryViewOfKind: UICollectionView.elementKindSectionHeader,
                            withReuseIdentifier: "Heading")
        view.addSubview(collection); collection.pinEdges(to: view)
        navigationItem.leftBarButtonItem = UIBarButtonItem(barButtonSystemItem: .close,
                                                          target: self, action: #selector(close))
        observe(model) { [weak self] in self?.collection.reloadData() }
    }
    public override func applyTheme() {
        super.applyTheme()
        if isViewLoaded { collection.reloadData() }
    }
    public func numberOfSections(in collectionView: UICollectionView) -> Int { 2 }
    public func collectionView(_ collectionView: UICollectionView, numberOfItemsInSection section: Int) -> Int {
        section == 0 ? MusicFMModeChoice.main.count : MusicFMModeChoice.scenes.count
    }
    public func collectionView(_ collectionView: UICollectionView, cellForItemAt indexPath: IndexPath)
        -> UICollectionViewCell {
        let choice = indexPath.section == 0 ? MusicFMModeChoice.main[indexPath.item] : MusicFMModeChoice.scenes[indexPath.item]
        let cell = collectionView.dequeueReusableCell(withReuseIdentifier: "FM", for: indexPath) as! MusicFMChoiceCell
        cell.configure(choice, selected: choice.matches(model.activeHomeFM), theme: theme)
        return cell
    }
    public func collectionView(_ collectionView: UICollectionView,
        viewForSupplementaryElementOfKind kind: String, at indexPath: IndexPath) -> UICollectionReusableView {
        let header = collectionView.dequeueReusableSupplementaryView(ofKind: kind, withReuseIdentifier: "Heading",
                                                                     for: indexPath) as! MusicFMHeading
        header.label.text = indexPath.section == 1 ? "场景模式" : nil
        header.label.textColor = UIColor(theme.muted)
        return header
    }
    public func collectionView(_ collectionView: UICollectionView, layout collectionViewLayout: UICollectionViewLayout,
        sizeForItemAt indexPath: IndexPath) -> CGSize {
        CGSize(width: indexPath.section == 0 ? collectionView.bounds.width - 28 :
                max(0, (collectionView.bounds.width - 28 - 12) / 4),
               height: indexPath.section == 0 ? 64 : 84)
    }
    public func collectionView(_ collectionView: UICollectionView, layout collectionViewLayout: UICollectionViewLayout,
        minimumInteritemSpacingForSectionAt section: Int) -> CGFloat { 4 }
    public func collectionView(_ collectionView: UICollectionView, layout collectionViewLayout: UICollectionViewLayout,
        minimumLineSpacingForSectionAt section: Int) -> CGFloat { section == 0 ? 10 : 4 }
    public func collectionView(_ collectionView: UICollectionView, layout collectionViewLayout: UICollectionViewLayout,
        insetForSectionAt section: Int) -> UIEdgeInsets { UIEdgeInsets(top: 12, left: 14, bottom: 20, right: 14) }
    public func collectionView(_ collectionView: UICollectionView, layout collectionViewLayout: UICollectionViewLayout,
        referenceSizeForHeaderInSection section: Int) -> CGSize {
        CGSize(width: collectionView.bounds.width, height: section == 1 ? 32 : 0)
    }
    public func collectionView(_ collectionView: UICollectionView, didSelectItemAt indexPath: IndexPath) {
        let choice = indexPath.section == 0 ? MusicFMModeChoice.main[indexPath.item] : MusicFMModeChoice.scenes[indexPath.item]
        guard work == nil, isPageActive, model.isFMActive,
              !choice.matches(model.activeHomeFM) || failure != nil else { return }
        let access = model.accountAccessGeneration
        work = Task { [weak self] in
            guard let self else { return }
            defer { self.work = nil }
            do {
                try await self.model.playHomeAction(choice.action, title: choice.title, forceReload: true)
                guard !Task.isCancelled, self.isPageActive, self.model.accountAccessGeneration == access else { return }
                self.failure = self.model.error
                if let error = self.failure { self.showError(error) }
                else { self.onSelected() }
            } catch {
                if !Task.isCancelled, self.isPageActive, self.model.accountAccessGeneration == access {
                    self.failure = MusicServiceError.sanitized(error).localizedDescription
                    self.showError(self.failure)
                }
            }
        }
    }
    public override func pageDidBecomeInactive() { work?.cancel() }
    @objc private func close() { dismiss(animated: true) }
    deinit { work?.cancel() }
}

@MainActor
private final class MusicFMChoiceCell: UICollectionViewCell {
    private let icon = UIImageView(), label = UILabel()
    override init(frame: CGRect) {
        super.init(frame: frame)
        label.textAlignment = .center; label.numberOfLines = 2
        icon.contentMode = .scaleAspectFit
        contentView.addSubview(icon); contentView.addSubview(label)
        contentView.layer.cornerRadius = 9
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
    func configure(_ choice: MusicFMModeChoice, selected: Bool, theme: PageTheme) {
        let scene = choice.mode == .scene
        icon.isHidden = !scene
        if scene { icon.image = MusicFMSceneIcon.image(code: choice.submode) }
        icon.tintColor = UIColor(theme.ink)
        label.text = choice.title
        label.font = .systemFont(ofSize: scene ? 12 : 16, weight: scene ? .regular : .medium)
        label.textColor = UIColor(theme.ink)
        contentView.backgroundColor = selected ? UIColor(theme.accent).withAlphaComponent(0.16) :
            scene ? .clear : UIColor(theme.panel)
        accessibilityTraits = selected ? [.button, .selected] : .button
        accessibilityLabel = choice.title
        setNeedsLayout()
    }
    override func layoutSubviews() {
        super.layoutSubviews()
        icon.frame = CGRect(x: bounds.midX - 12.5, y: 10, width: 25, height: 25)
        label.frame = icon.isHidden ? bounds : CGRect(x: 2, y: 43, width: bounds.width - 4, height: 32)
    }
}
@MainActor
private final class MusicFMHeading: UICollectionReusableView {
    let label = UILabel()
    override init(frame: CGRect) {
        super.init(frame: frame)
        label.font = .systemFont(ofSize: 13)
        addSubview(label); label.pinEdges(to: self)
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
}

#endif
