#if os(iOS)
import UIKit
import MeowUI

@MainActor
public final class FilmHomeViewController: PosterHomeViewController {
    public var onSelectCategory: ((FilmCategoryModel) -> Void)?
    public var onSelectPlaybackItem: ((FilmItem, FilmCategory?) -> Void)?
    let model: FilmHomeModel
    private let images: ArtworkImages
    var sections: [FilmHomeSection] = []

    public init(model: FilmHomeModel, images: ArtworkImages, themeProvider: any PageThemeProviding) {
        self.model = model
        self.images = images
        super.init(
            configuration: PosterHomeConfiguration(
                pageAccessibilityID: "film.home",
                collectionAccessibilityID: "film.home.sections",
                moreIdentifierPrefix: "film.home.more"
            ),
            themeProvider: themeProvider
        )
        pageTitle = "影视"
    }

    public override func viewDidLoad() {
        super.viewDidLoad()
        observe(model, whenVisible: true) { [weak self] in self?.render() }
        render()
    }

    public override func pageDidBecomeVisible() {
        model.loadIfNeeded()
        render()
        super.pageDidBecomeVisible()
    }

    public override func pageDidBecomeInactive() {
        super.pageDidBecomeInactive()
        model.close()
    }

    public override func pageDidBecomeHidden() {
        model.cancel()
        super.pageDidBecomeHidden()
    }

    public override func didRequestRefresh() {
        clearErrorSource("home")
        model.refresh()
    }

    public override func didTapSectionMore(sectionID: String) {
        guard let category = model.categoryModel(sectionID: sectionID) else { return }
        onSelectCategory?(category)
    }

    public override func didSelectCard(inSection sectionID: String, cardID: String, index: Int) {
        guard let section = sections.first(where: { $0.id == sectionID }) else { return }
        let item: FilmItem?
        if section.items.indices.contains(index),
           Self.cardID(for: section.items[index]) == cardID {
            item = section.items[index]
        } else {
            item = section.items.first { Self.cardID(for: $0) == cardID }
        }
        guard let item else { return }
        onSelectPlaybackItem?(item, model.categoryModel(sectionID: sectionID)?.category)
    }

    static func makePosterCard(_ item: FilmItem, images: ArtworkImages, textBadge: String = "", label: String = "") -> PosterCatalogCard {
        PosterCatalogCard(
            id: cardID(for: item),
            title: item.title,
            textBadge: textBadge,
            label: label,
            accessibilityIdentifier: "film.poster.\(item.sourceID).\(item.id)",
            artwork: .url(item.posterURL, images: images)
        )
    }

    static func cardID(for item: FilmItem) -> String {
        if let targetID = item.targetID, !targetID.isEmpty {
            return targetID
        }
        return "\(item.sourceID)\u{1F}\(item.id)\u{1F}\(item.kind?.rawValue ?? "")"
    }

    private func render() {
        guard isViewLoaded else { return }
        sections = model.sections
        let projected = sections.map { section in
            PosterCatalogSection(
                id: section.id,
                title: section.title,
                hasMore: model.supportsCategory(sectionID: section.id),
                statusText: "暂无影片",
                items: section.items.map { Self.makePosterCard($0, images: images) }
            )
        }
        applyHomeState(
            sourceID: model.selectedSourceID,
            sections: projected,
            isLoading: model.isLoading,
            hasError: model.error != nil,
            emptyText: "暂无影片"
        )
        syncErrorSource(
            "home",
            message: model.error?.localizedDescription,
            token: model.error.map { AnyHashable(String(describing: $0)) },
            retry: { [weak self] in self?.model.refresh() }
        )
    }
}
#endif
