import Foundation
import MeowMusicFeature

extension NativeMusicService: MusicHomeBrowsing {
    /// Implemented home/channel catalogue providers, in settings order.
    static let homeSources: [MusicPublicBrowsePlatform] = [.netease, .kugou]
    public func homeSourceSelection() throws -> MusicHomeSourceSelection {
        try check()
        // Every read reports the durable value, never a remembered guess.
        return homeSourceSelection(try loadHomeSource())
    }
    private func homeSourceSelection(_ value: (platform: MusicPublicBrowsePlatform, revision: Int)) -> MusicHomeSourceSelection {
        .init(selected: value.platform, revision: value.revision,
              available: Self.homeSources.filter(enabledPlatforms.contains))
    }
    public func selectHomeSource(_ source: MusicPublicBrowsePlatform, ifRevision: Int) throws -> MusicHomeSourceSelection {
        try check(); try requireHomeSource(source)
        let current = try loadHomeSource()
        try musicRequire(current.revision == ifRevision, .staleRevision)
        guard current.platform != source else { return homeSourceSelection(current) }
        let saved: (platform: MusicPublicBrowsePlatform, revision: Int) =
            try transaction(["catalogSource"], reportingCancellationAfterCommit: false) { document in
            let revision = document.catalogSource["revision"].int
            try musicRequire(revision == ifRevision, .staleRevision)
            try musicRequire(revision < 9_007_199_254_740_991, .limit)
            document.catalogSource = .obj([("schema", .n(1)), ("revision", .n(revision + 1)),
                                           ("platform", .string(source.rawValue))])
            return (platform: source, revision: revision + 1)
        }
        adoptHomeSource(saved)
        return homeSourceSelection(saved)
    }
    /// Reads and adopts the durable choice. A storage failure is reported; it
    /// never becomes a request against a default platform.
    func loadHomeSource() throws -> (platform: MusicPublicBrowsePlatform, revision: Int) {
        let value = try read().catalogSource
        guard let platform = MusicPublicBrowsePlatform(rawValue: value["platform"].string) else {
            throw MusicServiceError.corruptDocument
        }
        let result = (platform: platform, revision: value["revision"].int)
        adoptHomeSource(result)
        return result
    }
    private func adoptHomeSource(_ value: (platform: MusicPublicBrowsePlatform, revision: Int)) {
        let previous = homeSource
        homeSource = value
        if let previous, previous.platform != value.platform || previous.revision != value.revision { retireHomeSource() }
    }
    /// Retires the previous source's pages, cursors, snapshot and in-flight
    /// home reads. Never call inside access/authorization perform.
    private func retireHomeSource() {
        homeSourceRevision = UUID()
        homeCache = nil; lastHomeScope = nil
        cancelWork("musicHome")
        try? access.perform { try homeSnapshot?.clear() }
        for observer in homeObservers.values { observer.yield(()) }
    }
    func currentHomeSource() throws -> MusicPublicBrowsePlatform {
        try check()
        let source = try homeSource?.platform ?? loadHomeSource().platform
        try requireHomeSource(source)
        return source
    }
    func requireHomeSource(_ source: MusicPublicBrowsePlatform) throws {
        try requirePlatform(source.rawValue)
        try musicRequire(Self.homeSources.contains(source), .unsupported)
    }
    /// A result publishes only for the source and revision it was requested for.
    func checkHomeSource(_ source: MusicPublicBrowsePlatform, revision: UUID, epoch own: UUID) throws {
        try check(own); try requireHomeSource(source)
        try musicRequire(homeSource?.platform == source && homeSourceRevision == revision, .staleResult)
    }
    /// Each guest source owns a revocable binding; a NetEase account change
    /// never retires or reloads KuGou content.
    private func anonymousHomeBinding(_ source: MusicPublicBrowsePlatform) -> MusicNativeHomeBinding {
        if let value = anonymousHomeBindings[source] { return value }
        let value = MusicNativeHomeBinding(scope: source == .netease ? "netease:guest" : "kugou:guest",
                                           revision: UUID(), loggedIn: false)
        anonymousHomeBindings[source] = value
        return value
    }
    private func currentHomeDisplayScope(source: MusicPublicBrowsePlatform = .netease) async throws -> MusicNativeCacheScope {
        try check(); try requireHomeSource(source)
        let own = epoch
        let value: MusicNativeCacheScope
        if source == .netease, let owner = accounts as? any MusicNativeHomeDisplayProviding {
            // Local storage only. Do not enter work()/nativeHomeBinding():
            // those may verify/refresh credentials or wait on provider HTTP.
            value = try await owner.nativeHomeDisplayScope()
        } else if source == .netease, accounts is any MusicNativeHomeProviding {
            // Owners without display-scope support cannot serve this cached view.
            // A disk-cache miss is preferable to silently doing network work.
            throw MusicServiceError.unsupported
        } else {
            let guest = anonymousHomeBinding(source)
            value = .init(scope: guest.scope, revision: guest.revision, authorization: guest.authorization)
        }
        try check(own)
        guard source == .netease else { return value }
        return try value.authorization.perform {
            try access.perform {
                if let previous = lastHomeScope, previous != value.scope {
                    if homeCache?.source == .netease { homeCache = nil }
                    if homeSource?.platform != .kugou { try? homeSnapshot?.clear() }
                }
                lastHomeScope = value.scope
                return value
            }
        }
    }
    private func checkHomeDisplayScope(_ value: MusicNativeCacheScope, source: MusicPublicBrowsePlatform,
                                       epoch own: UUID) async throws {
        try check(own); try value.authorization.check()
        if source == .netease, let owner = accounts as? any MusicNativeHomeDisplayProviding {
            try await owner.checkNativeHomeDisplayScope(value)
        } else {
            guard let guest = anonymousHomeBindings[source], guest.scope == value.scope,
                  guest.revision == value.revision, guest.authorization === value.authorization else {
                throw MusicServiceError.staleResult
            }
        }
        try check(own); try value.authorization.check()
    }
    func currentHomeBinding(source: MusicPublicBrowsePlatform = .netease) async throws -> MusicNativeHomeBinding {
        try check(); try requireHomeSource(source)
        let own = epoch
        let binding: MusicNativeHomeBinding
        if source == .netease, let owner = accounts as? any MusicNativeHomeProviding {
            binding = try await work(label: "musicHome") { try await owner.nativeHomeBinding() }
        } else { binding = anonymousHomeBinding(source) }
        try check(own); try binding.authorization.check()
        if source == .netease {
            if let previous = lastHomeScope, previous != binding.scope {
                if homeCache?.source == .netease { homeCache = nil }
                if homeSource?.platform != .kugou { try? access.perform { try homeSnapshot?.clear() } }
            }
            lastHomeScope = binding.scope
        }
        return binding
    }
    func checkHomeBinding(_ binding: MusicNativeHomeBinding, epoch own: UUID) async throws {
        try check(own); try binding.authorization.check()
        if !anonymousHomeBindings.values.contains(binding) {
            guard let owner = accounts as? any MusicNativeHomeProviding else { throw MusicServiceError.staleResult }
            try await owner.checkNativeHomeBinding(binding)
        }
        try check(own); try binding.authorization.check()
    }
    public func homeChanges() async -> AsyncStream<Void> {
        guard !closed else { return AsyncStream { $0.finish() } }
        let own = epoch, id = UUID(), pair = AsyncStream<Void>.makeStream(bufferingPolicy: .bufferingNewest(1))
        let continuation = pair.continuation
        homeObservers[id] = continuation
        let task: Task<Void, Never>?
        if let owner = accounts as? any MusicNativeHomeProviding {
            let previousScope = lastHomeScope
            let previousRevision = homeCache?.source == .netease && homeCache?.expires == .distantPast
                ? homeCache?.revision : nil
            let hasDisplayScope = accounts is any MusicNativeHomeDisplayProviding
            // Sample before subscribing; the owner's replay closes this gap.
            // The same subscription also receives explicit source changes;
            // account notifications only concern a NetEase home.
            let baseline: MusicNativeCacheScope?
            if hasDisplayScope { baseline = try? await currentHomeDisplayScope() }
            else { baseline = nil }
            let initialChange = previousScope != nil && (previousScope != baseline?.scope ||
                (previousRevision != nil && previousRevision != baseline?.revision))
            let stream = await owner.nativeHomeChanges()
            task = Task { [weak self] in
                var previous = baseline, pending = initialChange
                for await _ in stream {
                    guard !Task.isCancelled, let self else { break }
                    if !hasDisplayScope {
                        if await self.homeFollowsNeteaseAccount() { continuation.yield(()) }
                        continue
                    }
                    let change = await self.homeAccountChange(previous: previous, pending: pending, epoch: own)
                    guard !Task.isCancelled else { break }
                    previous = change.binding; pending = false
                    if change.changed { continuation.yield(()) }
                }
                continuation.finish()
            }
        } else { task = nil }
        continuation.onTermination = { [weak self] _ in
            task?.cancel()
            Task { await self?.removeHomeObserver(id) }
        }
        if epoch != own || closed || Task.isCancelled {
            task?.cancel(); removeHomeObserver(id); continuation.finish()
        }
        return pair.stream
    }
    private func removeHomeObserver(_ id: UUID) { homeObservers.removeValue(forKey: id) }
    /// A source not yet read is treated as NetEase-scoped (conservative reload).
    private func homeFollowsNeteaseAccount() async -> Bool { homeSource?.platform != .kugou }
    private func homeAccountChange(previous: MusicNativeCacheScope?, pending: Bool, epoch own: UUID) async
        -> (binding: MusicNativeCacheScope?, changed: Bool) {
        guard epoch == own, available, !closed else { return (previous, false) }
        let knownScope = lastHomeScope
        let knownRevision = homeCache?.source == .netease && homeCache?.expires == .distantPast
            ? homeCache?.revision : nil
        do {
            let current = try await currentHomeDisplayScope()
            try check(own); try current.authorization.check()
            // Initial acquisition may have been busy while a page succeeded.
            // Its recorded scope/revision must still participate in detecting
            // a logout coalesced into the first notification.
            let recoveredChange = previous == nil && knownScope != nil &&
                (knownScope != current.scope || (knownRevision != nil && knownRevision != current.revision))
            let changed = pending || recoveredChange || (previous != nil && previous != current)
            // Display and live-request revisions are deliberately independent.
            // Fresh pages retain their live binding checks; a display change
            // cannot evict a newer network result for the same durable scope.
            if let cached = homeCache, cached.source == .netease,
               cached.scope != current.scope ||
                (cached.expires == .distantPast && cached.revision != current.revision) { homeCache = nil }
            return (current, changed && homeSource?.platform != .kugou)
        } catch {
            guard epoch == own, !Task.isCancelled else { return (previous, false) }
            let failure = MusicServiceError.sanitized(error)
            let revoked = previous?.authorization.isActive == false
            let changed = pending || revoked ||
                failure.isAuthenticationFailure || [.staleAccess, .staleResult, .accessUnavailable].contains(failure)
            if changed, let previous, let cached = homeCache, cached.source == .netease,
               cached.expires == .distantPast,
               cached.scope == previous.scope, cached.revision == previous.revision { homeCache = nil }
            // Busy/network failures are not guest success. Keep the baseline
            // so the next real notification can still detect its replacement.
            return (previous, changed && homeSource?.platform != .kugou)
        }
    }
    public func restoredHome() async throws -> MusicPublicBrowsePage? {
        let own = epoch, source = try currentHomeSource(), revision = homeSourceRevision
        let binding: MusicNativeCacheScope
        do { binding = try await currentHomeDisplayScope(source: source) }
        catch MusicServiceError.unsupported { return nil }
        try await checkHomeDisplayScope(binding, source: source, epoch: own)
        try checkHomeSource(source, revision: revision, epoch: own)
        return try binding.authorization.perform {
            try access.perform {
                guard let data = try? homeSnapshot?.read(),
                      let restored = try? MusicNativeHomeSnapshot.decode(data, source: source, scope: binding.scope) else { return nil }
                // Remember the restored revision for observer baseline merging,
                // but never let a disk snapshot masquerade as a fresh response.
                if homeCache == nil || homeCache?.source != source || homeCache?.scope != binding.scope || homeCache!.expires <= now() {
                    homeCache = (source, binding.scope, binding.revision, .distantPast, restored)
                }
                return .init(sections: try publicSections(restored, prefix: source.rawValue + ":home:"))
            }
        }
    }
    public func homePage(cursor: String?, refresh: Bool) async throws -> MusicPublicBrowsePage {
        try await scopedHomePage(.home, cursor: cursor, refresh: refresh)
    }
    func scopedHomePage(_ destination: MusicPublicBrowseDestination, cursor: String?, refresh: Bool) async throws -> MusicPublicBrowsePage {
        let own = epoch
        if case .channel(let channel, _) = destination { try requireHomeSource(channel.platform) }
        let source = try currentHomeSource(), sourceRevision = homeSourceRevision
        if case .channel(let channel, _) = destination {
            // A channel belongs to the directory it was published in.
            try musicRequire(channel.platform == source, .staleResult)
        }
        let binding = try await currentHomeBinding(source: source)
        try checkHomeSource(source, revision: sourceRevision, epoch: own)
        let name = source.rawValue + ":" + MusicNativePublicBrowseDiscovery.key(destination)
        let key = name + ":" + sourceRevision.uuidString + ":" + binding.revision.uuidString
        let position = try MusicNativePublicBrowseCursor.read(cursor, key: key, epoch: own, now: now())
        let isHome = destination == .home
        if isHome, cursor == nil, !refresh, let cached = homeCache,
           cached.source == source, cached.scope == binding.scope, cached.revision == binding.revision, cached.expires > now() {
            try await checkHomeBinding(binding, epoch: own)
            try checkHomeSource(source, revision: sourceRevision, epoch: own)
            return try binding.authorization.perform { try finishHome(cached.page, destination: destination,
                position: position, key: key, source: source, epoch: own, binding: binding) }
        }
        let port = publicPlatforms, account = accounts as? any MusicNativeHomeProviding
        do {
            let value = try await work(label: "musicHome") {
                if source == .netease, case .channel(let channel, let tag) = destination, channel.kind == .vip {
                    try musicRequire(cursor == nil && tag.isEmpty, .invalidRequest)
                    return try await port.browseVIP(account: account, binding: binding)
                }
                if source == .netease, let account, binding.loggedIn {
                    return try await port.browseAccountHome(destination, remote: position.remote,
                        account: account, binding: binding)
                }
                if source == .netease, case .channel(let channel, _) = destination,
                   channel.kind == .artistActivity { return .init() }
                return try await port.browseDiscovery(destination, source: source,
                    remote: position.remote, offset: position.offset)
            }
            try await checkHomeBinding(binding, epoch: own)
            try checkHomeSource(source, revision: sourceRevision, epoch: own)
            return try binding.authorization.perform {
                let page = try finishHome(value, destination: destination, position: position, key: key,
                    source: source, epoch: own, binding: binding)
                if isHome, cursor == nil {
                    homeCache = (source, binding.scope, binding.revision, now().addingTimeInterval(600), value)
                    if let data = try? MusicNativeHomeSnapshot.encode(value, source: source, scope: binding.scope) {
                        // Cache failure does not turn successful music content
                        // into an error or overwrite the previous usable file.
                        try? access.perform { try homeSnapshot?.write(data) }
                    }
                }
                return page
            }
        } catch {
            let failure = MusicServiceError.sanitized(error)
            if failure.isAuthenticationFailure || [.staleAccess, .staleResult, .accessUnavailable].contains(failure),
               epoch == own, homeSourceRevision == sourceRevision, let cached = homeCache,
               cached.source == source, cached.scope == binding.scope, cached.revision == binding.revision {
                homeCache = nil
            }
            // Revision invalidation is not account replacement: a newer
            // revision may already have saved a same-scope snapshot. Only
            // currentHomeBinding's observed scope change can retire that file.
            throw failure
        }
    }
    private func finishHome(_ value: MusicNativePublicBrowseDiscovery, destination: MusicPublicBrowseDestination,
                            position: MusicNativePublicBrowseCursor, key: String, source: MusicPublicBrowsePlatform, epoch own: UUID,
                            binding: MusicNativeHomeBinding) throws -> MusicPublicBrowsePage {
        try check(own)
        try musicRequire(value.sections.count <= 60 && value.sections.reduce(0, { $0 + $1.items.count }) <= 600, .limit)
        let prefix = source.rawValue + ":" + MusicNativePublicBrowseDiscovery.key(destination) + ":"
        let sections = try publicSections(value, prefix: prefix, binding: binding)
        let consumed = value.consumed ?? sections.reduce(0) { $0 + $1.items.count }
        let next = try value.next.map { remote in
            try musicRequire(consumed > 0 && remote != position.remote, .limit)
            return try position.encode(key: key, epoch: own, offset: position.offset + 1, remote: remote)
        }
        return .init(sections: sections, cursor: next, filters: value.filters, selectedFilter: value.selected,
                     notice: value.notice, consumed: value.consumed)
    }
}

