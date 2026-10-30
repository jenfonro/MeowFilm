#if os(iOS)
import UIKit

@MainActor
public final class NavigationTitleView: UIView {
    public override var intrinsicContentSize: CGSize {
        CGSize(width: UIView.layoutFittingExpandedSize.width, height: PageLayoutMetrics.searchControl)
    }

    public init(content: UIView) {
        super.init(frame: .zero)
        setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        addSubview(content)
        content.pinEdges(to: self)
        content.heightAnchor.constraint(equalToConstant: PageLayoutMetrics.searchControl).isActive = true
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
}
#endif
