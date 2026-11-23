#if os(iOS)
import UIKit
import MeowUI

@MainActor
final class MusicBrowseHeader: UICollectionReusableView {
    static let reuseIdentifier = "MusicBrowseHeader"
    private let title = UILabel()
    private let play = UIButton(type: .system), choices = UIButton(type: .system)
    private let channelButton = UIButton(type: .system)
    private let channelArrow = UIImageView(image: UIImage(systemName: "chevron.right",
        withConfiguration: UIImage.SymbolConfiguration(pointSize: 11, weight: .regular)))
    private let tabs = UIStackView()
    private var tabButtons: [UIButton] = []
    private var tabIDs: [String] = []
    private var choiceAction: ((String) -> Void)?
    private let avatars = (0..<3).map { _ in MusicArtworkView(size: 0) }
    private var playAction: (() -> Void)?
    private var channelAction: (() -> Void)?
    private var headingHeight: CGFloat = 0
    private var leadingAvatars = false

    static func height(for section: MusicPublicBrowseSection, showsSectionPlayback: Bool = true) -> CGFloat {
        headingHeight(for: section, showsSectionPlayback: showsSectionPlayback) + (section.tabs.isEmpty ? 0 : 36)
    }

    private static func headingHeight(for section: MusicPublicBrowseSection, showsSectionPlayback: Bool) -> CGFloat {
        let hasPlayback = showsSectionPlayback && section.items.contains {
            if case .track = $0.content { return true }; return false
        }
        return !section.title.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ||
            hasPlayback || !section.filters.isEmpty || section.channel != nil || !section.avatars.isEmpty ? 44 : 0
    }

