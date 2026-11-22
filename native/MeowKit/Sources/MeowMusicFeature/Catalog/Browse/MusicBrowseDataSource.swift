#if os(iOS)
import UIKit
import MeowUI

@MainActor
extension MusicBrowseViewController {
    func makeCell(in collectionView: UICollectionView, at indexPath: IndexPath, id: ItemID)
        -> UICollectionViewCell? {
        guard let row = rows[id] else { return nil }
        let cell = collectionView.dequeueReusableCell(
            withReuseIdentifier: row.section.layout == .banner ? "Banner" : MusicBrowseCell.reuseIdentifier,
            for: indexPath)
        configure(cell, section: row.section, item: row.item)
        return cell
    }

    func configure(_ cell: UICollectionViewCell, section: MusicPublicBrowseSection,
                           item: MusicPublicBrowseItem) {
        if let cell = cell as? MusicBannerCarouselCell {
            cell.accessibilityIdentifier = "music.browse.banner.\(section.id)"
            cell.configure(section.items, theme: theme, images: model.artworkImages) { [weak self] item in
                if case .track = item.content { self?.play(item, section: section) }
                else { self?.open(item, section: section) }
            }
            cell.setActive(isPageVisible && !tags.isExpanded)
            return
        }
        guard let cell = cell as? MusicBrowseCell else { return }
        cell.configure(item: item, layout: section.layout, theme: theme, images: model.artworkImages, artworkURL: artworkURL(for: item),
                       onPreview: { [weak self] track in
            guard let self, let current = self.currentSection(for: item, id: section.id) else { return }
            self.play(item, section: current, startingAt: track)
        }) { [weak self] in
            guard let self, let current = self.currentSection(for: item, id: section.id) else { return }
            self.play(item, section: current)
        }
        cell.accessibilityIdentifier = "music.browse.item.\(item.id)"
        configurePlaybackLoading(cell, section: section, item: item)
    }

    func makeHeader(in collectionView: UICollectionView, kind: String,
                            at indexPath: IndexPath) -> UICollectionReusableView? {
        guard let section = section(at: indexPath.section) else { return nil }
        let header = collectionView.dequeueReusableSupplementaryView(
            ofKind: kind, withReuseIdentifier: MusicBrowseHeader.reuseIdentifier, for: indexPath
        ) as! MusicBrowseHeader
        configure(header, section: section, first: indexPath.section == 0)
        return header
    }

    func configure(_ header: MusicBrowseHeader, section: MusicPublicBrowseSection, first: Bool) {
        header.configure(section: section,
                         theme: theme, images: model.artworkImages, showsSectionPlayback: showsSectionPlayback, onPlay: { [weak self] in
            guard let self, let current = self.sections.first(where: { $0.id == section.id }),
                  let item = current.items.first else { return }
            self.play(item, section: current)
        }, onChannel: { [weak self] in
            if let channel = section.channel { self?.router?.selectChannel(channel) }
        }, onChoice: { [weak self] id in
            guard let self, let current = self.sections.first(where: { $0.id == section.id }) else { return }
            self.selectChoice(id, section: current)
        })
    }

    public func collectionView(_ collectionView: UICollectionView, didSelectItemAt indexPath: IndexPath) {
        guard let row = row(at: indexPath) else { return }
        open(row.item, section: row.section)
    }

    func open(_ item: MusicPublicBrowseItem, section: MusicPublicBrowseSection) {
        switch item.content {
        case .collection(let value): router?.showPublicCollection(value)
        case .channel(let value): router?.selectChannel(value)
        case .daily(let value): router?.showPublicBrowse(.discovery(.daily(value)), title: item.title)
        case .action(let value):
            if value.isPersonalFM || value.isHeartbeat || value.isSimilar { play(item, section: section) }
            else { router?.showPublicBrowse(.homeAction(value, title: item.title), title: item.title) }
        case .track: play(item, section: section)
        case .unavailable: break
        }
    }

