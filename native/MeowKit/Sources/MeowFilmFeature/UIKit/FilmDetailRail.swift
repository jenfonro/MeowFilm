#if os(iOS)
import UIKit
import MeowUI

/// Detail rails contain a bounded set of metadata already supplied to the page.
@MainActor
class FilmTouchScrollView: UIScrollView {
    override func touchesShouldCancel(in view: UIView) -> Bool { true }
}

@MainActor
final class FilmDetailRail: FilmTouchScrollView {
    private let stack = UIStackView()
    private var widths: [ObjectIdentifier: NSLayoutConstraint] = [:]

    init() {
        super.init(frame: .zero)
        showsHorizontalScrollIndicator = false
        delaysContentTouches = false
        stack.axis = .horizontal
        stack.alignment = .top
        stack.spacing = 12
        addSubview(stack)
        stack.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: contentLayoutGuide.leadingAnchor, constant: 16),
            stack.trailingAnchor.constraint(equalTo: contentLayoutGuide.trailingAnchor, constant: -16),
            stack.topAnchor.constraint(equalTo: contentLayoutGuide.topAnchor),
            stack.bottomAnchor.constraint(equalTo: contentLayoutGuide.bottomAnchor),
            stack.heightAnchor.constraint(equalTo: frameLayoutGuide.heightAnchor),
        ])
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override var intrinsicContentSize: CGSize {
        CGSize(width: UIView.noIntrinsicMetric,
               height: stack.systemLayoutSizeFitting(UIView.layoutFittingCompressedSize).height)
    }

    func append(_ view: UIView, width: CGFloat) {
        view.translatesAutoresizingMaskIntoConstraints = false
        let constraint = view.widthAnchor.constraint(equalToConstant: width)
        constraint.isActive = true
        widths[ObjectIdentifier(view)] = constraint
        stack.addArrangedSubview(view)
        invalidateIntrinsicContentSize()
    }

    func setWidth(_ width: CGFloat, for view: UIView) {
        guard let constraint = widths[ObjectIdentifier(view)], abs(constraint.constant - width) > 0.5 else { return }
        constraint.constant = width
        invalidateIntrinsicContentSize()
    }

    func removeAll() {
        NSLayoutConstraint.deactivate(Array(widths.values))
        for child in stack.arrangedSubviews {
            stack.removeArrangedSubview(child)
            child.removeFromSuperview()
        }
        widths.removeAll()
        invalidateIntrinsicContentSize()
    }

    func setViews(_ views: [UIView], width: CGFloat) {
        if stack.arrangedSubviews.elementsEqual(views, by: { $0 === $1 }) {
            for child in views { setWidth(width, for: child) }
            return
        }
        let wanted = Set(views.map(ObjectIdentifier.init))
        for child in stack.arrangedSubviews where !wanted.contains(ObjectIdentifier(child)) {
            widths.removeValue(forKey: ObjectIdentifier(child))?.isActive = false
            stack.removeArrangedSubview(child)
            child.removeFromSuperview()
        }
        for (index, child) in views.enumerated() {
            if index >= stack.arrangedSubviews.count || stack.arrangedSubviews[index] !== child {
                stack.removeArrangedSubview(child)
                stack.insertArrangedSubview(child, at: index)
            }
            if widths[ObjectIdentifier(child)] == nil {
                child.translatesAutoresizingMaskIntoConstraints = false
                let constraint = child.widthAnchor.constraint(equalToConstant: width)
                constraint.isActive = true
                widths[ObjectIdentifier(child)] = constraint
            } else { setWidth(width, for: child) }
        }
        invalidateIntrinsicContentSize()
    }

    func resetOffset() { setContentOffset(CGPoint(x: -adjustedContentInset.left, y: 0), animated: false) }

}

@MainActor
class FilmPressButton: UIButton {
    private lazy var feedback = PressFeedback(view: self)
    override var isHighlighted: Bool {
        didSet { feedback.setPressed(isHighlighted) }
    }
}

@MainActor
final class FilmEpisodeButton: FilmPressButton {
    var onSelect: (() -> Void)?
    private let artwork: ArtworkView
    private let heading = UILabel(), summary = UILabel()
    private let stack = UIStackView()
    private var listConstraints: [NSLayoutConstraint] = []
    private var displayedEpisode: FilmEpisode?
    private var isList = false

