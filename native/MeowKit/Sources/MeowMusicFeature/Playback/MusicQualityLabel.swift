enum MusicQualityLabel {
    static func symbol(_ value: String) -> String {
        ["standard": "SD", "128k": "SD", "192k": "HQ", "high": "HQ", "320k": "HQ",
         "lossless": "SQ", "flac": "SQ", "hires": "Hi-Res", "flac24bit": "Hi-Res",
         "master": "MASTER", "spatial": "SPATIAL", "enhanced": "HQ+"][value] ?? ""
    }
    static func name(_ value: String) -> String {
        ["128k": "标准", "192k": "较高", "320k": "高品质", "flac": "无损", "flac24bit": "Hi-Res",
         "standard": "标准", "high": "高品质", "lossless": "无损", "hires": "Hi-Res",
         "master": "母带", "spatial": "空间音频", "enhanced": "臻品", "jyeffect": "高清环绕声",
         "dolby": "杜比", "sky": "天空", "vivid": "Audio Vivid", "dts_x": "DTS:X",
         "sony360": "360 Reality Audio", "qq_atmos": "臻品全景声", "viper_atmos": "蝰蛇全景声"][value] ?? value
    }
}