extension MusicNativePlatforms {
    func browseAccountHome(_ destination: MusicPublicBrowseDestination, remote: MJ,
                           account: any MusicNativeHomeProviding, binding: MusicNativeHomeBinding) async throws -> MusicNativePublicBrowseDiscovery {
        let request: MusicNativeHomeRequest
        if case .channel(let channel, _) = destination, channel.kind == .artistActivity {
            request = .artistActivity(before: remote["before"] == .null ? nil : try remote["before"].text(18))
        } else {
            let continuation: MusicNativeHomeContinuation?
            if remote.fields.isEmpty { continuation = nil }
            else {
                continuation = try .init(cursor: remote["cursor"].string,
                    blockCodeOrderList: remote["blockOrder"] == .null ? nil : remote["blockOrder"].text(16_000, empty: true),
                    loadedPositionCodes: remote["loaded"].strings)
            }
            request = destination == .home ? .home(continuation: continuation) : .featured(continuation: continuation)
        }
        let root = try MJ.parse(await account.nativeHomePayload(request, binding: binding),
                                maximum: 2 * 1024 * 1024)
        var result: MusicNativePublicBrowseDiscovery
        if case .artistActivity = request { result = try MusicNativePublicBrowseParser.artistActivity(root) }
        else { result = try MusicNativePublicBrowseParser.blocks(root, previous: remote,
            isHome: destination == .home, loggedIn: binding.loggedIn) }
        if case .home(nil) = request, binding.loggedIn,
           !result.sections.contains(where: { $0.layout == .artistUpdates || $0.channel?.kind == .artistActivity }) {
            try await account.checkNativeHomeBinding(binding)
            let activity = try MJ.parse(await account.nativeHomePayload(.artistActivity(), binding: binding),
                                       maximum: 2 * 1024 * 1024)
            result.sections += try MusicNativePublicBrowseParser.artistActivity(activity).sections
        }
        result = try await browseEnrich(result, metadata: { request in
            switch request {
            case .songIDs(let ids): return try await account.nativeHomePayload(.songMetadata(ids: ids), binding: binding)
            case .playlistIDs(let ids): return try await account.nativeHomePayload(.playlistMetadata(ids: ids), binding: binding)
            }
        })
        try await account.checkNativeHomeBinding(binding)
        return result
    }
}
