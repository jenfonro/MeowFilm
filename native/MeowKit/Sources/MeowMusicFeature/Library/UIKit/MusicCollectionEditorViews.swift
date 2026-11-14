#if os(iOS)
import UIKit
import MeowUI

@MainActor
final class MusicCollectionEditorSelectionMarkView: UIView {
    static let side: CGFloat = 23

    private let check = UIImageView(image: UIImage(
        systemName: "checkmark",
        withConfiguration: UIImage.SymbolConfiguration(pointSize: 15, weight: .bold)
    ))

    override init(frame: CGRect) {
        super.init(frame: frame)
        isUserInteractionEnabled = false
        layer.cornerRadius = Self.side / 2
        layer.borderWidth = 1
        check.contentMode = .center
        check.isHidden = true
        addSubview(check)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func configure(selected: Bool, theme: PageTheme) {
        backgroundColor = selected ? UIColor(theme.accent) : .clear
        layer.borderColor = UIColor(selected ? theme.accent : theme.muted).withAlphaComponent(
            selected ? 1 : 0.35
        ).cgColor
        check.tintColor = UIColor(theme.onAccent)
        check.isHidden = !selected
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        check.frame = bounds
    }
}

@MainActor
final class MusicCollectionEditorToolbarView: UIView {
    let selectAll = UIButton(type: .custom)
    let done = UIButton(type: .system)

    private let selectionMark = MusicCollectionEditorSelectionMarkView()
    private let selectAllLabel = UILabel()

    override init(frame: CGRect) {
        super.init(frame: frame)
        accessibilityIdentifier = "music.collection.editor.toolbar"

        selectAll.accessibilityIdentifier = "music.collection.editor.selectAll"
        selectAll.contentHorizontalAlignment = .left
        selectAll.addSubview(selectionMark)
        selectAll.addSubview(selectAllLabel)
        selectAllLabel.font = .systemFont(ofSize: 15)
        selectAllLabel.isUserInteractionEnabled = false
        selectAll.addAction(UIAction { [weak self] _ in
            self?.onSelectAll?()
        }, for: .touchUpInside)
        addSubview(selectAll)

        done.setTitle("完成", for: .normal)
        done.titleLabel?.font = .systemFont(ofSize: 16)
        done.accessibilityLabel = "完成"
        done.accessibilityIdentifier = "music.collection.editor.done"
        done.addAction(UIAction { [weak self] _ in
            self?.onDone?()
        }, for: .touchUpInside)
        addSubview(done)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    var onSelectAll: (() -> Void)?
    var onDone: (() -> Void)?

    func configure(allSelected: Bool, canSelect: Bool, canSave: Bool, theme: PageTheme) {
        let title = allSelected ? "取消全选" : "全选"
        selectAllLabel.text = title
        selectAll.accessibilityLabel = title
        selectAll.accessibilityTraits = allSelected ? [.button, .selected] : .button
        selectAll.isEnabled = canSelect
        done.isEnabled = canSave
        selectionMark.configure(selected: allSelected, theme: theme)
        applyTheme(theme)
        setNeedsLayout()
    }

    func applyTheme(_ theme: PageTheme) {
        selectAllLabel.textColor = UIColor(theme.ink)
        done.tintColor = UIColor(theme.accent)
        selectAll.alpha = selectAll.isEnabled ? 1 : 0.4
        done.alpha = done.isEnabled ? 1 : 0.4
        selectionMark.configure(selected: selectAll.accessibilityTraits.contains(.selected), theme: theme)
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        let markSide = MusicCollectionEditorSelectionMarkView.side
        let controlHeight: CGFloat = 44
        let controlY: CGFloat = 4
        let labelWidth = selectAllLabel.sizeThatFits(
            CGSize(width: max(0, bounds.width - 120), height: controlHeight)
        ).width
        selectAll.frame = CGRect(x: 20, y: controlY, width: markSide + 10 + labelWidth,
                                 height: controlHeight)
        selectionMark.frame = CGRect(x: 0, y: (controlHeight - markSide) / 2,
                                     width: markSide, height: markSide)
        selectAllLabel.frame = CGRect(x: markSide + 10, y: 0, width: labelWidth,
                                      height: controlHeight)
        done.frame = CGRect(x: max(20, bounds.width - 80), y: controlY, width: 60,
                            height: controlHeight)
    }
}

@MainActor
final class MusicCollectionEditorActionsView: UIView {
    let enqueue = UIButton(type: .system)
    let addToCollection = UIButton(type: .system)
    let deleteSelected = UIButton(type: .system)
    var showsDelete = true {
        didSet { deleteSelected.isHidden = !showsDelete }
    }

