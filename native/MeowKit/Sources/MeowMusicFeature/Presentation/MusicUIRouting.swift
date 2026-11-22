#if os(iOS)

/// Typed UI actions supplied by product composition. There is no navigation
/// singleton, second product stack, event bus or reverse access to App internals.
@MainActor
public protocol MusicUIRouting: AnyObject {
    var primaryPlaybackPresentation: MusicMiniPlayerPresentation { get }
    func showSearch()
    func showSettings()
    func showPublicBrowse(_ request: MusicPublicBrowseRequest, title: String)
    func showPublicCollection(_ collection: MusicPublicBrowseCollection)
    func showLocalCollection(_ collection: MusicCollection)
    func showPlayer(_ destination: MusicPlaybackDestination)
    func selectChannel(_ channel: MusicPublicBrowseChannel)
}
#endif
