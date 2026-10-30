#if os(iOS)
import UIKit
import MeowUI

@MainActor
final class MusicQueueCurrentPage: UIViewController {
    private let layout = MusicQueueListLayout()
    lazy var collection = UICollectionView(frame: .zero, collectionViewLayout: layout)
    let mode = UIButton(type: .system), continuePlaying = UIButton(type: .system)
    let retry = UIButton(type: .system)
    private let controls = UIStackView()
    private let empty = UILabel()

    override func viewDidLoad() {
        super.viewDidLoad()
        view.backgroundColor = .clear
        layout.minimumLineSpacing = 0
        layout.minimumInteritemSpacing = 0
        collection.backgroundColor = .clear
        collection.contentInsetAdjustmentBehavior = .never
        collection.alwaysBounceVertical = true
        collection.isDirectionalLockEnabled = true
        collection.accessibilityIdentifier = "music.queue.current"
        controls.distribution = .fillEqually
        controls.spacing = 8
        for button in [mode, continuePlaying, retry] { controls.addArrangedSubview(button) }
        retry.setTitle("重试", for: .normal)
        controls.isHidden = true
        empty.textAlignment = .center
        view.addSubview(collection)
        view.addSubview(controls)
    }

    func configure(snapshot: MusicSimilarModeSnapshot?, count: Int, modeTitle: String, theme: PageTheme) {
        loadViewIfNeeded()
        controls.isHidden = snapshot?.active != true
        retry.isHidden = snapshot?.canRetry != true
        mode.setTitle(modeTitle, for: .normal)
        continuePlaying.setTitle(snapshot?.continueEnabled == true ? "续播已开启" : "续播已关闭", for: .normal)
        for button in [mode, continuePlaying, retry] {
            button.tintColor = UIColor(theme.ink)
            button.backgroundColor = UIColor(theme.chip)
            button.layer.cornerRadius = 12
            button.titleLabel?.font = .systemFont(ofSize: 13)
        }
        empty.text = count == 0 ? "没有正在播放歌曲" : nil
        empty.textColor = UIColor(theme.muted)
        collection.backgroundView = empty.text == nil ? nil : empty
        view.setNeedsLayout()
    }

    override func viewDidLayoutSubviews() {
        super.viewDidLayoutSubviews()
        let width = view.bounds.width, height = view.bounds.height
        let footer: CGFloat = controls.isHidden ? 0 : 52
        collection.frame = CGRect(x: 0, y: 0, width: width, height: max(0, height - footer))
        controls.frame = CGRect(x: 0, y: height - footer + 8, width: width, height: 44)
    }
}

@MainActor
private final class MusicQueueListLayout: UICollectionViewFlowLayout {
    override func prepare() {
        if let collectionView, collectionView.bounds.width > 0 {
            let insets = collectionView.adjustedContentInset
            let size = CGSize(width: max(0, collectionView.bounds.width - insets.left - insets.right), height: 64)
            if itemSize != size { itemSize = size }
        }
        super.prepare()
    }

    override func shouldInvalidateLayout(forBoundsChange newBounds: CGRect) -> Bool {
        newBounds.width != collectionView?.bounds.width || super.shouldInvalidateLayout(forBoundsChange: newBounds)
    }
}

@MainActor
final class MusicQueueTablePage: UIViewController {
    let table = UITableView(frame: .zero, style: .plain)
    private let empty = UILabel()
    override func viewDidLoad() {
        super.viewDidLoad()
        view.backgroundColor = .clear
        table.backgroundColor = .clear
        table.separatorStyle = .none
        table.rowHeight = 64
        table.contentInsetAdjustmentBehavior = .never
        table.isDirectionalLockEnabled = true
        empty.textAlignment = .center
        view.addSubview(table)
    }
    func setEmpty(_ text: String?, theme: PageTheme) {
        loadViewIfNeeded()
        empty.text = text; empty.textColor = UIColor(theme.muted)
        table.backgroundView = text == nil ? nil : empty
    }
    override func viewDidLayoutSubviews() {
        super.viewDidLayoutSubviews()
        table.frame = view.bounds
    }
}
#endif
