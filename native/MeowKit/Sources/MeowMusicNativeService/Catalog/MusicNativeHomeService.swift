import Foundation
import MeowMusicFeature

extension NativeMusicService: MusicHomeBrowsing {
    public func homeSourceSelection() -> MusicHomeSourceSelection {
        .init(selected: selectedHomeSource, available: enabledPlatforms.contains(.netease) ? [.netease] : [])
    }
    public func selectHomeSource(_ source: MusicPublicBrowsePlatform) throws {
        try check()
        try musicRequire(homeSourceSelection().available.contains(source), .unsupported)
        guard selectedHomeSource != source else { return }
        selectedHomeSource = source; homeSourceRevision = UUID()
        homeCache = nil; lastHomeScope = nil
        cancelWork("musicHome")
        try? access.perform { try homeSnapshot?.clear() }
        for observer in homeObservers.values { observer.yield(()) }
    }
    func currentHomeSource() throws -> MusicPublicBrowsePlatform {
        try check()
        guard let source = selectedHomeSource else { throw MusicServiceError.unsupported }
        try requireHomeSource(source)
        return source
    }
    func requireHomeSource(_ source: MusicPublicBrowsePlatform) throws {
        try requirePlatform(source.rawValue)
        try musicRequire(source == .netease, .unsupported)
    }
    func checkHomeSource(_ source: MusicPublicBrowsePlatform, revision: UUID?, epoch own: UUID) throws {
        try check(own); try requireHomeSource(source)
        if let revision {
            try musicRequire(selectedHomeSource == source && homeSourceRevision == revision, .staleResult)
        }
    }
    private func currentHomeDisplayScope() async throws -> MusicNativeCacheScope {
        try check(); try requirePlatform("wy")
        let own = epoch
        let value: MusicNativeCacheScope
        if let owner = accounts as? any MusicNativeHomeDisplayProviding {
            // Local storage only. Do not enter work()/nativeHomeBinding():
            // those may verify/refresh credentials or wait on provider HTTP.
            value = try await owner.nativeHomeDisplayScope()
        } else if accounts is any MusicNativeHomeProviding {
            // Owners without display-scope support cannot serve this cached view.
            // A disk-cache miss is preferable to silently doing network work.
            throw MusicServiceError.unsupported
        } else {
            if anonymousHomeBinding == nil {
                anonymousHomeBinding = .init(scope: "netease:guest", revision: UUID(), loggedIn: false)
            }
            let guest = anonymousHomeBinding!
            value = .init(scope: guest.scope, revision: guest.revision, authorization: guest.authorization)
        }
        try check(own)
        return try value.authorization.perform {
            try access.perform {
                if let previous = lastHomeScope, previous != value.scope {
                    homeCache = nil; try? homeSnapshot?.clear()
                }
                lastHomeScope = value.scope
                return value
            }
        }
    }
    private func checkHomeDisplayScope(_ value: MusicNativeCacheScope, epoch own: UUID) async throws {
        try check(own); try value.authorization.check()
        if let owner = accounts as? any MusicNativeHomeDisplayProviding {
            try await owner.checkNativeHomeDisplayScope(value)
        } else {
            guard let guest = anonymousHomeBinding, guest.scope == value.scope,
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
        if let owner = accounts as? any MusicNativeHomeProviding {
            binding = try await work(label: "musicHome") { try await owner.nativeHomeBinding() }
        } else {
            if anonymousHomeBinding == nil {
                anonymousHomeBinding = .init(scope: "netease:guest", revision: UUID(), loggedIn: false)
            }
            binding = anonymousHomeBinding!
        }
        try check(own); try binding.authorization.check()
        if let previous = lastHomeScope, previous != binding.scope {
            homeCache = nil
            try? access.perform { try homeSnapshot?.clear() }
        }
        lastHomeScope = binding.scope
        return binding
    }
    func checkHomeBinding(_ binding: MusicNativeHomeBinding, epoch own: UUID) async throws {
        try check(own); try binding.authorization.check()
        if let owner = accounts as? any MusicNativeHomeProviding {
            try await owner.checkNativeHomeBinding(binding)
        } else {
            try musicRequire(anonymousHomeBinding == binding, .staleResult)
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
            let previousRevision = homeCache?.expires == .distantPast ? homeCache?.revision : nil
            let hasDisplayScope = accounts is any MusicNativeHomeDisplayProviding
            // Sample before subscribing; the owner's replay closes this gap.
            // The same subscription also receives explicit source changes.
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
                    if !hasDisplayScope { continuation.yield(()); continue }
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
    private func homeAccountChange(previous: MusicNativeCacheScope?, pending: Bool, epoch own: UUID) async
        -> (binding: MusicNativeCacheScope?, changed: Bool) {
        guard epoch == own, available, !closed else { return (previous, false) }
        let knownScope = lastHomeScope
        let knownRevision = homeCache?.expires == .distantPast ? homeCache?.revision : nil
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
            if let cached = homeCache,
               cached.scope != current.scope ||
                (cached.expires == .distantPast && cached.revision != current.revision) { homeCache = nil }
            return (current, changed)
        } catch {
            guard epoch == own, !Task.isCancelled else { return (previous, false) }
            let failure = MusicServiceError.sanitized(error)
            let revoked = previous?.authorization.isActive == false
            let changed = pending || revoked ||
                failure.isAuthenticationFailure || [.staleAccess, .staleResult, .accessUnavailable].contains(failure)
            if changed, let previous, let cached = homeCache,
               cached.expires == .distantPast,
               cached.scope == previous.scope, cached.revision == previous.revision { homeCache = nil }
            // Busy/network failures are not guest success. Keep the baseline
            // so the next real notification can still detect its replacement.
            return (previous, changed)
        }
    }
    public func restoredHome() async throws -> MusicPublicBrowsePage? {
        let own = epoch, source = try currentHomeSource(), revision = homeSourceRevision
        let binding: MusicNativeCacheScope
        do { binding = try await currentHomeDisplayScope() }
        catch MusicServiceError.unsupported { return nil }
        try await checkHomeDisplayScope(binding, epoch: own)
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
        let source: MusicPublicBrowsePlatform, sourceRevision: UUID?
        if case .channel(let channel, _) = destination {
            source = channel.platform; sourceRevision = nil
        } else { source = try currentHomeSource(); sourceRevision = homeSourceRevision }
        let binding = try await currentHomeBinding(source: source)
        try checkHomeSource(source, revision: sourceRevision, epoch: own)
        let name = source.rawValue + ":" + MusicNativePublicBrowseDiscovery.key(destination)
        let key = name + ":" + (sourceRevision?.uuidString ?? "") + ":" + binding.revision.uuidString
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
                if case .channel(let channel, let tag) = destination, channel.kind == .vip {
                    try musicRequire(cursor == nil && tag.isEmpty, .invalidRequest)
                    return try await port.browseVIP(account: account, binding: binding)
                }
                if let account, binding.loggedIn {
                    return try await port.browseAccountHome(destination, remote: position.remote,
                        account: account, binding: binding)
                }
                if case .channel(let channel, _) = destination, channel.kind == .artistActivity { return .init() }
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
               epoch == own, let cached = homeCache,
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
            try musicRequire(consumed > 0 && remote != position.remote && position.offset < 199, .limit)
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
                                maximum: 2 * 1024 * 1024, mode: .upstream)
        var result: MusicNativePublicBrowseDiscovery
        if case .artistActivity = request { result = try MusicNativePublicBrowseParser.artistActivity(root) }
        else { result = try MusicNativePublicBrowseParser.blocks(root, previous: remote,
            isHome: destination == .home, loggedIn: binding.loggedIn) }
        if case .home(nil) = request, binding.loggedIn,
           !result.sections.contains(where: { $0.layout == .artistUpdates || $0.channel?.kind == .artistActivity }) {
            try await account.checkNativeHomeBinding(binding)
            let activity = try MJ.parse(await account.nativeHomePayload(.artistActivity(), binding: binding),
                                       maximum: 2 * 1024 * 1024, mode: .upstream)
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
