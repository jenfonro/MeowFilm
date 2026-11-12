#if os(iOS)
import UIKit

/// A cancellable native modal shared by address input and import confirmation.
@MainActor
public final class ManagementDialog: UIViewController {
    public let body = UIStackView()
    public var submit: (() async throws -> Bool)?
    public var accepted: (() -> Void)?
    private let heading: String, action: String
    private let error = UILabel(), accept = UIButton(type: .system), cancel = UIButton(type: .system)
    private var task: Task<Void, Never>?
    private var cancelling = false
    public init(title: String, action: String = "确定", themeProvider: any PageThemeProviding) {
        heading = title; self.action = action
        super.init(nibName: nil, bundle: nil); modalPresentationStyle = .overFullScreen; modalTransitionStyle = .crossDissolve
    }
    @available(*, unavailable) required init?(coder: NSCoder) { fatalError() }
    public override func viewDidLoad() {
        super.viewDidLoad(); view.backgroundColor = .clear
        let blur = UIVisualEffectView(effect: UIBlurEffect(style: .regular)); blur.contentView.backgroundColor = UIColor.black.withAlphaComponent(0.18)
        view.addSubview(blur); blur.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([blur.leadingAnchor.constraint(equalTo: view.leadingAnchor), blur.trailingAnchor.constraint(equalTo: view.trailingAnchor), blur.topAnchor.constraint(equalTo: view.topAnchor), blur.bottomAnchor.constraint(equalTo: view.bottomAnchor)])
        let panel = UIView(); panel.backgroundColor = ManagementAppearance.surface; panel.layer.cornerRadius = 20; panel.clipsToBounds = true
        let title = UILabel(); title.text = heading; title.font = .systemFont(ofSize: 17, weight: .semibold); title.textColor = .label
        let header = UIView(); header.addSubview(title); title.translatesAutoresizingMaskIntoConstraints = false
        let line = UIView(); line.backgroundColor = ManagementAppearance.line; header.addSubview(line); line.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([header.heightAnchor.constraint(equalToConstant: 52), title.leadingAnchor.constraint(equalTo: header.leadingAnchor, constant: 18), title.trailingAnchor.constraint(equalTo: header.trailingAnchor, constant: -18), title.centerYAnchor.constraint(equalTo: header.centerYAnchor), line.leadingAnchor.constraint(equalTo: header.leadingAnchor), line.trailingAnchor.constraint(equalTo: header.trailingAnchor), line.bottomAnchor.constraint(equalTo: header.bottomAnchor), line.heightAnchor.constraint(equalToConstant: 0.5)])
        error.textColor = .systemRed; error.font = .systemFont(ofSize: 13); error.numberOfLines = 0; error.isHidden = true
        body.axis = .vertical; body.spacing = 12
        let scroll = UIScrollView(); scroll.addSubview(body); body.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([body.leadingAnchor.constraint(equalTo: scroll.contentLayoutGuide.leadingAnchor), body.trailingAnchor.constraint(equalTo: scroll.contentLayoutGuide.trailingAnchor), body.topAnchor.constraint(equalTo: scroll.contentLayoutGuide.topAnchor), body.bottomAnchor.constraint(equalTo: scroll.contentLayoutGuide.bottomAnchor), body.widthAnchor.constraint(equalTo: scroll.frameLayoutGuide.widthAnchor)])
        accept.setTitle(action, for: .normal); accept.accessibilityIdentifier = "management.dialog.confirm"
        accept.titleLabel?.font = .systemFont(ofSize: 14); accept.setTitleColor(.white, for: .normal); accept.backgroundColor = .systemBlue
        accept.addAction(UIAction { [weak self] _ in self?.commit() }, for: .touchUpInside)
        cancel.setTitle("取消", for: .normal); cancel.accessibilityIdentifier = "management.dialog.cancel"; cancel.titleLabel?.font = .systemFont(ofSize: 14); cancel.setTitleColor(.label, for: .normal); cancel.backgroundColor = ManagementAppearance.input
        cancel.addAction(UIAction { [weak self] _ in self?.close() }, for: .touchUpInside)
        for button in [cancel, accept] { button.layer.cornerRadius = 12; button.widthAnchor.constraint(greaterThanOrEqualToConstant: 66).isActive = true; button.heightAnchor.constraint(equalToConstant: 36).isActive = true; button.contentEdgeInsets = .init(top: 0, left: 14, bottom: 0, right: 14) }
        let buttons = UIStackView(arrangedSubviews: [UIView(), cancel, accept]); buttons.spacing = 10; buttons.alignment = .center
        let contents = UIStackView(arrangedSubviews: [scroll, error, buttons]); contents.axis = .vertical; contents.spacing = 14; contents.isLayoutMarginsRelativeArrangement = true; contents.layoutMargins = .init(top: 20, left: 20, bottom: 20, right: 20)
        let stack = UIStackView(arrangedSubviews: [header, contents]); stack.axis = .vertical
        view.addSubview(panel); panel.addSubview(stack)
        for item in [panel, stack] { item.translatesAutoresizingMaskIntoConstraints = false }
        let area = UILayoutGuide(); view.addLayoutGuide(area)
        let maxWidth = panel.widthAnchor.constraint(equalToConstant: 606); maxWidth.priority = .defaultHigh
        let contentHeight = scroll.heightAnchor.constraint(equalTo: body.heightAnchor); contentHeight.priority = .init(999)
        NSLayoutConstraint.activate([area.leadingAnchor.constraint(equalTo: view.leadingAnchor), area.trailingAnchor.constraint(equalTo: view.trailingAnchor), area.topAnchor.constraint(equalTo: view.safeAreaLayoutGuide.topAnchor), area.bottomAnchor.constraint(equalTo: view.keyboardLayoutGuide.topAnchor),
            panel.centerXAnchor.constraint(equalTo: view.centerXAnchor), panel.centerYAnchor.constraint(equalTo: area.centerYAnchor), maxWidth,
            panel.widthAnchor.constraint(lessThanOrEqualTo: view.safeAreaLayoutGuide.widthAnchor, constant: -20), panel.topAnchor.constraint(greaterThanOrEqualTo: view.safeAreaLayoutGuide.topAnchor, constant: 8), panel.bottomAnchor.constraint(lessThanOrEqualTo: view.keyboardLayoutGuide.topAnchor, constant: -8),
            stack.leadingAnchor.constraint(equalTo: panel.leadingAnchor), stack.trailingAnchor.constraint(equalTo: panel.trailingAnchor), stack.topAnchor.constraint(equalTo: panel.topAnchor), stack.bottomAnchor.constraint(equalTo: panel.bottomAnchor), contentHeight,
            scroll.heightAnchor.constraint(greaterThanOrEqualToConstant: 40), scroll.heightAnchor.constraint(lessThanOrEqualTo: area.heightAnchor, constant: -156)])
    }
    public static func input(placeholder: String) -> UITextField {
        let input = UITextField(); input.placeholder = placeholder; input.font = .systemFont(ofSize: 14); input.backgroundColor = ManagementAppearance.input; input.layer.cornerRadius = 12
        input.leftView = UIView(frame: .init(x: 0, y: 0, width: 12, height: 1)); input.leftViewMode = .always
        input.rightView = UIView(frame: .init(x: 0, y: 0, width: 12, height: 1)); input.rightViewMode = .always
        input.heightAnchor.constraint(equalToConstant: 40).isActive = true; return input
    }
    public override func viewWillDisappear(_ animated: Bool) { super.viewWillDisappear(animated); task?.cancel() }
    private func commit() {
        guard task == nil else { return }; view.endEditing(true); accept.isEnabled = false; error.isHidden = true
        task = Task { [weak self] in
            guard let self else { return }
            do {
                let success = try await submit?() ?? true
                try Task.checkCancellation()
                if success { let completion = accepted; dismiss(animated: true, completion: completion) }
            } catch is CancellationError { }
            catch { if !cancelling { self.error.text = error.localizedDescription; self.error.isHidden = false } }
            task = nil; accept.isEnabled = true
            if cancelling { dismiss(animated: true) }
        }
    }
    private func close() {
        guard let task else { dismiss(animated: true); return }
        cancelling = true; cancel.isEnabled = false; task.cancel()
    }
}

