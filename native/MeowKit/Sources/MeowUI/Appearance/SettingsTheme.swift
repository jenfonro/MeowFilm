/// Settings controls use stable semantic colors. The surrounding window and
/// page header continue to use PageTheme's selected or song-derived palette.
public struct SettingsTheme: Equatable, Sendable {
    public let ink, muted, accent, onAccent, surface, border, separator: PageColor
    public let input, inputBorder, selected, danger, success, warning: PageColor

    public init(dark: Bool) {
        ink = .init(rgb: dark ? 0xf5f5f7 : 0x1c1c1e)
        muted = .init(rgb: dark ? 0xb8bac0 : 0x53565c)
        accent = .init(rgb: dark ? 0x75b5ff : 0x0065c2)
        onAccent = .init(rgb: dark ? 0x102339 : 0xffffff)
        surface = .init(rgb: dark ? 0x242426 : 0xffffff)
        border = .init(rgb: dark ? 0x62646b : 0xaeb4bf)
        separator = .init(rgb: dark ? 0x4a4a50 : 0xd7dadf)
        input = .init(rgb: dark ? 0x1b1b1e : 0xf2f3f5)
        inputBorder = .init(rgb: dark ? 0x858892 : 0x858b95)
        selected = .init(rgb: dark ? 0x33445a : 0xe7eef8)
        danger = .init(rgb: dark ? 0xff8f96 : 0xb4232c)
        success = .init(rgb: dark ? 0x7fd6a4 : 0x1f7046)
        warning = .init(rgb: dark ? 0xe8bb70 : 0x8a5600)
    }
}

public extension PageTheme {
    var settings: SettingsTheme { SettingsTheme(dark: dark) }
}

#if os(iOS)
import UIKit

@MainActor
public extension SettingsTheme {
    func apply(to field: UITextField) {
        field.textColor = UIColor(ink)
        field.tintColor = UIColor(accent)
        field.backgroundColor = UIColor(input)
        field.layer.cornerRadius = 6
        field.layer.borderWidth = 1
        field.layer.borderColor = UIColor(inputBorder).cgColor
        if let placeholder = field.placeholder {
            field.attributedPlaceholder = NSAttributedString(string: placeholder,
                attributes: [.foregroundColor: UIColor(muted)])
        }
    }

    func apply(to field: UITextView) {
        field.textColor = UIColor(ink)
        field.tintColor = UIColor(accent)
        field.backgroundColor = UIColor(input)
        field.layer.cornerRadius = 6
        field.layer.borderWidth = 1
        field.layer.borderColor = UIColor(inputBorder).cgColor
    }
}
#endif