    private let border = UIView()
    private let stack = UIStackView()

    override init(frame: CGRect) {
        super.init(frame: frame)
        accessibilityIdentifier = "music.collection.editor.actions"
        configure(enqueue, title: "下一首播放",
                  image: UIImage(
                    systemName: "text.line.first.and.arrowtriangle.forward",
                    withConfiguration: UIImage.SymbolConfiguration(pointSize: 21, weight: .regular)
                  ),
                  identifier: "music.collection.editor.enqueue")
        configure(addToCollection, title: "收藏到歌单",
                  image: UIImage(
                    systemName: "folder",
                    withConfiguration: UIImage.SymbolConfiguration(pointSize: 21, weight: .regular)
                  ),
                  identifier: "music.collection.editor.addToCollection")
        configure(deleteSelected, title: "删除", image: PageIcon.image(.remove, size: 22),
                  identifier: "music.collection.editor.delete")
        for button in [enqueue, addToCollection, deleteSelected] {
            stack.addArrangedSubview(button)
        }
        stack.axis = .horizontal
        stack.distribution = .fillEqually
        stack.translatesAutoresizingMaskIntoConstraints = false
        border.translatesAutoresizingMaskIntoConstraints = false
        addSubview(border)
        addSubview(stack)
        NSLayoutConstraint.activate([
            border.leadingAnchor.constraint(equalTo: leadingAnchor),
            border.trailingAnchor.constraint(equalTo: trailingAnchor),
            border.topAnchor.constraint(equalTo: topAnchor),
            border.heightAnchor.constraint(equalToConstant: 1),
            stack.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 12),
            stack.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -12),
            stack.topAnchor.constraint(equalTo: topAnchor, constant: 8),
            stack.bottomAnchor.constraint(equalTo: bottomAnchor, constant: -8),
        ])
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func applyTheme(_ theme: PageTheme) {
        border.backgroundColor = UIColor(theme.border)
        for button in [enqueue, addToCollection, deleteSelected] {
            button.configuration?.baseForegroundColor = UIColor(theme.ink)
            button.alpha = button.isEnabled ? 1 : 0.4
        }
    }

    private func configure(_ button: UIButton, title: String, image: UIImage?, identifier: String) {
        var configuration = UIButton.Configuration.plain()
        configuration.title = title
        configuration.image = image
        configuration.imagePlacement = .top
        configuration.imagePadding = 5
        configuration.contentInsets = .init(top: 5, leading: 0, bottom: 5, trailing: 0)
        configuration.titleTextAttributesTransformer = UIConfigurationTextAttributesTransformer {
            incoming in
            var outgoing = incoming
            outgoing.font = .systemFont(ofSize: 12)
            return outgoing
        }
        button.configuration = configuration
        button.accessibilityLabel = title
        button.accessibilityIdentifier = identifier
    }
}

@MainActor
final class MusicCollectionEditorTrackCell: UITableViewCell {
    static let reuseIdentifier = "MusicCollectionEditorTrackCell"

    private(set) var representedReference: MusicReference?
    private(set) var isTrackSelected = false
    var onToggle: (() -> Void)?

    private let backgroundContainer = UIView()
    private let selectedChip = UIView()
    private let selectionControl = UIButton(type: .custom)
    private let selectionMark = MusicCollectionEditorSelectionMarkView()
    private let artwork = MusicArtworkView(size: 0)
    private let titleLabel = UILabel()
    private let subtitleLabel = UILabel()
    private var representedTitle = ""