    public func collectionView(_ collectionView: UICollectionView, willDisplay cell: UICollectionViewCell,
                               forItemAt indexPath: IndexPath) {
        (cell as? MusicBannerCarouselCell)?.setActive(isPageVisible && !tags.isExpanded)
        updateVisibleArtworkDemand(including: indexPath)
        guard let row = row(at: indexPath) else { return }
        if let cell = cell as? MusicBrowseCell {
            configurePlaybackLoading(cell, section: row.section, item: row.item)
        }
        let item = row.item
        if case .action(let action) = item.content, action.isPersonalFM {
            loadFMPreview(item, action: action)
        }
        guard indexPath.section == sections.count - 1, indexPath.item >= row.section.items.count - 3 else { return }
        loadAutomaticNextPage()
    }

    func loadAutomaticNextPage() {
        guard let page, page.request != .discovery(.home),
              isPageVisible, !automaticPaginationHeld, page.error == nil, !page.isLoading,
              !page.requiresExplicitNext, !page.usesPageNavigation, page.nextCursor != nil else { return }
        runPageTask { await page.appendNext() }
    }

    public func collectionView(_ collectionView: UICollectionView, didEndDisplaying cell: UICollectionViewCell,
                               forItemAt indexPath: IndexPath) {
        (cell as? MusicBrowseCell)?.resetPressFeedback()
        (cell as? MusicBrowseCell)?.setDisplayVisible(false)
        (cell as? MusicBannerCarouselCell)?.setActive(false)
        updateVisibleArtworkDemand(excluding: indexPath)
    }

    func updateVisibleArtworkDemand(including: IndexPath? = nil, excluding: IndexPath? = nil) {
        guard isPageVisible else { page?.setVisibleArtworkItems([]); return }
        var paths = Set(collection.indexPathsForVisibleItems)
        if let including { paths.insert(including) }
        if let excluding { paths.remove(excluding) }
        page?.setVisibleArtworkItems(paths.compactMap { path in
            guard let row = row(at: path), row.section.layout != .banner else { return nil }
            return row.item
        })
    }

    public func scrollViewWillBeginDragging(_ scrollView: UIScrollView) {
        automaticPaginationHeld = false
        for cell in collection.visibleCells.compactMap({ $0 as? MusicBrowseCell }) { cell.resetPressFeedback() }
    }

    public func collectionView(_ collectionView: UICollectionView,
                               contextMenuConfigurationForItemAt indexPath: IndexPath,
                               point: CGPoint) -> UIContextMenuConfiguration? {
        guard let page, let row = row(at: indexPath), case .track(let track) = row.item.content else { return nil }
        let request = page.actionPageID
        return UIContextMenuConfiguration(identifier: nil, previewProvider: nil) { [weak self] _ in
            guard let self, let page = self.page, page.actionPageID == request else { return nil }
            var actions: [UIMenuElement] = [
                UIAction(title: "下一首播放", image: UIImage(systemName: "text.line.first.and.arrowtriangle.forward")) {
                    [weak self] _ in
                    guard page.actionPageID == request else { return }
                    self?.perform(operation: "下一首播放", kind: .queue) {
                        await self?.model.insertNext(track)
                    }
                },
                UIAction(title: "音源", image: UIImage(systemName: "slider.horizontal.3")) { [weak self] _ in
                    guard page.actionPageID == request else { return }
                    self?.showSources(track)
                },
                UIAction(title: "添加到播放队列", image: UIImage(systemName: "text.badge.plus")) {
                    [weak self] _ in
                    guard page.actionPageID == request else { return }
                    self?.perform(operation: "添加到播放队列", kind: .queue) {
                        await self?.model.append(track)
                    }
                },
            ]
            actions.append(UIMenu(title: "添加到歌单", children: self.model.collections.filter {
                ["playlist", "favorites"].contains($0.kind)
            }.map { target in
                UIAction(title: target.title) { [weak self] _ in
                    guard page.actionPageID == request else { return }
                    self?.perform(operation: "添加到歌单", kind: .library) {
                        await self?.model.add(track, to: target)
                    }
                }
            }))
            return UIMenu(children: actions)
        }
    }

