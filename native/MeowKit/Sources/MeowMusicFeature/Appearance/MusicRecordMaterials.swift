// SPDX-License-Identifier: GPL-3.0-only
import MeowUI

/// Record-only materials; not part of the shared page theme.
public struct MusicRecordMaterials: Equatable, Sendable {
    public let ring, tone, mist, reflect: PageColor
    public let discRotation: Double
    public init(palette: PageColorway = .lilac) {
        let values: [[UInt32]] = [
            [0x89749c8c, 0xb7a6ce70, 0xcedade30, 0xdfcfad32],
            [0xaf777880, 0xcf9c9573, 0xe2d1c938, 0xe5c97840],
            [0x61957482, 0x94bd9b70, 0xc1d7b632, 0xb6c67a42],
            [0x81879880, 0xb9bfbd6b, 0xc9d3da32, 0xd6c18c3b],
            [0xbd9e468c, 0xd4bd627a, 0xe0d8a93d, 0xc6b8d642],
            [0xa57b9a86, 0xc69bb278, 0xded0dc36, 0xd2ca9342],
            [0x73839d92, 0x9cacc47d, 0xc2d6d932, 0xc9c38b48],
            [0xb38a6f8a, 0xcfab9478, 0xded3c436, 0xc8b2d13b],
            [0x678c9a8c, 0x9ac0c675, 0xc8dfdb32, 0xc3bf8e42]
        ]
        let colors = values[PageColorway.allCases.firstIndex(of: palette)!].map { PageColor(rgba: $0) }
        ring = colors[0]; tone = colors[1]; mist = colors[2]; reflect = colors[3]
        discRotation = palette.hue - 262
    }
}
