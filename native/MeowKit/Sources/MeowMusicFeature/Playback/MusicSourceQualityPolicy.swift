import Foundation

/// Saved quality ordering belongs to automatic playback. Inspection has a
/// stable order of its own and always reaches every supported quality.
public enum MusicSourceQualityPolicy {
    public static let qualities = ["128k", "192k", "320k", "flac", "flac24bit", "master", "jyeffect", "dolby", "sky", "vivid"]
    public static let inspectionQualities = ["flac", "flac24bit", "master", "320k", "192k", "128k",
                                            "jyeffect", "dolby", "sky", "vivid"]
    public static let groups = ["master", "spatial", "enhanced", "hires", "lossless", "high", "standard"]
    public static func group(_ quality: String) -> String {
        ["128k": "standard", "192k": "high", "320k": "high", "flac": "lossless", "flac24bit": "hires",
         "master": "master", "jyeffect": "enhanced", "dolby": "spatial", "sky": "spatial", "vivid": "spatial"][quality] ?? quality
    }
    public static func ordered(_ state: MusicPlaybackState, automatic: Bool) -> [String] {
        let order = state.qualityOrder + groups.filter { !state.qualityOrder.contains($0) }
        let start = automatic ? state.qualityOrder.firstIndex(of: group(state.requestQuality)) ?? 0 : 0
        let spatial = state.spatialOrder + ["dolby", "sky", "vivid"].filter { !state.spatialOrder.contains($0) }
        var result: [String] = []
        for tier in order.dropFirst(start) where state.qualityEnabled.contains(tier) {
            let values = tier == "spatial" ? spatial.filter { qualities.contains($0) && state.spatialEnabled.contains($0) }
                : qualities.filter { group($0) == tier }.reversed().map { $0 }
            result += values
        }
        if automatic, let i = result.firstIndex(of: state.requestQuality) { result.insert(result.remove(at: i), at: 0) }
        return result
    }
}
