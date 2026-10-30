#if os(iOS) || os(macOS)
import Foundation
import Combine

struct MusicQualitySettingsSnapshot: Equatable {
    let revision: Int
    let qualityOrder, qualityEnabled, spatialOrder, spatialEnabled: [String]

    init(_ state: MusicPlaybackState) {
        revision = state.settingsRevision
        qualityOrder = MusicQualityTogglePolicy.ordered(state.qualityOrder, spatial: false)
        qualityEnabled = state.qualityEnabled
        spatialOrder = MusicQualityTogglePolicy.ordered(state.spatialOrder, spatial: true)
        spatialEnabled = state.spatialEnabled
    }
}

private struct MusicQualityOrderPreview {
    let spatial: Bool
    let values: [String]
}

@MainActor
final class MusicQualitySettingsState: ObservableObject {
    @Published private(set) var value: MusicQualitySettingsSnapshot
    @Published private(set) var available = false
    @Published private(set) var busy = false
    @Published private var movingOrder: MusicQualityOrderPreview?
    private let model: MusicViewModel
    private var active = false
    private var generation: UUID?
    private var work: Task<Void, Never>?

    init(model: MusicViewModel) {
        self.model = model
        value = MusicQualitySettingsSnapshot(model.state)
    }

    func start() {
        active = true
        reconcileAccess()
        reconcileSettings()
    }

    func reconcileAccess() {
        let next = active && model.isAvailable && !model.isClosed
        let access = model.accountAccessGeneration
        if generation != access || available != next {
            work?.cancel()
            if movingOrder != nil { movingOrder = nil }
        }
        generation = access
        if available != next { available = next }
        reconcileSettings()
    }

    func reconcileSettings() {
        let next = MusicQualitySettingsSnapshot(model.state)
        if value != next { value = next }
    }

    func enabled(_ id: String, spatial: Bool) -> Bool {
        (spatial ? value.spatialEnabled : value.qualityEnabled).contains(id)
    }

    func order(spatial: Bool) -> [String] {
        if let movingOrder, movingOrder.spatial == spatial { return movingOrder.values }
        return spatial ? value.spatialOrder : value.qualityOrder
    }

    func locked(_ id: String, spatial: Bool) -> Bool {
        spatial
            ? MusicQualityTogglePolicy.locksSpatial(id, qualities: value.qualityEnabled, spatial: value.spatialEnabled)
            : MusicQualityTogglePolicy.locksQuality(id, qualities: value.qualityEnabled, spatial: value.spatialEnabled)
    }

    func setEnabled(_ id: String, _ enabled: Bool, spatial: Bool) {
        guard enabled != self.enabled(id, spatial: spatial), enabled || !locked(id, spatial: spatial) else { return }
        var values = spatial ? value.spatialEnabled : value.qualityEnabled
        if enabled { values.append(id) }
        else { values.removeAll { $0 == id } }
        save(spatial ? .init(ifRevision: value.revision, spatialEnabled: values)
                     : .init(ifRevision: value.revision, qualityEnabled: values))
    }

    func reorder(_ values: [String], spatial: Bool) {
        let order = spatial ? value.spatialOrder : value.qualityOrder
        guard values != order, values.count == order.count, Set(values) == Set(order) else { return }
        save(spatial ? .init(ifRevision: value.revision, spatialOrder: values)
                     : .init(ifRevision: value.revision, qualityOrder: values),
             movingOrder: .init(spatial: spatial, values: values))
    }

    func close() {
        active = false
        available = false
        movingOrder = nil
        work?.cancel()
    }

    private func save(_ change: MusicSettingsChange, movingOrder: MusicQualityOrderPreview? = nil) {
        guard active, available, work == nil, model.isAvailable, !model.isClosed,
              generation == model.accountAccessGeneration else { return }
        let access = generation
        if let movingOrder { self.movingOrder = movingOrder }
        busy = true
        work = Task { [weak self] in
            guard let self else { return }
            defer {
                work = nil
                reconcileSettings()
                self.movingOrder = nil
                busy = false
            }
            guard !Task.isCancelled, active, available, access == model.accountAccessGeneration else { return }
            await model.updateSettings(change)
        }
    }

    deinit { work?.cancel() }
}
#endif