    func makeLayout() -> UICollectionViewLayout {
        UICollectionViewCompositionalLayout { [weak self] index, environment in
            guard let self, self.sections.indices.contains(index) else { return nil }
            let section = self.sections[index]
            let width = environment.container.effectiveContentSize.width
            let item = NSCollectionLayoutItem(layoutSize: NSCollectionLayoutSize(
                widthDimension: .fractionalWidth(1), heightDimension: .fractionalHeight(1)))
            let group: NSCollectionLayoutGroup
            var scrolling = UICollectionLayoutSectionOrthogonalScrollingBehavior.none
            switch section.layout {
            case .featureRail:
                let card = min(216, max(112, width * 0.36))
                group = .horizontal(layoutSize: .init(widthDimension: .absolute(card),
                    heightDimension: .absolute(card / 0.775)), subitems: [item])
                scrolling = .continuous
            case .songColumns, .vipSongs:
                group = .vertical(layoutSize: .init(widthDimension: .absolute((width - 16) * 0.94),
                    heightDimension: .absolute(180)), subitem: item, count: 3)
                scrolling = .groupPaging
            case .banner:
                let bannerWidth = max(1, width - Self.sectionHorizontalInset * 2)
                group = .horizontal(layoutSize: .init(widthDimension: .absolute(bannerWidth),
                    heightDimension: .absolute(bannerWidth / 2.56)), subitems: [item])
                scrolling = .groupPagingCentered
            case .grid, .artistGrid, .channelGrid:
                let count = width < 359 ? 2 : 3
                group = .horizontal(layoutSize: .init(widthDimension: .fractionalWidth(1),
                    heightDimension: .absolute((width - 32 - CGFloat(count - 1) * 12) / CGFloat(count) + 42)),
                    subitem: item, count: count)
                group.interItemSpacing = .fixed(12)
            case .rankingStack:
                group = .vertical(layoutSize: .init(widthDimension: .fractionalWidth(1),
                    heightDimension: .absolute(156)), subitems: [item])
            case .artistUpdates:
                let card = max(240, (width - 32) * 0.91)
                group = .horizontal(layoutSize: .init(widthDimension: .absolute(card),
                    heightDimension: .absolute(card / 2.9)), subitems: [item])
                scrolling = .groupPaging
            case .rankedSongs:
                group = .horizontal(layoutSize: .init(widthDimension: .absolute(max(240, (width - 32) * 0.91)),
                    heightDimension: .absolute(210)), subitems: [item])
                scrolling = .groupPaging
            case .vipRankedSongs:
                group = .vertical(layoutSize: .init(widthDimension: .fractionalWidth(1),
                    heightDimension: .absolute(210)), subitems: [item])
            case .list, .albumRanking:
                group = .vertical(layoutSize: .init(widthDimension: .fractionalWidth(1),
                    heightDimension: .absolute(64)), subitems: [item])
            case .playlistRail, .rankingSmall:
                let card = section.layout == .rankingSmall ? max(1, (width - 32) * 0.24) :
                    min(216, max(112, width * 0.36))
                group = .horizontal(layoutSize: .init(widthDimension: .absolute(card),
                    heightDimension: .absolute(card + 42)), subitems: [item])
                scrolling = .continuous
            }
            let result = NSCollectionLayoutSection(group: group)
            let headerHeight = MusicBrowseHeader.height(
                for: section, showsSectionPlayback: self.showsSectionPlayback)
            result.contentInsets = .init(top: index == 0 && headerHeight == 0 &&
                                         section.layout != .banner ? PageLayoutMetrics.contentGap : 4,
                                         leading: Self.sectionHorizontalInset, bottom: 20,
                                         trailing: Self.sectionHorizontalInset)
            result.interGroupSpacing = 9
            result.orthogonalScrollingBehavior = scrolling
            if headerHeight > 0 {
                let header = NSCollectionLayoutBoundarySupplementaryItem(
                    layoutSize: .init(widthDimension: .fractionalWidth(1), heightDimension: .absolute(headerHeight)),
                    elementKind: UICollectionView.elementKindSectionHeader, alignment: .top)
                result.boundarySupplementaryItems = [header]
            }
            return result
        }
    }
}

@MainActor
final class MusicBrowseCollectionView: UICollectionView {
    override func touchesShouldCancel(in view: UIView) -> Bool {
        view is UIButton || super.touchesShouldCancel(in: view)
    }
}
#endif