    override init(frame: CGRect) {
        super.init(frame: frame)
        title.font = .systemFont(ofSize: 16)
        title.numberOfLines = 1
        play.setImage(PageIcon.image(.play), for: .normal)
        play.accessibilityLabel = "播放全部"
        play.addAction(UIAction { [weak self] _ in self?.playAction?() }, for: .touchUpInside)
        choices.setImage(UIImage(systemName: "chevron.down"), for: .normal)
        choices.showsMenuAsPrimaryAction = true
        choices.accessibilityLabel = "选择栏目"
        channelButton.addAction(UIAction { [weak self] _ in self?.channelAction?() }, for: .touchUpInside)
        channelArrow.contentMode = .scaleAspectFit
        tabs.axis = .horizontal; tabs.spacing = 9; tabs.distribution = .fillEqually
        for item in [channelButton, title, channelArrow, play, choices, tabs] + avatars { addSubview(item) }
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override func prepareForReuse() {
        super.prepareForReuse()
        playAction = nil
        channelAction = nil
        choiceAction = nil
        choices.menu = nil
        tabIDs.removeAll()
        cancelPendingAvatarLoads()
    }

    func cancelPendingAvatarLoads() {
        for avatar in avatars { avatar.cancelPendingArtworkLoad() }
    }

    func resumePendingAvatarLoads() {
        for avatar in avatars where !avatar.isHidden { avatar.resumePendingLoadIfNeeded() }
    }

    func configure(section: MusicPublicBrowseSection,
                   theme: PageTheme, images: MusicArtworkImages, showsSectionPlayback: Bool = true, onPlay: @escaping () -> Void,
                   onChannel: @escaping () -> Void, onChoice: @escaping (String) -> Void) {
        headingHeight = Self.headingHeight(for: section, showsSectionPlayback: showsSectionPlayback)
        leadingAvatars = section.layout == .artistUpdates
        title.text = section.title
        title.textColor = UIColor(theme.ink)
        playAction = onPlay
        play.isHidden = !showsSectionPlayback || !section.items.contains {
            if case .track = $0.content { return true }; return false
        }
        channelAction = onChannel
        channelButton.isHidden = section.channel == nil
        channelButton.accessibilityLabel = section.title
        channelButton.accessibilityIdentifier = "music.browse.channel.\(section.id)"
        channelArrow.isHidden = channelButton.isHidden
        channelArrow.tintColor = UIColor(theme.ink)
        title.isAccessibilityElement = channelButton.isHidden
        for (index, avatar) in avatars.enumerated() {
            avatar.isHidden = index >= section.avatars.count
            if !avatar.isHidden { avatar.load(section.avatars[index], images: images); avatar.layer.cornerRadius = 12.5 }
            else { avatar.cancelPendingArtworkLoad() }
        }
        play.tintColor = UIColor(theme.ink); choices.tintColor = UIColor(theme.ink)
        let actions: [UIMenuElement] = section.filters.map { filter in
            UIAction(title: filter.title, state: filter.id == section.selectedFilter ? .on : .off) {
                _ in onChoice(filter.id)
            }
        }
        choices.menu = UIMenu(children: actions)
        choices.isHidden = actions.isEmpty
        choiceAction = onChoice
        tabIDs = section.tabs.map(\.id)
        while tabButtons.count < section.tabs.count {
            let index = tabButtons.count
            let button = UIButton(type: .system)
            button.addAction(UIAction { [weak self] _ in
                guard let self, self.tabIDs.indices.contains(index) else { return }
                self.choiceAction?(self.tabIDs[index])
            }, for: .touchUpInside)
            tabs.addArrangedSubview(button)
            tabButtons.append(button)
        }
        for (index, button) in tabButtons.enumerated() {
            guard section.tabs.indices.contains(index) else {
                button.isHidden = true
                continue
            }
            let tab = section.tabs[index]
            button.isHidden = false
            button.setTitle(tab.title, for: .normal)
            button.tintColor = UIColor(tab.id == section.selectedTab ? theme.accent : theme.ink)
            button.accessibilityTraits = tab.id == section.selectedTab ? [.button, .selected] : .button
        }
        tabs.isHidden = section.tabs.isEmpty
        accessibilityIdentifier = "music.browse.heading.\(section.id)"
        setNeedsLayout()
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        let choiceWidth: CGFloat = choices.isHidden ? 0 : 44
        let playWidth: CGFloat = play.isHidden ? 0 : 44
        let avatarCount = avatars.filter { !$0.isHidden }.count
        let avatarWidth: CGFloat = avatarCount == 0 ? 0 : 25 + CGFloat(avatarCount - 1) * 18 + 8
        let titleArea = max(0, bounds.width - choiceWidth - playWidth - avatarWidth)
        let height = max(0, headingHeight - 6)
        let maximum = max(0, titleArea - (channelArrow.isHidden ? 0 : 18))
        let width = channelArrow.isHidden ? maximum :
            min(maximum, title.sizeThatFits(CGSize(width: maximum, height: height)).width)
        title.frame = CGRect(x: leadingAvatars ? avatarWidth : 0, y: 2, width: width, height: height)
        channelArrow.frame = channelArrow.isHidden ? .zero :
            CGRect(x: title.frame.maxX + 6, y: title.frame.midY - 8, width: 12, height: 16)
        channelButton.frame = CGRect(x: 0, y: 0, width: max(0, bounds.width - choiceWidth - playWidth), height: headingHeight)
        play.frame = CGRect(x: bounds.width - choiceWidth - playWidth, y: 0, width: playWidth, height: headingHeight)
        choices.frame = CGRect(x: bounds.width - choiceWidth, y: 0, width: choiceWidth, height: headingHeight)
        tabs.frame = CGRect(x: 0, y: headingHeight, width: bounds.width,
                            height: max(0, bounds.height - headingHeight))
        for (index, avatar) in avatars.enumerated() {
            avatar.frame = CGRect(x: (leadingAvatars ? 0 : titleArea + 8) + CGFloat(index) * 18,
                                  y: leadingAvatars ? title.frame.midY - 12.5 : 8, width: 25, height: 25)
        }
    }
}
#endif
