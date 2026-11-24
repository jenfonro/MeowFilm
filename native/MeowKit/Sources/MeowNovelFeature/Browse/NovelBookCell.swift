#if os(iOS)
import UIKit
import MeowDebug
import MeowUI

@MainActor
final class NovelBookCell: UITableViewCell, PageThemeable {
    static let reuseIdentifier = "novel.book"

    private let coverContainer = UIView(), cover = UIImageView(), placeholder = UIImageView(image: UIImage(systemName: "book.closed"))
    private let title = UILabel(), author = UILabel(), intro = UILabel(), chapter = UILabel()
    private let wordCount = NovelBookTagLabel(), source = NovelBookTagLabel()
    private let chapterBackground = UIView(), metadata = UIStackView(), metadataSpace = UIView()
    private var wordWidth: NSLayoutConstraint!
    private nonisolated(unsafe) var pending: Task<Void, Never>?
    private var key = UUID()
    private var representedCoverKey: NovelImageStore.Key?

    override init(style: UITableViewCell.CellStyle, reuseIdentifier: String?) {
        super.init(style: .default, reuseIdentifier: reuseIdentifier)
        backgroundColor = .clear; contentView.backgroundColor = .clear; accessoryType = .none
        selectedBackgroundView = UIView()
        isAccessibilityElement = false; contentView.shouldGroupAccessibilityChildren = true
        coverContainer.clipsToBounds = true; coverContainer.layer.cornerRadius = 8
        cover.contentMode = .scaleAspectFill; cover.clipsToBounds = true
        cover.isAccessibilityElement = true; cover.accessibilityIdentifier = "novel.book.cover"
        cover.accessibilityTraits = .image
        placeholder.contentMode = .scaleAspectFit; placeholder.isAccessibilityElement = false
        for (label, name) in [(title, "title"), (author, "author"), (intro, "intro"),
                              (wordCount, "wordCount"), (source, "source"), (chapter, "chapter")] {
            label.accessibilityIdentifier = "novel.book." + name
            label.adjustsFontForContentSizeCategory = true; label.lineBreakMode = .byTruncatingTail
            label.setContentCompressionResistancePriority(.init(999), for: .vertical)
        }
        title.numberOfLines = 2
        source.lineBreakMode = .byTruncatingMiddle
        chapterBackground.addSubview(chapter)
        for view in [cover, placeholder, chapterBackground] { coverContainer.addSubview(view) }
        for view in [cover, placeholder, chapterBackground, chapter] { view.translatesAutoresizingMaskIntoConstraints = false }
        NSLayoutConstraint.activate([
            coverContainer.widthAnchor.constraint(equalToConstant: 74),
            coverContainer.heightAnchor.constraint(equalToConstant: 104),
            cover.leadingAnchor.constraint(equalTo: coverContainer.leadingAnchor),
            cover.trailingAnchor.constraint(equalTo: coverContainer.trailingAnchor),
            cover.topAnchor.constraint(equalTo: coverContainer.topAnchor),
            cover.bottomAnchor.constraint(equalTo: coverContainer.bottomAnchor),
            placeholder.centerXAnchor.constraint(equalTo: cover.centerXAnchor),
            placeholder.centerYAnchor.constraint(equalTo: cover.centerYAnchor),
            placeholder.widthAnchor.constraint(equalToConstant: 26),
            placeholder.heightAnchor.constraint(equalToConstant: 26),
            chapterBackground.leadingAnchor.constraint(equalTo: cover.leadingAnchor),
            chapterBackground.trailingAnchor.constraint(equalTo: cover.trailingAnchor),
            chapterBackground.bottomAnchor.constraint(equalTo: cover.bottomAnchor),
            chapter.leadingAnchor.constraint(equalTo: chapterBackground.leadingAnchor, constant: 4),
            chapter.trailingAnchor.constraint(equalTo: chapterBackground.trailingAnchor, constant: -4),
            chapter.topAnchor.constraint(equalTo: chapterBackground.topAnchor, constant: 4),
            chapter.bottomAnchor.constraint(equalTo: chapterBackground.bottomAnchor, constant: -4),
        ])
        metadata.spacing = 6
        for view in [wordCount, source, metadataSpace] { metadata.addArrangedSubview(view) }
        wordCount.setContentHuggingPriority(.required, for: .horizontal)
        source.setContentHuggingPriority(.required, for: .horizontal)
        source.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        metadataSpace.setContentHuggingPriority(.init(1), for: .horizontal)
        let details = UIStackView(arrangedSubviews: [title, author, intro, metadata])
        details.axis = .vertical; details.spacing = 4; details.setCustomSpacing(8, after: intro)
        wordWidth = wordCount.widthAnchor.constraint(lessThanOrEqualTo: details.widthAnchor, multiplier: 0.45)
        NSLayoutConstraint.activate([
            wordCount.widthAnchor.constraint(lessThanOrEqualTo: details.widthAnchor),
            source.widthAnchor.constraint(lessThanOrEqualTo: details.widthAnchor),
        ])
        let row = UIStackView(arrangedSubviews: [coverContainer, details])
        row.alignment = .top; row.spacing = 12; row.translatesAutoresizingMaskIntoConstraints = false
        contentView.addSubview(row)
        NSLayoutConstraint.activate([
            row.leadingAnchor.constraint(equalTo: contentView.leadingAnchor, constant: 16),
            row.trailingAnchor.constraint(equalTo: contentView.trailingAnchor, constant: -16),
            row.topAnchor.constraint(equalTo: contentView.topAnchor, constant: 12),
            row.bottomAnchor.constraint(equalTo: contentView.bottomAnchor, constant: -12),
        ])
        registerForTraitChanges([UITraitPreferredContentSizeCategory.self]) { (cell: NovelBookCell, _: UITraitCollection) in
            cell.applyTypography()
        }
        applyTypography()
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override func prepareForReuse() {
        super.prepareForReuse()
        cancelPendingLoad()
        representedCoverKey = nil
        cover.image = nil
        placeholder.isHidden = false
    }

    /// Cancels only an in-flight load without clearing an already-admitted cached image.
    func cancelPendingLoad() {
        key = UUID()
        pending?.cancel()
        pending = nil
    }

    func configure(_ book: NovelBook, sourceCount: Int = 1, isCurrentSource: Bool = false, feature: NovelFeature, theme: PageTheme, loadCover: Bool) {
        title.text = book.name
        let authorName = book.author.trimmingCharacters(in: .whitespacesAndNewlines)
        setText(authorName.isEmpty ? nil : "作者：" + authorName, on: author)
        setText(book.intro, on: intro)
        setText(book.wordCount, on: wordCount)
        let sourceName = feature.sources.first { $0.id == book.installationId }?.name.trimmingCharacters(in: .whitespacesAndNewlines)
        let sourceText = [sourceName, isCurrentSource ? "当前书源" : nil, sourceCount > 1 ? "\(sourceCount) 源" : nil]
            .compactMap { $0 }.filter { !$0.isEmpty }.joined(separator: " · ")
        setText(sourceText, on: source)
        setText(book.latestChapterTitle, on: chapter); chapterBackground.isHidden = chapter.isHidden
        cover.accessibilityLabel = book.name + "封面"
        metadata.isHidden = wordCount.isHidden && source.isHidden
        applyTheme(theme)
        applyTypography()

        guard !book.coverUrl.isEmpty, let source = try? feature.source(for: book) else {
            cancelPendingLoad()
            representedCoverKey = nil
            cover.image = nil
            placeholder.isHidden = false
            return
        }
        let coverKey = NovelImageStore.Key(source: source, bookUrl: book.bookUrl, url: book.coverUrl, kind: "cover")
        if representedCoverKey == coverKey, cover.image != nil || (pending != nil && loadCover) {
            return
        }
        cancelPendingLoad()
        let token = key
        representedCoverKey = coverKey
        if let cached = feature.images.cached(source: source, bookUrl: book.bookUrl, url: book.coverUrl, kind: "cover") {
            cover.image = cached
            placeholder.isHidden = true
            return
        }
        cover.image = nil
        placeholder.isHidden = false
        guard loadCover else { return }
        pending = Task { [weak self] in
            do {
                let image = try await feature.images.read(source: source, book: book, url: book.coverUrl, kind: "cover")
                guard !Task.isCancelled, self?.key == token, self?.representedCoverKey == coverKey,
                      feature.accepts(source) else { return }
                self?.cover.image = image
                self?.placeholder.isHidden = true
            } catch {
                if (error as? NovelError) == .imageReleaseFailed { feature.error = error.localizedDescription }
                if !(error is CancellationError) && !Task.isCancelled {
                    DebugRuntime.logError(.novel, sub: "cover", event: "load.failed", error: error, data: .object([
                        "sourceId": .string(source.id),
                        "coverUrl": .string(book.coverUrl)
                    ]), level: .warn)
                }
            }
        }
    }

    func applyTheme(_ theme: PageTheme) {
        title.textColor = UIColor(theme.ink)
        let secondary = UIColor(theme.muted.mixed(with: theme.ink, ratio: 0.25))
        author.textColor = secondary; intro.textColor = secondary
        wordCount.textColor = secondary; wordCount.backgroundColor = UIColor(theme.chip)
        source.textColor = UIColor(theme.accent.contrastRatio(with: theme.chip) >= 4.5 ? theme.accent : theme.ink)
        source.backgroundColor = UIColor(theme.chip)
        coverContainer.backgroundColor = UIColor(theme.chip); placeholder.tintColor = secondary
        chapter.textColor = .white; chapterBackground.backgroundColor = UIColor.black.withAlphaComponent(0.68)
        selectedBackgroundView?.backgroundColor = UIColor(theme.buttonSelected)
    }

    private func setText(_ text: String?, on label: UILabel) {
        let text = text?.trimmingCharacters(in: .whitespacesAndNewlines)
        label.text = text; label.isHidden = text?.isEmpty != false
    }

    private func applyTypography() {
        let fonts: [(UILabel, CGFloat, UIFont.TextStyle)] = [
            (title, 17, .body), (author, 13, .subheadline), (intro, 13, .subheadline),
            (wordCount, 12, .caption1), (source, 12, .caption1), (chapter, 11, .caption2),
        ]
        for (label, size, style) in fonts {
            label.font = UIFontMetrics(forTextStyle: style).scaledFont(for: .systemFont(ofSize: size, weight: .regular), compatibleWith: traitCollection)
        }
        let accessibility = traitCollection.preferredContentSizeCategory.isAccessibilityCategory
        metadata.axis = accessibility ? .vertical : .horizontal
        metadata.alignment = accessibility ? .leading : .center
        metadataSpace.isHidden = accessibility
        wordWidth.isActive = !accessibility && !wordCount.isHidden && !source.isHidden
    }

    nonisolated deinit {
        pending?.cancel()
    }
}

@MainActor
private final class NovelBookTagLabel: UILabel {
    override init(frame: CGRect) {
        super.init(frame: frame)
        layer.cornerRadius = 5; clipsToBounds = true
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
    override var intrinsicContentSize: CGSize {
        let size = super.intrinsicContentSize
        return CGSize(width: size.width + 12, height: size.height + 6)
    }
    override func drawText(in rect: CGRect) {
        super.drawText(in: rect.insetBy(dx: 6, dy: 3))
    }
}
#endif
