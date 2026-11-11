#if os(iOS)
import XCTest
import UIKit
import MeowUI
@testable import MeowMusicFeature

@MainActor
final class MusicLyricClockTests: XCTestCase {
    func testShortWordsAndGapsContinueBetweenHalfSecondMediaSamples() async throws {
        let line = MusicLyricLine(index: 0, time: 0, end: 2, text: "MMMM MMMM MMMM", words: [
            .init(time: 0.01, duration: 0.04, text: "MMMM"),
            .init(time: 0.24, duration: 0.04, text: "MMMM"),
            .init(time: 0.35, duration: 0, text: "MMMM")
        ])
        let fixture = mount(line, position: 0, playing: true)
        defer { fixture.close() }
        let mask = try highlightMask(in: fixture.cell)
        await elapse(0.09)
        let first = visibleWidth(mask, presented: true)
        XCTAssertGreaterThan(first, 0, "A short word must finish without another media sample")
        await elapse(0.06)
        XCTAssertEqual(visibleWidth(mask, presented: true), first, accuracy: 0.5, "The real gap must hold its highlight")
        await elapse(0.27)
        XCTAssertGreaterThan(visibleWidth(mask, presented: true), first * 2,
                             "Later short words must continue within the same 0.5s sample interval")
    }

    func testLayoutAndStyleRefreshDoNotRewindToLastMediaSample() async throws {
        let line = longWord()
        let fixture = mount(line, position: 0, playing: true)
        defer { fixture.close() }
        let mask = try highlightMask(in: fixture.cell)
        await elapse(0.16)
        let before = visibleWidth(mask, presented: true)
        XCTAssertGreaterThan(before, 0)
        fixture.cell.frame.size.width = 420
        for view in descendants(fixture.cell) { view.setNeedsLayout() }
        fixture.cell.setNeedsLayout()
        fixture.cell.layoutIfNeeded()
        XCTAssertGreaterThanOrEqual(visibleWidth(mask), before - 0.5)
        configure(fixture.cell, line: line, position: 0, playing: true)
        fixture.cell.layoutIfNeeded()
        XCTAssertGreaterThanOrEqual(visibleWidth(mask), before - 0.5,
                                    "Reapplying appearance must preserve the presentation clock")
        await elapse(0.1)
        XCTAssertGreaterThan(visibleWidth(mask, presented: true), before)
    }

    func testNewMediaSampleCalibratesProgressAndRateChangeKeepsPosition() async throws {
        let fixture = mount(longWord(), position: 1, playing: true)
        defer { fixture.close() }
        let mask = try highlightMask(in: fixture.cell)
        let before = visibleWidth(mask)
        fixture.cell.updateTime(0.25, playing: true, rate: 1, active: true)
        XCTAssertLessThan(visibleWidth(mask), before / 2)
        await elapse(0.08)
        let calibrated = visibleWidth(mask, presented: true)
        fixture.cell.updateState(selected: false, active: true, timed: true,
            previewDragging: false, position: 0.25, playing: true, rate: 2)
        XCTAssertGreaterThanOrEqual(visibleWidth(mask), calibrated - 0.5)
        await elapse(0.08)
        XCTAssertGreaterThan(visibleWidth(mask, presented: true), calibrated)
    }

    func testPauseFreezesAndConfirmedSeekUpdatesImmediatelyThenResumes() async throws {
        let fixture = mount(longWord(), position: 0, playing: true)
        defer { fixture.close() }
        let mask = try highlightMask(in: fixture.cell)
        await elapse(0.12)
        fixture.cell.updateState(selected: false, active: true, timed: true,
            previewDragging: false, position: 0, playing: false, rate: 1)
        let frozen = visibleWidth(mask)
        XCTAssertGreaterThan(frozen, 0)
        await elapse(0.1)
        XCTAssertEqual(visibleWidth(mask, presented: true), frozen, accuracy: 0.01)
        fixture.cell.updateTime(1.5, playing: false, rate: 1, active: true)
        let sought = visibleWidth(mask)
        XCTAssertGreaterThan(sought, frozen * 2)
        await elapse(0.08)
        XCTAssertEqual(visibleWidth(mask, presented: true), sought, accuracy: 0.01)
        fixture.cell.updateState(selected: false, active: true, timed: true,
            previewDragging: false, position: 1.5, playing: true, rate: 1)
        await elapse(0.1)
        XCTAssertGreaterThan(visibleWidth(mask, presented: true), sought)
        fixture.cell.updateTime(0, playing: true, rate: 1, active: true)
        XCTAssertLessThan(visibleWidth(mask), frozen, "Playing seek must also take effect immediately")
        await elapse(0.08)
        XCTAssertGreaterThan(visibleWidth(mask, presented: true), 0)
    }

