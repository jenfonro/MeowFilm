import Foundation
import Combine

/// Music channel selection and restoration only. Opening an app page is
/// an explicit application action, never a side effect of catalogue loading.
@MainActor public final class MusicChannelSelection: ObservableObject {
    @Published public private(set) var channel: MusicPublicBrowseChannel?
    private let defaults: UserDefaults
    private let lastChannelKey = "music.ui.lastChannel"
    public init(defaults: UserDefaults = .standard) { self.defaults = defaults }

    /// browse.js openChannel/rememberChannel: channels are a primary page,
    /// not pushed detail pages and not another product navigation stack.
    public func selectChannel(_ value: MusicPublicBrowseChannel) {
        channel = value
        defaults.set(value.identity, forKey: lastChannelKey)
    }
    public func restoreChannel(from catalogue: [MusicPublicBrowseChannel]) {
        if let channel {
            if let current = catalogue.first(where: { $0.platform == channel.platform &&
                ($0.id == channel.id || !channel.pageCode.isEmpty && $0.pageCode == channel.pageCode) }) {
                self.channel = current
            }
        } else if let key = defaults.string(forKey: lastChannelKey),
                  let current = catalogue.first(where: { $0.identity == key ||
                      !key.contains(":") && $0.platform == .netease && ($0.id == key || $0.pageCode == key) }) {
            // A late catalogue read must not switch the user's selected tab.
            channel = current
            defaults.set(current.identity, forKey: lastChannelKey)
        }
    }
}
