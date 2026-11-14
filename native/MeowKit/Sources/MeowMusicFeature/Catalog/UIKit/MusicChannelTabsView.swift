// SPDX-License-Identifier: GPL-3.0-only
#if canImport(UIKit)
import UIKit
import MeowUI

@MainActor
final class MusicChannelTabsView: UIView {
    private let scroll = UIScrollView()
    private var channels: [MusicPublicBrowseChannel] = []
    private var buttons: [String: MusicChannelTabButton] = [:]
    private var selectedID: String?
    private var onSelect: ((MusicPublicBrowseChannel) -> Void)?
    private var pendingCenter = false
    private var pendingCenterAnimated = false
    private var laidOutWidth: CGFloat = 0

    override init(frame: CGRect) {
        super.init(frame: frame)
        scroll.showsHorizontalScrollIndicator = false
        scroll.alwaysBounceHorizontal = true
        scroll.contentInsetAdjustmentBehavior = .never
        addSubview(scroll)
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
        guard pendingCenter, let selectedID, let selected = buttons[selectedID] else { return }
        pendingCenter = false
        let maximum = max(0, scroll.contentSize.width - scroll.bounds.width)
        let target = min(maximum, max(0, selected.frame.midX - scroll.bounds.width / 2))
        scroll.setContentOffset(CGPoint(x: target, y: 0), animated: pendingCenterAnimated)
        pendingCenterAnimated = false
    }
}

@MainActor
private final class MusicChannelTabButton: UIButton {
    let channel: MusicPublicBrowseChannel
    private let underline = UIView()

    init(channel: MusicPublicBrowseChannel) {
        self.channel = channel
        super.init(frame: .zero)
        setTitle(channel.title, for: .normal)
        titleLabel?.font = .systemFont(ofSize: 17)
        underline.layer.cornerRadius = 1.5
        addSubview(underline)
        accessibilityIdentifier = "music.channel.tab.\(channel.identity)"
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    var preferredWidth: CGFloat {
        let font = UIFont.systemFont(ofSize: 17)
        return max(52, ceil((channel.title as NSString).size(withAttributes: [.font: font]).width) + 24)
    }

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
