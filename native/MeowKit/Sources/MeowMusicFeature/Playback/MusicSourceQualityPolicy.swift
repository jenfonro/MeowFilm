import Foundation

/// Saved quality ordering belongs to automatic playback. Inspection has a
/// stable order of its own and always reaches every supported quality.
public enum MusicSourceQualityPolicy {
    public static let qualities = ["128k", "192k", "320k", "flac", "flac24bit", "master", "jyeffect", "dolby", "sky", "vivid"]
    /// Manual source exploration order matching Windows MusicQualityPolicy.SourceTiers:
    /// high (320k, 192k) -> lossless -> hires -> master -> standard -> spatial -> enhanced.
    public static let inspectionQualities = ["320k", "192k", "flac", "flac24bit", "master", "128k",
                                             "dolby", "sky", "vivid", "jyeffect"]
    public static let groups = ["master", "spatial", "enhanced", "hires", "lossless", "high", "standard"]
    public static let spatialTypes = ["dolby", "dts_x", "sony360", "sky", "vivid", "qq_atmos", "viper_atmos"]

    public static func group(_ quality: String) -> String {
        switch quality {
        case "master", "jymaster", "qq_master", "viper_tape":
            return "master"
        case "spatial", "dolby", "dts_x", "sony360", "sky", "vivid", "qq_atmos", "viper_atmos",
             "atmos", "atmos_plus", "atmos_2":
            return "spatial"
        case "enhanced", "jyeffect", "qq_enhanced", "viper_clear":
            return "enhanced"
        case "hires", "flac24bit":
            return "hires"
        case "lossless", "flac":
            return "lossless"
        case "high", "higher", "exhigh", "320k", "192k":
            return "high"
        case "standard", "128k":
            return "standard"
        default:
            return quality
        }
    }

    public static func levels(for category: String, state: MusicPlaybackState) -> [String] {
        switch category {
        case "master": return ["master"]
        case "hires": return ["flac24bit"]
        case "lossless": return ["flac"]
        case "high": return ["320k", "192k"]
        case "standard": return ["128k"]
        case "enhanced": return ["jyeffect"]
        case "spatial":
            let spatialOrder = state.spatialOrder + state.spatialEnabled.filter { !state.spatialOrder.contains($0) }
            return spatialOrder.filter { qualities.contains($0) && state.spatialEnabled.contains($0) }
        default:
            return []
        }
    }

    /// Automatic order degrades strictly downward from `requestQuality` (or starts at the
    /// first enabled tier when `requestQuality == "auto"`). Requesting `192k` never upgrades to `320k`.
    public static func ordered(_ state: MusicPlaybackState, automatic: Bool) -> [String] {
        let order = state.qualityOrder + groups.filter { !state.qualityOrder.contains($0) }
        let request = state.requestQuality
        let requestGroup = group(request)
        let start = (!automatic || request == "auto") ? 0 : (order.firstIndex(of: requestGroup) ?? 0)
        var result: [String] = []
        for tier in order.dropFirst(start) where state.qualityEnabled.contains(tier) {
            let tierLevels = levels(for: tier, state: state)
            if automatic, request != "auto", tier == requestGroup, tierLevels.contains(request) {
                result += tierLevels.drop(while: { $0 != request })
            } else {
                result += tierLevels
            }
        }
        return result
    }
}
