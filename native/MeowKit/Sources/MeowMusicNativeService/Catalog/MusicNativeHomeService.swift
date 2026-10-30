import Foundation
import MeowMusicFeature

extension NativeMusicService: MusicHomeBrowsing {
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
    func currentHomeBinding() async throws -> MusicNativeHomeBinding {
        try check(); try requirePlatform("wy")
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
        guard let owner = accounts as? any MusicNativeHomeProviding else {
            return AsyncStream { $0.finish() }
        }
        guard accounts is any MusicNativeHomeDisplayProviding else { return await owner.nativeHomeChanges() }
        let own = epoch, previousScope = lastHomeScope
        let previousRevision = homeCache?.expires == .distantPast ? homeCache?.revision : nil
        // Sample before subscribing, then re-read on EVERY notification. The
        // owner's replay closes this sampling/subscription gap, even if logout
        // replaces that replay in its one-element buffer.
        let baseline = try? await currentHomeDisplayScope()
        let initialChange = previousScope != nil && (previousScope != baseline?.scope ||
            (previousRevision != nil && previousRevision != baseline?.revision))
        let stream = await owner.nativeHomeChanges()
        return AsyncStream(bufferingPolicy: .bufferingNewest(1)) { continuation in
            let task = Task { [weak self] in
                var previous = baseline, pending = initialChange
                for await _ in stream {
                    guard !Task.isCancelled, let self else { break }
                    let change = await self.homeAccountChange(previous: previous, pending: pending, epoch: own)
                    guard !Task.isCancelled else { break }
                    previous = change.binding; pending = false
                    if change.changed { continuation.yield(()) }
                }
                continuation.finish()
            }
            continuation.onTermination = { _ in task.cancel() }
        }
    }
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
        let own = epoch
        let binding: MusicNativeCacheScope
        do { binding = try await currentHomeDisplayScope() }
        catch MusicServiceError.unsupported { return nil }
        try await checkHomeDisplayScope(binding, epoch: own)
        return try binding.authorization.perform {
            try access.perform {
                guard let data = try? homeSnapshot?.read(),
                      let restored = try? MusicNativeHomeSnapshot.decode(data, scope: binding.scope) else { return nil }
                // Remember the restored revision for observer baseline merging,
                // but never let a disk snapshot masquerade as a fresh response.
                if homeCache == nil || homeCache?.scope != binding.scope || homeCache!.expires <= now() {
                    homeCache = (binding.scope, binding.revision, .distantPast, restored)
                }
                return .init(sections: try publicSections(restored, prefix: "home:"))
            }
        }
    }
    public func homePage(cursor: String?, refresh: Bool) async throws -> MusicPublicBrowsePage {
        try await scopedHomePage(.home, cursor: cursor, refresh: refresh)
    }
    func scopedHomePage(_ destination: MusicPublicBrowseDestination, cursor: String?, refresh: Bool) async throws -> MusicPublicBrowsePage {
        let own = epoch, binding = try await currentHomeBinding()
        let name = MusicNativePublicBrowseDiscovery.key(destination)
        let key = name + ":" + binding.revision.uuidString
        let position = try MusicNativePublicBrowseCursor.read(cursor, key: key, epoch: own, now: now())
        let isHome = destination == .home
        if isHome, cursor == nil, !refresh, let cached = homeCache,
           cached.scope == binding.scope, cached.revision == binding.revision, cached.expires > now() {
            try await checkHomeBinding(binding, epoch: own)
            return try binding.authorization.perform { try finishHome(cached.page, destination: destination,
                position: position, key: key, epoch: own, binding: binding) }
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
                return try await port.browseDiscovery(destination, remote: position.remote, offset: position.offset)
            }
            try await checkHomeBinding(binding, epoch: own)
            return try binding.authorization.perform {
                let page = try finishHome(value, destination: destination, position: position, key: key, epoch: own, binding: binding)
                if isHome, cursor == nil {
                    homeCache = (binding.scope, binding.revision, now().addingTimeInterval(600), value)
                    if let data = try? MusicNativeHomeSnapshot.encode(value, scope: binding.scope) {
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
               cached.scope == binding.scope, cached.revision == binding.revision {
                homeCache = nil
            }
            // Revision invalidation is not account replacement: a newer
            // revision may already have saved a same-scope snapshot. Only
            // currentHomeBinding's observed scope change can retire that file.
            throw failure
        }
    }
    private func finishHome(_ value: MusicNativePublicBrowseDiscovery, destination: MusicPublicBrowseDestination,
                            position: MusicNativePublicBrowseCursor, key: String, epoch own: UUID,
                            binding: MusicNativeHomeBinding) throws -> MusicPublicBrowsePage {
        try check(own)
        try musicRequire(value.sections.count <= 60 && value.sections.reduce(0, { $0 + $1.items.count }) <= 600, .limit)
        let prefix = MusicNativePublicBrowseDiscovery.key(destination) + ":"
        let sections = try publicSections(value, prefix: prefix, binding: binding)
        let next = try value.next.map { remote in
            try musicRequire(remote != position.remote && position.offset < 199 && !sections.isEmpty, .limit)
            return try position.encode(key: key, epoch: own, offset: position.offset + 1, remote: remote)
        }
        return .init(sections: sections, cursor: next, filters: value.filters, selectedFilter: value.selected, notice: value.notice)
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
        else { result = try MusicNativePublicBrowseParser.blocks(root, previous: remote, isHome: destination == .home) }
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
