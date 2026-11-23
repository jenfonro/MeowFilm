// SPDX-License-Identifier: GPL-3.0-only
#if canImport(UIKit)
import UIKit
import MeowUI

@MainActor
final class MusicChannelTabsView: UIView, UIScrollViewDelegate {
    private static let edgeFadeWidth: CGFloat = 18
    private static let trailingInset: CGFloat = 14
    private let scroll = UIScrollView()
    private let edgeMask = CAGradientLayer()
    private var channels: [MusicPublicBrowseChannel] = []
    private var buttons: [String: MusicChannelTabButton] = [:]
    private var selectedID: String?
    private var onSelect: ((MusicPublicBrowseChannel) -> Void)?
    private var pendingCenter = false
    private var pendingCenterAnimated = false
    private var laidOutWidth: CGFloat = 0
    private var lastEdgeFade: (size: CGSize, left: Bool, right: Bool)?

    override init(frame: CGRect) {
        super.init(frame: frame)
        scroll.showsHorizontalScrollIndicator = false
        scroll.alwaysBounceHorizontal = true
        scroll.contentInsetAdjustmentBehavior = .never
        scroll.contentInset = UIEdgeInsets(top: 0, left: 0, bottom: 0, right: Self.trailingInset)
        scroll.delegate = self
        addSubview(scroll)
        edgeMask.startPoint = CGPoint(x: 0, y: 0.5)
        edgeMask.endPoint = CGPoint(x: 1, y: 0.5)
        layer.mask = edgeMask
        accessibilityIdentifier = "music.channel.switcher"
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func configure(channels: [MusicPublicBrowseChannel], selectedID: String?, theme: PageTheme,
                   onSelect: @escaping (MusicPublicBrowseChannel) -> Void) {
        let rebuild = channels != self.channels
        let selectionChanged = selectedID != self.selectedID
        self.channels = channels
        self.selectedID = selectedID
        self.onSelect = onSelect
        if rebuild {
            for button in buttons.values { button.removeFromSuperview() }
            buttons = [:]
            for channel in channels {
                let button = MusicChannelTabButton(channel: channel)
                button.addAction(UIAction { [weak self] _ in
                    guard let self, let current = self.channels.first(where: {
                        $0.identity == channel.identity
                    }) else { return }
                    self.onSelect?(current)
                }, for: .touchUpInside)
                buttons[channel.identity] = button
                scroll.addSubview(button)
            }
        }
        for channel in channels {
            buttons[channel.identity]?.configure(
                selected: channel.identity == selectedID,
                theme: theme
            )
        }
        if rebuild || selectionChanged {
            pendingCenter = true
            pendingCenterAnimated = !rebuild && window != nil
        }
        setNeedsLayout()
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        scroll.frame = bounds
        var x: CGFloat = 0
        for channel in channels {
            guard let button = buttons[channel.identity] else { continue }
            let width = button.preferredWidth
            button.frame = CGRect(x: x, y: 0, width: width, height: bounds.height)
            x += width
        }
        scroll.contentSize = CGSize(width: x, height: bounds.height)
        if laidOutWidth != bounds.width {
            laidOutWidth = bounds.width
            pendingCenter = true
            pendingCenterAnimated = false
        }
        guard pendingCenter, let selectedID, let selected = buttons[selectedID] else {
            updateEdgeFade()
            return
        }
        pendingCenter = false
        let maximum = max(0, scroll.contentSize.width + scroll.contentInset.right - scroll.bounds.width)
        let target = min(maximum, max(0, selected.frame.midX - scroll.bounds.width / 2))
        scroll.setContentOffset(CGPoint(x: target, y: 0), animated: pendingCenterAnimated)
        pendingCenterAnimated = false
        updateEdgeFade()
    }

    func scrollViewDidScroll(_ scrollView: UIScrollView) {
        updateEdgeFade()
    }

    private func updateEdgeFade() {
        let width = bounds.width
        guard width > 0 else { return }
        let canScrollLeft = scroll.contentOffset.x > 1
        let canScrollRight = scroll.contentOffset.x + scroll.bounds.width <
            scroll.contentSize.width + scroll.contentInset.right - 1
        if let lastEdgeFade,
           lastEdgeFade.size == bounds.size,
           lastEdgeFade.left == canScrollLeft,
           lastEdgeFade.right == canScrollRight {
            return
        }
        lastEdgeFade = (bounds.size, canScrollLeft, canScrollRight)
        let fadeFraction = min(0.25, Double(Self.edgeFadeWidth / width))
        let clear = UIColor.clear.cgColor
        let opaque = UIColor.black.cgColor
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        edgeMask.frame = bounds
        edgeMask.colors = [
            canScrollLeft ? clear : opaque,
            opaque,
            opaque,
            canScrollRight ? clear : opaque,
        ]
        edgeMask.locations = [
            0,
            NSNumber(value: fadeFraction),
            NSNumber(value: 1 - fadeFraction),
            1,
        ]
        CATransaction.commit()
    }
}

@MainActor
private final class MusicChannelTabButton: UIButton {
    let channel: MusicPublicBrowseChannel
    let preferredWidth: CGFloat
    private let underline = UIView()

    init(channel: MusicPublicBrowseChannel) {
        self.channel = channel
        let font = UIFont.systemFont(ofSize: 17)
        self.preferredWidth = max(52, ceil((channel.title as NSString).size(withAttributes: [.font: font]).width) + 24)
        super.init(frame: .zero)
        setTitle(channel.title, for: .normal)
        titleLabel?.font = font
        underline.layer.cornerRadius = 1.5
        addSubview(underline)
        accessibilityIdentifier = "music.channel.tab.\(channel.identity)"
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func configure(selected: Bool, theme: PageTheme) {
        titleLabel?.font = .systemFont(ofSize: 17)
        setTitleColor(UIColor(selected ? theme.ink : theme.muted), for: .normal)
        underline.backgroundColor = UIColor(theme.ink)
        underline.isHidden = !selected
        accessibilityTraits = selected ? [.button, .selected] : .button
        setNeedsLayout()
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        underline.frame = CGRect(x: 8, y: max(0, bounds.height - 4),
                                 width: max(0, bounds.width - 16), height: 3)
    }
}
#endif