    init(episode: FilmEpisode, images: ArtworkImages, list: Bool = false) {
        artwork = ArtworkView(size: 0, images: images)
        super.init(frame: .zero)
        artwork.layer.cornerRadius = 9
        heading.font = .systemFont(ofSize: 14)
        heading.numberOfLines = 2
        summary.font = .systemFont(ofSize: 12)
        summary.numberOfLines = 3
        let text = UIStackView(arrangedSubviews: [heading, summary])
        text.axis = .vertical; text.spacing = 6
        stack.addArrangedSubview(artwork); stack.addArrangedSubview(text)
        stack.axis = .vertical; stack.alignment = .fill; stack.spacing = 6
        stack.isUserInteractionEnabled = false
        addSubview(stack)
        stack.pinEdges(to: self)
        listConstraints = [
            artwork.widthAnchor.constraint(equalTo: widthAnchor, multiplier: 0.36),
            heightAnchor.constraint(greaterThanOrEqualTo: artwork.heightAnchor),
        ]
        artwork.heightAnchor.constraint(equalTo: artwork.widthAnchor, multiplier: 9.0 / 16.0).isActive = true
        addAction(UIAction { [weak self] _ in self?.onSelect?() }, for: .touchUpInside)
        update(episode: episode, list: list)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func update(episode: FilmEpisode, list: Bool) {
        if isList != list {
            isList = list
            stack.axis = list ? .horizontal : .vertical
            stack.alignment = list ? .center : .fill
            stack.spacing = list ? 12 : 6
            for constraint in listConstraints { constraint.isActive = list }
        }
        guard displayedEpisode != episode else { return }
        if displayedEpisode?.artworkURL != episode.artworkURL { artwork.load(episode.artworkURL) }
        displayedEpisode = episode
        heading.text = episode.number.map {
            episode.title.isEmpty ? "第 \($0) 集" : "\($0). \(episode.title)"
        } ?? episode.title
        heading.textAlignment = episode.title.isEmpty ? .center : .natural
        summary.text = episode.summary
        summary.isHidden = episode.summary.isEmpty
        accessibilityLabel = [heading.text, episode.summary].compactMap { $0 }.filter { !$0.isEmpty }.joined(separator: "，")
        accessibilityIdentifier = "film.episode.\(episode.id)"
    }

    func applyTheme(_ theme: PageTheme) {
        artwork.backgroundColor = UIColor(theme.chip)
        heading.textColor = UIColor(theme.ink)
        summary.textColor = UIColor(theme.muted)
    }
}

@MainActor
final class FilmCastView: UIView {
    private let portrait: ArtworkView
    private let name = UILabel(), role = UILabel()

    init(member: FilmCastMember, images: ArtworkImages) {
        portrait = ArtworkView(size: 0, images: images)
        super.init(frame: .zero)
        portrait.layer.cornerRadius = 10
        portrait.load(member.portraitURL)
        name.text = member.name
        name.font = .systemFont(ofSize: 13)
        name.numberOfLines = 2
        name.textAlignment = .center
        role.text = member.role
        role.font = .systemFont(ofSize: 12)
        role.numberOfLines = 2
        role.textAlignment = .center
        let stack = UIStackView(arrangedSubviews: [portrait, name, role])
        stack.axis = .vertical
        stack.spacing = 6
        addSubview(stack)
        stack.pinEdges(to: self)
        portrait.heightAnchor.constraint(equalTo: portrait.widthAnchor, multiplier: 1.25).isActive = true
        isAccessibilityElement = true
        accessibilityLabel = [member.name, member.role].filter { !$0.isEmpty }.joined(separator: "，")
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func applyTheme(_ theme: PageTheme) {
        portrait.backgroundColor = UIColor(theme.chip)
        name.textColor = UIColor(theme.ink)
        role.textColor = UIColor(theme.muted)
    }
}

@MainActor
final class FilmRelatedButton: FilmPressButton {
    private let artwork: ArtworkView
    private let heading = UILabel()

    init(item: FilmItem, images: ArtworkImages) {
        artwork = ArtworkView(size: 0, images: images)
        super.init(frame: .zero)
        artwork.layer.cornerRadius = 7
        artwork.load(item.posterURL)
        heading.text = item.title
        heading.font = .systemFont(ofSize: 13)
        heading.numberOfLines = 2
        let stack = UIStackView(arrangedSubviews: [artwork, heading])
        stack.axis = .vertical
        stack.spacing = 7
        stack.isUserInteractionEnabled = false
        addSubview(stack)
        stack.pinEdges(to: self)
        artwork.heightAnchor.constraint(equalTo: artwork.widthAnchor, multiplier: 1.5).isActive = true
        accessibilityLabel = item.title
        accessibilityIdentifier = "film.similar.\(item.sourceID).\(item.id)"
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func applyTheme(_ theme: PageTheme) {
        artwork.backgroundColor = UIColor(theme.chip)
        heading.textColor = UIColor(theme.ink)
    }
}
#endif
