// SPDX-License-Identifier: GPL-3.0-only
#if os(iOS)
import UIKit
import MeowUI

@MainActor
final class MusicQueueRowView: UIView, PageThemeable {
    var onFavorite: (() -> Void)?
    var onRemove: (() -> Void)?
    let reorderHandle = UIImageView(image: PageIcon.image(.reorder, size: 18))
    private let mark = UILabel(), playing = UIImageView(image: PageIcon.image(.playing, size: 20))
    private let title = UILabel(), artist = UILabel()
    private let favorite = UIButton(type: .system), remove = UIButton(type: .system)
    private static let heart = PageIcon.image(.heart, size: 22)
    private static let filledHeart = PageIcon.image(.heart, filled: true, size: 22)
    private var restingBackground = UIColor.clear, interactionBackground = UIColor.clear
    private var isPressed = false, isReordering = false
    private var isRowActive = false, isRowFavorite = false

    override init(frame: CGRect) {
        super.init(frame: frame)
        layer.cornerRadius = 12
        mark.font = .monospacedDigitSystemFont(ofSize: 12, weight: .regular)
        mark.textAlignment = .center
        title.font = .systemFont(ofSize: 15)
        artist.font = .systemFont(ofSize: 12)
        title.lineBreakMode = .byTruncatingTail
        artist.lineBreakMode = .byTruncatingTail
        favorite.addAction(UIAction { [weak self] _ in self?.onFavorite?() }, for: .touchUpInside)
        remove.setImage(PageIcon.image(.remove, size: 18), for: .normal)
        remove.addAction(UIAction { [weak self] _ in self?.onRemove?() }, for: .touchUpInside)
        reorderHandle.contentMode = .center
        for item in [mark, playing, title, artist, favorite, remove, reorderHandle] { addSubview(item) }
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func configure(title: String, artist: String, index: Int, active: Bool,
                   favorite: Bool?, removable: Bool, reorderable: Bool, theme: PageTheme) {
        self.title.text = title
        self.artist.text = artist
        isRowActive = active
        isRowFavorite = favorite == true
        mark.text = String(format: "%02d", index + 1)
        mark.isHidden = active
        playing.isHidden = !active
        self.favorite.isHidden = favorite == nil
        self.favorite.setImage(isRowFavorite ? Self.filledHeart : Self.heart, for: .normal)
        self.favorite.accessibilityLabel = (isRowFavorite ? "取消收藏 " : "收藏 ") + title
        remove.isHidden = !removable
        remove.accessibilityLabel = "移出队列 " + title
        reorderHandle.isHidden = !reorderable
        applyTheme(theme)
        setNeedsLayout()
    }

    func applyTheme(_ theme: PageTheme) {
        mark.textColor = UIColor(theme.muted)
        playing.tintColor = UIColor(theme.accent)
        title.textColor = UIColor(isRowActive ? theme.accent : theme.ink)
        artist.textColor = UIColor(theme.muted)
        restingBackground = isRowActive ? UIColor(theme.accent).withAlphaComponent(0.06) : .clear
        interactionBackground = UIColor(theme.chip)
        updateBackground()
        favorite.tintColor = UIColor(isRowFavorite ? theme.accent : theme.muted)
        remove.tintColor = UIColor(theme.muted)
        reorderHandle.tintColor = UIColor(theme.muted).withAlphaComponent(0.6)
    }

    func setPressed(_ pressed: Bool) {
        guard isPressed != pressed else { return }
        isPressed = pressed
        updateBackground()
    }

    func setReordering(_ reordering: Bool) {
        guard isReordering != reordering else { return }
        isReordering = reordering
        updateBackground()
    }

    private func updateBackground() {
        backgroundColor = isPressed || isReordering ? interactionBackground : restingBackground
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        let viewport = window?.bounds.size ?? bounds.size
        let metrics = MusicReferenceQueueMetrics(viewportWidth: Double(viewport.width),
                                                viewportHeight: Double(viewport.height))
        let gap = CGFloat(metrics.selectionGap), controlWidth = CGFloat(metrics.rowControlWidth)
        let height = bounds.height
        var trailing = bounds.width - 4
        for (item, width) in [(reorderHandle as UIView, CGFloat(28)), (remove, controlWidth),
                              (favorite, controlWidth)] where !item.isHidden {
            trailing -= width
            item.frame = CGRect(x: trailing, y: (height - 44) / 2, width: width, height: 44)
        }
        mark.frame = CGRect(x: 4, y: 0, width: 24, height: height)
        playing.frame = CGRect(x: 6, y: (height - 20) / 2, width: 20, height: 20)
        let x = 28 + gap, width = max(0, trailing - x - 4)
        title.frame = CGRect(x: x, y: (height - 41) / 2, width: width, height: 20)
        artist.frame = CGRect(x: x, y: title.frame.maxY + 5, width: width, height: 16)
    }

    func isReorderHandle(at point: CGPoint) -> Bool {
        !reorderHandle.isHidden && reorderHandle.frame.contains(point)
    }
}

@MainActor
final class MusicQueueCell: UICollectionViewCell {
    static let reuseIdentifier = "MusicQueueCell"
    let row = MusicQueueRowView()
    override init(frame: CGRect) {
        super.init(frame: frame)
        isAccessibilityElement = true
        contentView.addSubview(row)
    }
    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
    override func layoutSubviews() {
        super.layoutSubviews()
        row.frame = contentView.bounds
    }
    override func prepareForReuse() {
        super.prepareForReuse()
        row.onFavorite = nil; row.onRemove = nil
        row.setPressed(false); row.setReordering(false)
        accessibilityCustomActions = nil
    }
}

@MainActor
final class MusicQueueSongCell: UITableViewCell {
    static let reuseIdentifier = "MusicQueueSongCell"
    let row = MusicQueueRowView()
    override init(style: UITableViewCell.CellStyle, reuseIdentifier: String?) {
        super.init(style: style, reuseIdentifier: reuseIdentifier)
        isAccessibilityElement = true
        backgroundColor = .clear
        selectionStyle = .none
        contentView.addSubview(row)
    }
    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
    override func layoutSubviews() {
        super.layoutSubviews()
        row.frame = contentView.bounds
    }
    override func prepareForReuse() {
        super.prepareForReuse()
        row.onFavorite = nil; row.onRemove = nil
        row.setPressed(false); row.setReordering(false)
        accessibilityCustomActions = nil
    }
}
#endif
