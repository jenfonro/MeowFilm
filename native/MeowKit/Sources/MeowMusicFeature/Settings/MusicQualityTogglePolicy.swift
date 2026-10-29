/// Mirrors b900c259 audio-quality.js / orderedSettings: a checked toggle is
/// locked when removing it would leave no effective quality. An enabled
/// spatial category with no enabled, recognized subtype is not effective.
enum MusicQualityTogglePolicy {
    static let categories = ["master", "spatial", "enhanced", "hires", "lossless", "high", "standard"]
    static let spatialTypes = ["dolby", "sky", "vivid", "dts_x", "sony360", "qq_atmos", "viper_atmos"]

    static func hasEnabledQuality(qualities: [String], spatial: [String]) -> Bool {
        qualities.contains {
            categories.contains($0) && ($0 != "spatial" || spatial.contains { spatialTypes.contains($0) })
        }
    }
    static func locksQuality(_ id: String, qualities: [String], spatial: [String]) -> Bool {
        qualities.contains(id) && !hasEnabledQuality(qualities: qualities.filter { $0 != id }, spatial: spatial)
    }
    static func locksSpatial(_ id: String, qualities: [String], spatial: [String]) -> Bool {
        spatial.contains(id) && !hasEnabledQuality(qualities: qualities, spatial: spatial.filter { $0 != id })
    }
    static func ordered(_ stored: [String], spatial: Bool) -> [String] {
        let allowed = spatial ? spatialTypes : categories
        var seen = Set<String>()
        return (stored + allowed).filter { allowed.contains($0) && seen.insert($0).inserted }
    }
}