    func testConfirmedSeekToRepeatedSampleRewindsInsteadOfBeingIgnored() async throws {
        let fixture = mount(longWord(), position: 0, playing: true)
        defer { fixture.close() }
        let mask = try highlightMask(in: fixture.cell)
        let initial = visibleWidth(mask)
        await elapse(0.16)
        let advanced = visibleWidth(mask, presented: true)
        XCTAssertGreaterThan(advanced, initial)
        fixture.cell.updateTime(0, playing: true, rate: 1, active: true)
        XCTAssertLessThan(visibleWidth(mask), advanced / 2)
    }

    func testCancellingTimedHighlightKeepsBodyAndDoesNotReviveFromOldTick() async throws {
        let line = longWord()
        let fixture = mount(line, position: 0, playing: true)
        defer { fixture.close() }
        let mask = try highlightMask(in: fixture.cell)
        await elapse(0.08)
        XCTAssertGreaterThan(visibleWidth(mask, presented: true), 0)
        fixture.cell.updateState(selected: false, active: false, timed: false,
            previewDragging: false, position: 0, playing: false, rate: 1)
        XCTAssertNil(mask.path)
        await elapse(0.08)
        XCTAssertNil(mask.path)
        XCTAssertTrue(descendants(fixture.cell).compactMap { $0 as? UILabel }.contains { $0.text == line.text })
    }

    func testEmptyWordsHighlightWholeLineAndZeroDurationCompletesAtItsStart() async throws {
        let line = MusicLyricLine(index: 0, time: 0, end: 20, text: "whole line")
        let fixture = mount(line, position: 0, playing: false)
        defer { fixture.close() }
        let mask = try highlightMask(in: fixture.cell)
        XCTAssertEqual(try XCTUnwrap(mask.path).boundingBoxOfPath, mask.bounds)
        fixture.cell.updateTime(0, playing: false, rate: 1, active: false)
        XCTAssertNil(mask.path, "The initial centered row must not be highlighted before it is active")
        XCTAssertTrue(descendants(fixture.cell).compactMap { $0 as? UILabel }.contains { $0.text == line.text },
                      "Stopping the highlight must preserve the lyric body")
        let zero = MusicLyricLine(index: 1, time: -1, end: 20, text: "MMMM MMMM", words: [
            .init(time: -0.1, duration: 0, text: "MMMM"),
            .init(time: 0.1, duration: 0, text: "MMMM")
        ])
        configure(fixture.cell, line: zero, position: -0.2, playing: false)
        fixture.cell.layoutIfNeeded()
        XCTAssertEqual(visibleWidth(mask), 0)
        fixture.cell.updateTime(-0.1, playing: false, rate: 1, active: true)
        let first = visibleWidth(mask)
        XCTAssertGreaterThan(first, 0)
        fixture.cell.updateTime(0.1, playing: false, rate: 1, active: true)
        let complete = visibleWidth(mask)
        XCTAssertGreaterThan(complete, first * 2)
        fixture.cell.updateTime(10, playing: true, rate: 1, active: true)
        await elapse(0.08)
        XCTAssertEqual(visibleWidth(mask, presented: true), complete, accuracy: 0.01,
                       "An unbounded last word must remain highlighted without a fabricated duration")
    }

    func testHiddenDetachedAndReusedCellsStopUpdatingAndReleaseOldContent() async throws {
        let fixture = mount(longWord(), position: 0, playing: true)
        defer { fixture.close() }
        let mask = try highlightMask(in: fixture.cell)
        await elapse(0.08)
        fixture.cell.isHidden = true
        let hidden = visibleWidth(mask)
        await elapse(0.08)
        XCTAssertEqual(visibleWidth(mask, presented: true), hidden, accuracy: 0.01)
        fixture.cell.isHidden = false
        XCTAssertGreaterThan(visibleWidth(mask), hidden)
        fixture.cell.removeFromSuperview()
        let detached = visibleWidth(mask)
        await elapse(0.08)
        XCTAssertEqual(visibleWidth(mask, presented: true), detached, accuracy: 0.01)
        fixture.cell.prepareForReuse()
        XCTAssertNil(mask.path)
        XCTAssertNil(fixture.cell.accessibilityLabel)
        XCTAssertTrue(descendants(fixture.cell).compactMap { $0 as? UILabel }.allSatisfy { $0.text == nil })
        await elapse(0.08)
        XCTAssertNil(mask.path, "A stale tick must not restore the old row")

        weak var releasedCell: MusicLyricCell?
        autoreleasepool {
            let cell = MusicLyricCell(style: .default, reuseIdentifier: nil)
            cell.frame = fixture.cell.frame
            fixture.window.rootViewController?.view.addSubview(cell)
            configure(cell, line: longWord(), position: 0, playing: true)
            cell.layoutIfNeeded()
            releasedCell = cell
            cell.removeFromSuperview()
        }
        // Let UIKit's pending layer transaction release its temporary view
        // references before checking ownership by the presentation clock.
        CATransaction.flush()
        await elapse(0.08)
        XCTAssertNil(releasedCell, "The presentation clock must not retain its cell")
    }