@MainActor
public final class ManagementImportView: UIView, UITableViewDataSource, UITableViewDelegate {
    public struct Item {
        public let title: String, detail: String, status: String
        public init(title: String, detail: String, status: String) { self.title = title; self.detail = detail; self.status = status }
    }
    public private(set) var selected: Set<Int>
    private let items: [Item], table = UITableView(frame: .zero, style: .plain), all = UIButton(type: .custom), count = UILabel()
    public init(items: [Item], selected: Set<Int>? = nil) {
        self.items = items; self.selected = selected ?? Set(items.indices); super.init(frame: .zero)
        table.dataSource = self; table.delegate = self; table.rowHeight = 58; table.backgroundColor = .clear; table.separatorColor = ManagementAppearance.line; table.separatorInset = .zero
        let tableHeight = table.heightAnchor.constraint(equalToConstant: CGFloat(min(12, max(1, items.count))) * 58); tableHeight.priority = .defaultHigh; tableHeight.isActive = true
        table.heightAnchor.constraint(greaterThanOrEqualToConstant: 58).isActive = true
        all.contentHorizontalAlignment = .leading; all.setTitle("   全选", for: .normal); all.setTitleColor(.label, for: .normal); all.titleLabel?.font = .systemFont(ofSize: 14, weight: .medium); all.setPreferredSymbolConfiguration(.init(pointSize: 20), forImageIn: .normal)
        all.addAction(UIAction { [weak self] _ in guard let self else { return }; self.selected = self.selected.count == items.count ? [] : Set(items.indices); render() }, for: .touchUpInside)
        count.font = .systemFont(ofSize: 13); count.textColor = .secondaryLabel; count.setContentHuggingPriority(.required, for: .horizontal)
        let header = UIStackView(arrangedSubviews: [all, count]); header.alignment = .center; header.heightAnchor.constraint(equalToConstant: 36).isActive = true
        let line = UIView(); line.heightAnchor.constraint(equalToConstant: 0.5).isActive = true; line.backgroundColor = ManagementAppearance.line
        let stack = UIStackView(arrangedSubviews: [header, line, table]); stack.axis = .vertical; stack.spacing = 6; addSubview(stack); stack.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([stack.leadingAnchor.constraint(equalTo: leadingAnchor), stack.trailingAnchor.constraint(equalTo: trailingAnchor), stack.topAnchor.constraint(equalTo: topAnchor), stack.bottomAnchor.constraint(equalTo: bottomAnchor)])
        render()
    }
    @available(*, unavailable) required init?(coder: NSCoder) { fatalError() }
    private func render() {
        let checked = selected.count == items.count && !items.isEmpty
        all.setImage(UIImage(systemName: checked ? "checkmark.circle.fill" : "circle")?.withTintColor(checked ? .systemBlue : .systemGray3, renderingMode: .alwaysOriginal), for: .normal)
        count.text = "\(selected.count)/\(items.count)"; table.reloadData()
    }
    private func toggle(_ index: Int) { if selected.contains(index) { selected.remove(index) } else { selected.insert(index) }; render() }
    public func tableView(_ tableView: UITableView, didSelectRowAt indexPath: IndexPath) { toggle(indexPath.row) }
    public func tableView(_ tableView: UITableView, numberOfRowsInSection section: Int) -> Int { items.count }
    public func tableView(_ tableView: UITableView, cellForRowAt indexPath: IndexPath) -> UITableViewCell {
        let item = items[indexPath.row], cell = UITableViewCell(style: .default, reuseIdentifier: nil)
        cell.backgroundColor = .clear; cell.selectionStyle = .none
        let check = UIButton(type: .custom); check.setImage(UIImage(systemName: selected.contains(indexPath.row) ? "checkmark.circle.fill" : "circle", withConfiguration: UIImage.SymbolConfiguration(pointSize: 20))?.withTintColor(selected.contains(indexPath.row) ? .systemBlue : .systemGray3, renderingMode: .alwaysOriginal), for: .normal)
        check.accessibilityLabel = "选择 " + item.title; check.widthAnchor.constraint(equalToConstant: 26).isActive = true; check.heightAnchor.constraint(equalToConstant: 44).isActive = true
        check.addAction(UIAction { [weak self] _ in self?.toggle(indexPath.row) }, for: .touchUpInside)
        let title = UILabel(); title.text = item.title; title.font = .systemFont(ofSize: 14, weight: .medium); title.textColor = .label
        let detail = UILabel(); detail.text = item.detail; detail.font = .systemFont(ofSize: 12); detail.textColor = .secondaryLabel; detail.isHidden = item.detail.isEmpty
        let labels = UIStackView(arrangedSubviews: [title, detail]); labels.axis = .vertical; labels.spacing = 4; labels.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        let status = UILabel(); status.text = item.status; status.font = .systemFont(ofSize: 12); status.textColor = item.status == "已存在" || item.status == "已有" ? .systemOrange : .systemBlue
        status.setContentHuggingPriority(.required, for: .horizontal); status.setContentCompressionResistancePriority(.required, for: .horizontal)
        let badge = UIView(); badge.backgroundColor = status.textColor.withAlphaComponent(0.1); badge.layer.cornerRadius = 10; badge.addSubview(status); status.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([status.leadingAnchor.constraint(equalTo: badge.leadingAnchor, constant: 8), status.trailingAnchor.constraint(equalTo: badge.trailingAnchor, constant: -8), status.topAnchor.constraint(equalTo: badge.topAnchor, constant: 3), status.bottomAnchor.constraint(equalTo: badge.bottomAnchor, constant: -3)])
        let row = UIStackView(arrangedSubviews: [check, labels, badge]); row.alignment = .center; row.spacing = 10; cell.contentView.addSubview(row); row.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([row.leadingAnchor.constraint(equalTo: cell.contentView.leadingAnchor), row.trailingAnchor.constraint(equalTo: cell.contentView.trailingAnchor), row.topAnchor.constraint(equalTo: cell.contentView.topAnchor), row.bottomAnchor.constraint(equalTo: cell.contentView.bottomAnchor)])
        return cell
    }
}
#endif
