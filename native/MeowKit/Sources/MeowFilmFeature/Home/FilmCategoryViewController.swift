#if os(iOS)
import UIKit
import MeowUI

@MainActor
public final class FilmCategoryViewController: PosterCategoryViewController {
    public var onSelectItem: ((FilmItem) -> Void)?
    let model: FilmCategoryModel
    private let images: ArtworkImages
    var renderedItems: [FilmItem] = []

    public init(model: FilmCategoryModel, images: ArtworkImages, themeProvider: any PageThemeProviding) {
        self.model = model
        self.images = images
        super.init(
            configuration: PosterCategoryConfiguration(
                pageAccessibilityID: "film.category.\(model.category.rawValue)",
                collectionAccessibilityID: "film.category.grid",
                filterIdentifierPrefix: "film.category",
                moreButtonAccessibilityID: "film.category.more",
                loadingAccessibilityID: "film.category.loading",
                emptyText: "暂无影片"
            ),
            themeProvider: themeProvider
        )
        pageTitle = model.category.title
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

    public override func didSelectCard(cardID: String, index: Int) {
        let item: FilmItem?
        if renderedItems.indices.contains(index),
           FilmHomeViewController.cardID(for: renderedItems[index]) == cardID {
            item = renderedItems[index]
        } else {
            item = renderedItems.first { FilmHomeViewController.cardID(for: $0) == cardID }
        }
        guard let item else { return }
        onSelectItem?(item)
    }

    public override func didSelectFilter(rowID: String, value: String) {
        clearErrorSource("category")
        model.selectFilter(rowID, value: value)
    }

    public override func didRequestRefresh() {
        clearErrorSource("category")
        model.refresh()
    }

    public override func didRequestLoadMore() {
        clearErrorSource("category")
        model.loadMore()
    }

    private func render() {
        guard isViewLoaded else { return }
        renderedItems = model.items
        let rows = model.filterRows.map { row in
            PosterCategoryFilterRowModel(
                id: row.id,
                title: row.title,
                selectedID: model.filters[row.id] ?? "",
                options: row.options
            )
        }
        let cards = renderedItems.map { FilmHomeViewController.makePosterCard($0, images: images) }
        applyCategoryState(
            filterRows: rows,
            cards: cards,
            isLoading: model.isLoading,
            hasMore: model.hasMore,
            hasError: model.error != nil
        )
        syncErrorSource(
            "category",
            message: model.error?.localizedDescription,
            token: model.error.map { AnyHashable(String(describing: $0)) },
            retry: { [weak self] in self?.model.loadMore() }
        )
    }
}
#endif