    private func longWord() -> MusicLyricLine {
        .init(index: 0, time: 0, end: 3, text: "MMMMMMMM", words: [
            .init(time: 0, duration: 2, text: "MMMMMMMM")
        ])
    }

    private func configure(_ cell: MusicLyricCell, line: MusicLyricLine,
                           position: Double, playing: Bool) {
        cell.configure(line: line, selected: false, active: true, timed: true,
            previewDragging: false, position: position, rate: 1, playing: playing,
            preferences: MusicAppearancePreferences(), theme: PageTheme())
    }

    @MainActor
    private struct Fixture {
        let window: UIWindow
        let cell: MusicLyricCell
        let previousKeyWindow: UIWindow?

        func close() {
            cell.prepareForReuse()
            window.isHidden = true
            window.rootViewController = nil
            previousKeyWindow?.makeKey()
        }
    }

    private func mount(_ line: MusicLyricLine, position: Double, playing: Bool) -> Fixture {
        let scene = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first
        XCTAssertEqual(scene?.activationState, .foregroundActive,
                       "Presentation interpolation requires the actual foreground App host")
        let previousKeyWindow = scene?.keyWindow
        let window = scene.map { UIWindow(windowScene: $0) } ?? UIWindow(frame: CGRect(x: 0, y: 0, width: 390, height: 200))
        window.rootViewController = UIViewController()
        window.makeKeyAndVisible()
        let cell = MusicLyricCell(style: .default, reuseIdentifier: nil)
        cell.frame = CGRect(x: 0, y: 0, width: 390, height: 80)
        window.rootViewController?.view.addSubview(cell)
        configure(cell, line: line, position: position, playing: false)
        window.rootViewController?.view.layoutIfNeeded()
        cell.layoutIfNeeded()
        cell.updateTime(position, playing: playing, rate: 1, active: true)
        return Fixture(window: window, cell: cell, previousKeyWindow: previousKeyWindow)
    }

    private func descendants(_ view: UIView) -> [UIView] {
        view.subviews.flatMap { [$0] + descendants($0) }
    }

    private func highlightMask(in cell: MusicLyricCell) throws -> CAShapeLayer {
        try XCTUnwrap(descendants(cell).compactMap { ($0 as? UILabel)?.layer.mask as? CAShapeLayer }.first)
    }

    private func visibleWidth(_ mask: CAShapeLayer, presented: Bool = false) -> CGFloat {
        CATransaction.flush()
        let observed = presented ? (mask.presentation() ?? mask) : mask
        guard let path = observed.path, !path.isEmpty else { return 0 }
        // CA interpolation keeps zero-width rectangles for future fragments.
        // Only nonempty subpaths contribute to the actually revealed region.
        var points: [CGPoint] = []
        var visibleBounds = CGRect.null
        path.applyWithBlock { pointer in
            let element = pointer.pointee
            switch element.type {
            case .moveToPoint:
                points = [element.points[0]]
            case .addLineToPoint:
                points.append(element.points[0])
            case .addQuadCurveToPoint:
                points.append(contentsOf: [element.points[0], element.points[1]])
            case .addCurveToPoint:
                points.append(contentsOf: [element.points[0], element.points[1], element.points[2]])
            case .closeSubpath:
                let xs = points.map(\.x), ys = points.map(\.y)
                if let left = xs.min(), let right = xs.max(),
                   let top = ys.min(), let bottom = ys.max(), right > left, bottom > top {
                    visibleBounds = visibleBounds.union(
                        CGRect(x: left, y: top, width: right - left, height: bottom - top))
                }
            @unknown default:
                break
            }
        }
        return visibleBounds.isNull ? 0 : visibleBounds.width
    }

    private func elapse(_ seconds: Double) async {
        try? await Task.sleep(nanoseconds: UInt64(seconds * 1_000_000_000))
    }
}
#endif