    override init(style: UITableViewCell.CellStyle, reuseIdentifier: String?) {
        super.init(style: style, reuseIdentifier: reuseIdentifier)
        backgroundColor = .clear
        selectionStyle = .none
        accessoryType = .none
        editingAccessoryType = .none
        indentationWidth = 0

        selectedChip.layer.cornerRadius = 10
        backgroundContainer.addSubview(selectedChip)
        backgroundView = backgroundContainer

        selectionControl.addSubview(selectionMark)
        selectionControl.addAction(UIAction { [weak self] _ in
            self?.onToggle?()
        }, for: .touchUpInside)
        contentView.addSubview(selectionControl)

        artwork.layer.cornerRadius = 6
        contentView.addSubview(artwork)

        titleLabel.font = .systemFont(ofSize: 15)
        titleLabel.lineBreakMode = .byTruncatingTail
        subtitleLabel.font = .systemFont(ofSize: 12)
        subtitleLabel.lineBreakMode = .byTruncatingTail
        for label in [titleLabel, subtitleLabel] {
            label.numberOfLines = 1
            label.isAccessibilityElement = false
            contentView.addSubview(label)
        }
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override func prepareForReuse() {
        super.prepareForReuse()
        representedReference = nil
        representedTitle = ""
        onToggle = nil
        artwork.load(nil)
    }

    func configure(
        track: MusicRemoteTrack,
        selected: Bool,
        selectable: Bool,
        showsReorder: Bool,
        theme: PageTheme
    ) {
        representedReference = track.ref
        representedTitle = track.title
        artwork.load(track.artwork)
        titleLabel.text = track.title
        subtitleLabel.text = [track.artist, track.album].filter { !$0.isEmpty }.joined(separator: " · ")
        selectionControl.isEnabled = selectable
        showsReorderControl = showsReorder
        accessibilityIdentifier = "music.collection.editor.row.\(track.ref.id)"
        selectionControl.accessibilityIdentifier = "music.collection.editor.select.\(track.ref.id)"
        selectionMark.accessibilityIdentifier = "music.collection.editor.selectionMark.\(track.ref.id)"
        selectionControl.accessibilityValue = subtitleLabel.text
        setTrackSelected(selected, theme: theme)
        applyTheme(theme)
    }

    func setTrackSelected(_ selected: Bool, theme: PageTheme) {
        isTrackSelected = selected
        selectionControl.accessibilityLabel = selected
            ? "取消选择 \(representedTitle)" : "选择 \(representedTitle)"
        selectionControl.accessibilityTraits = selected ? [.button, .selected] : .button
        selectionMark.configure(selected: selected, theme: theme)
        selectedChip.backgroundColor = selected
            ? UIColor(theme.chip).withAlphaComponent(0.55) : .clear
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        selectedChip.frame = backgroundContainer.bounds.insetBy(dx: 16, dy: 0)

        let markSide = MusicCollectionEditorSelectionMarkView.side
        selectionControl.frame = CGRect(x: 10, y: 14, width: 44, height: 44)
        selectionMark.frame = CGRect(x: (selectionControl.bounds.width - markSide) / 2,
                                     y: (selectionControl.bounds.height - markSide) / 2,
                                     width: markSide, height: markSide)
        artwork.frame = CGRect(x: 56, y: 15, width: 42, height: 42)
        let copyX: CGFloat = 110
        let copyWidth = max(0, contentView.bounds.width - copyX - 8)
        titleLabel.frame = CGRect(x: copyX, y: 16, width: copyWidth, height: 20)
        subtitleLabel.frame = CGRect(x: copyX, y: 40, width: copyWidth, height: 16)
    }

    private func applyTheme(_ theme: PageTheme) {
        titleLabel.textColor = UIColor(theme.ink)
        subtitleLabel.textColor = UIColor(theme.muted)
        artwork.backgroundColor = UIColor(theme.chip)
        tintColor = UIColor(theme.muted)
        selectionControl.alpha = selectionControl.isEnabled ? 1 : 0.4
        setTrackSelected(isTrackSelected, theme: theme)
    }
}

#endif
