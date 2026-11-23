#if os(iOS)
import UIKit

/// UIKit owns the lifted item, neighbour movement and edge autoscrolling.
@MainActor
final class MusicQueueReorderController: NSObject, UIGestureRecognizerDelegate {
    var onBegin: (() -> Bool)?
    var onCancel: (() -> Void)?
    var onEnd: (() -> Void)?
    private(set) var isActive = false
    private weak var collection: UICollectionView?
    private let handle = UILongPressGestureRecognizer()
    private let row = UILongPressGestureRecognizer()
    private let press = UILongPressGestureRecognizer()
    private var activeGesture: UILongPressGestureRecognizer?
    private weak var activeCell: MusicQueueCell?, pressedCell: MusicQueueCell?
    private var grabOffset = CGPoint.zero
    private var pressStart = CGPoint.zero

    init(collection: UICollectionView, pager: UIScrollView) {
        self.collection = collection
        super.init()
        handle.minimumPressDuration = 0
        row.minimumPressDuration = 0.38
        row.allowableMovement = 9
        for gesture in [handle, row] {
            gesture.delegate = self
            gesture.addTarget(self, action: #selector(pressed))
            collection.addGestureRecognizer(gesture)
        }
        press.minimumPressDuration = 0
        press.cancelsTouchesInView = false
        press.delegate = self
        press.addTarget(self, action: #selector(feedback))
        collection.addGestureRecognizer(press)
        collection.panGestureRecognizer.require(toFail: handle)
        pager.panGestureRecognizer.require(toFail: handle)
    }

    func gestureRecognizer(_ gestureRecognizer: UIGestureRecognizer, shouldReceive touch: UITouch) -> Bool {
        guard !isActive, let collection,
              let path = collection.indexPathForItem(at: touch.location(in: collection)),
              let cell = collection.cellForItem(at: path) as? MusicQueueCell else { return false }
        if gestureRecognizer === press { return true }
        var hit = touch.view
        while let view = hit, view !== cell {
            if view is UIControl { return false }
            hit = view.superview
        }
        let isHandle = cell.row.isReorderHandle(at: touch.location(in: cell.row))
        return gestureRecognizer === handle ? isHandle : !isHandle
    }

    func gestureRecognizer(_ gestureRecognizer: UIGestureRecognizer,
                           shouldRecognizeSimultaneouslyWith other: UIGestureRecognizer) -> Bool {
        gestureRecognizer === press || other === press
    }

    @objc private func feedback(_ gesture: UILongPressGestureRecognizer) {
        guard let collection else { return }
        let point = gesture.location(in: collection)
        switch gesture.state {
        case .began:
            beginPress(at: point)
        case .changed where hypot(point.x - pressStart.x, point.y - pressStart.y) > 9 && !isActive:
            endPress()
        case .ended, .cancelled, .failed:
            endPress()
        default:
            break
        }
    }

    func beginPress(at point: CGPoint) {
        endPress()
        guard let collection, let path = collection.indexPathForItem(at: point),
              let cell = collection.cellForItem(at: path) as? MusicQueueCell else { return }
        pressStart = point
        pressedCell = cell
        cell.row.setPressed(true)
    }

    func endPress() {
        pressedCell?.row.setPressed(false)
        pressedCell = nil
    }

    @objc private func pressed(_ gesture: UILongPressGestureRecognizer) {
        guard let collection else { return }
        let point = gesture.location(in: collection)
        switch gesture.state {
        case .began:
            if begin(at: point) { activeGesture = gesture }
        case .changed:
            guard gesture === activeGesture else { return }
            move(to: point)
        case .ended:
            guard gesture === activeGesture else { return }
            end()
        case .cancelled, .failed:
            if gesture === activeGesture { cancel() }
        default:
            break
        }
    }

    @discardableResult
    func begin(at point: CGPoint) -> Bool {
        guard !isActive, let collection, let path = collection.indexPathForItem(at: point),
              let attributes = collection.layoutAttributesForItem(at: path),
              let cell = collection.cellForItem(at: path) as? MusicQueueCell, onBegin?() == true else { return false }
        grabOffset = CGPoint(x: point.x - attributes.center.x, y: point.y - attributes.center.y)
        guard collection.beginInteractiveMovementForItem(at: path) else { onCancel?(); return false }
        isActive = true
        activeCell = cell
        cell.row.setReordering(true)
        return true
    }

    func move(to point: CGPoint) {
        guard isActive else { return }
        collection?.updateInteractiveMovementTargetPosition(
            CGPoint(x: point.x - grabOffset.x, y: point.y - grabOffset.y))
    }

    func end() {
        guard isActive else { return }
        activeGesture = nil
        isActive = false
        finishHighlight()
        collection?.endInteractiveMovement()
        onEnd?()
    }

    func cancel() {
        guard isActive else { return }
        activeGesture = nil
        isActive = false
        finishHighlight()
        collection?.cancelInteractiveMovement()
        onCancel?()
    }

    private func finishHighlight() {
        activeCell?.row.setReordering(false)
        activeCell = nil
        endPress()
    }
}
#endif
