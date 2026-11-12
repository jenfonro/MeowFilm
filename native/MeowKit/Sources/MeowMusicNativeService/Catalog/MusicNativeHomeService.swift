import Foundation
import MeowMusicFeature

struct MusicNativeHomeLoad {
    let id: UUID
    let source: MusicPublicBrowsePlatform
    let sourceRevision: UUID
    let binding: MusicNativeHomeBinding
    var nextCursor: String?
    var sections: [MusicNativePublicBrowseSection] = []
}

struct MusicNativeHomeReadResult: Sendable {
    let id: UUID
    let result: Result<MusicPublicBrowsePage, MusicServiceError>
}

struct MusicNativeHomeFirstRead {
    let load: MusicNativeHomeLoad
    let task: Task<MusicNativeHomeReadResult, Never>
    var completed = false
    var delivered = false
    var notificationHandled = false
    /// One notification may transfer a result to a replacement page waiter.
    var handoff = false
}

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
    /// Retires request caches and cursors. The single display snapshot retains
    /// its own source identity. Never call inside access/authorization perform.
    private func retireHomeSource() {
        homeSourceRevision = UUID()
        homeCache = nil; homeLoad = nil
        homeFirstRead?.task.cancel(); homeFirstRead = nil
        lastHomeRefreshObservation = nil
        cancelWork("musicHome")
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
            // Account observation cannot fall back to live binding acquisition.
            throw MusicServiceError.unsupported
        } else {
            let guest = anonymousHomeBinding(source)
            value = .init(scope: guest.scope, revision: guest.revision, authorization: guest.authorization)
        }
        try check(own)
        try value.authorization.check()
        if source == .netease { lastHomeScope = value.scope }
        return value
    }
    func currentHomeBinding(source: MusicPublicBrowsePlatform = .netease) async throws -> MusicNativeHomeBinding {
        try check(); try requireHomeSource(source)
        let own = epoch
        let binding: MusicNativeHomeBinding
        if source == .netease, let owner = accounts as? any MusicNativeHomeProviding {
            binding = try await work(label: "musicHome") { try await owner.nativeHomeBinding() }
        } else { binding = anonymousHomeBinding(source) }
        try check(own); try binding.authorization.check()
        if source == .netease { lastHomeScope = binding.scope }
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
            let previousObservation = lastHomeObservation
            let hasDisplayScope = accounts is any MusicNativeHomeDisplayProviding
            // Sample before subscribing; the owner's replay closes this gap.
            // The same subscription also receives explicit source changes;
            // account notifications only concern a NetEase home.
            let baseline: MusicNativeCacheScope?
            if hasDisplayScope { baseline = try? await currentHomeDisplayScope() }
            else { baseline = nil }
            let initialChange = previousScope != nil && (previousScope != baseline?.scope ||
                (previousObservation != nil && previousObservation != baseline))
            if let baseline { lastHomeObservation = baseline }
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
        do {
            let current = try await currentHomeDisplayScope()
            try check(own); try current.authorization.check()
            // Initial acquisition may have been busy while a page succeeded.
            // Its recorded scope/revision must still participate in detecting
            // a logout coalesced into the first notification.
            let recoveredChange = previous == nil && knownScope != nil &&
                knownScope != current.scope
            let changed = pending || recoveredChange || (previous != nil && previous != current)
            lastHomeObservation = current
            // Observation can retire a revoked request cache, never display
            // data. Its local revision is not the live binding's revision.
            if let cached = homeCache, cached.source == .netease,
               cached.scope != current.scope ||
                homeLoad?.binding.authorization.isActive == false { homeCache = nil }
            guard changed, homeSource?.platform != .kugou else { return (current, false) }
            if var read = homeFirstRead, read.load.source == .netease,
               read.load.sourceRevision == homeSourceRevision, !read.load.binding.loggedIn,
               read.load.binding.scope == current.scope, read.load.binding.authorization.isActive {
                // A late account notification must not start another guest
                // read after success or failure was already delivered. If the
                // old page is still waiting, transfer this same task once.
                if read.delivered || read.notificationHandled { return (current, false) }
                read.handoff = true; read.notificationHandled = true; homeFirstRead = read
            }
            lastHomeRefreshObservation = current
            return (current, true)
        } catch {
            guard epoch == own, !Task.isCancelled else { return (previous, false) }
            let failure = MusicServiceError.sanitized(error)
            let revoked = previous?.authorization.isActive == false
            let changed = pending || revoked ||
                failure.isAuthenticationFailure || [.staleAccess, .staleResult, .accessUnavailable].contains(failure)
            if changed, homeLoad?.binding.authorization.isActive == false,
               homeCache?.source == .netease { homeCache = nil }
            if changed, homeSource?.platform != .kugou,
               var read = homeFirstRead, read.load.source == .netease,
               read.load.sourceRevision == homeSourceRevision, !read.load.binding.loggedIn,
               read.load.binding.authorization.isActive {
                // A transient local observation failure can retire a waiter
                // too. Hand off the active guest task before yielding.
                if read.delivered || read.notificationHandled { return (previous, false) }
                read.handoff = true; read.notificationHandled = true; homeFirstRead = read
            }
            // Busy/network failures are not guest success. Keep the baseline
            // so the next real notification can still detect its replacement.
            return (previous, changed && homeSource?.platform != .kugou)
        }
    }
    public func restoredHome() async throws -> MusicPublicBrowsePage? {
        let source = try currentHomeSource(), own = epoch, revision = homeSourceRevision
        let display = try access.perform {
            if !homeDisplayPrepared {
                homeDisplayPrepared = true
                if let data = try? homeSnapshot?.read() {
                    homeDisplay = try? MusicNativeHomeSnapshot.restore(data)
                }
            }
            return homeDisplay
        }
        guard let display, display.source == source else { return nil }
        // Last-success snapshots deliberately carry no account binding. Retain
        // public albums/songs, but only a live scoped read may publish private
        // entrances, including a section's account-only navigation action.
        var page = display.page
        page.sections = page.sections.compactMap { section in
            var section = section
            section.items.removeAll { $0.content.requiresAccount }
            if let channel = section.channel,
               MusicNativePublicBrowseContent.channel(channel).requiresAccount { section.channel = nil }
            return section.items.isEmpty ? nil : section
        }
        let songs = try await publicSongMetadata(page)
        try checkHomeSource(source, revision: revision, epoch: own)
        return try access.perform {
            .init(sections: try convertedPublicSections(page, prefix: source.rawValue + ":home:", songs: songs))
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
        if destination == .home {
            if cursor == nil {
                let read = beginHomeFirstRead(source: source, sourceRevision: sourceRevision,
                                             binding: binding, refresh: refresh, epoch: own)
                return try await waitForHomeFirstRead(read, epoch: own)
            }
            guard let load = homeLoad, load.source == source, load.sourceRevision == sourceRevision,
                  load.binding == binding, load.nextCursor == cursor else { throw MusicServiceError.staleResult }
            do { return try await readHomeBatch(load, cursor: cursor, epoch: own) }
            catch {
                let failure = MusicServiceError.sanitized(error)
                if failure.isAuthenticationFailure, binding.loggedIn, source == .netease {
                    // A continuation caller must never append guest page one
                    // to its old account's tail. Transfer replacement to the
                    // observed first-page refresh instead.
                    _ = try await recoverHomeFirstRead(after: load, epoch: own, notify: true)
                }
                throw failure
            }
        }
        let name = source.rawValue + ":" + MusicNativePublicBrowseDiscovery.key(destination)
        let key = name + ":" + sourceRevision.uuidString + ":" + binding.revision.uuidString
        let position = try MusicNativePublicBrowseCursor.read(cursor, key: key, epoch: own, now: now())
        if source == .netease, case .channel(let channel, let tag) = destination, channel.kind == .vip {
            try musicRequire(cursor == nil && tag.isEmpty, .invalidRequest)
        }
        let value = try await readHomeContent(destination, position: position, source: source, binding: binding)
        try await checkHomeBinding(binding, epoch: own)
        try checkHomeSource(source, revision: sourceRevision, epoch: own)
        let songs = try await publicSongMetadata(value)
        try await checkHomeBinding(binding, epoch: own)
        try checkHomeSource(source, revision: sourceRevision, epoch: own)
        return try binding.authorization.perform {
            try finishHome(value, destination: destination, position: position, key: key,
                           source: source, epoch: own, binding: binding, songs: songs)
        }
    }

    private func readHomeContent(_ destination: MusicPublicBrowseDestination, position: MusicNativePublicBrowseCursor,
                                 source: MusicPublicBrowsePlatform, binding: MusicNativeHomeBinding) async throws
        -> MusicNativePublicBrowseDiscovery {
        let port = publicPlatforms, account = accounts as? any MusicNativeHomeProviding
        return try await work(label: "musicHome") {
            if source == .netease, case .channel(let channel, _) = destination, channel.kind == .vip {
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
    }

    private func beginHomeFirstRead(source: MusicPublicBrowsePlatform, sourceRevision: UUID,
                                    binding: MusicNativeHomeBinding, refresh: Bool, epoch own: UUID)
        -> MusicNativeHomeFirstRead {
        // Callers have fenced the source after acquiring this binding. A late
        // binding acquisition alone must never evict a newer source's cache.
        if let cached = homeCache, cached.source == source,
           cached.scope != binding.scope || cached.revision != binding.revision { homeCache = nil }
        if var read = homeFirstRead, read.load.source == source, read.load.sourceRevision == sourceRevision,
           read.load.binding == binding, !read.completed || read.handoff {
            read.handoff = false; homeFirstRead = read
            return read
        }
        let cached: MusicNativePublicBrowseDiscovery?
        if !refresh, let value = homeCache, value.source == source, value.scope == binding.scope,
           value.revision == binding.revision, value.expires > now() { cached = value.page }
        else { cached = nil }
        // Even the same account's first-page replay gets its own cursor chain.
        let load = MusicNativeHomeLoad(id: UUID(), source: source, sourceRevision: sourceRevision,
                                       binding: binding, nextCursor: nil)
        homeLoad = load
        let task = Task { await self.runHomeFirstRead(load, cached: cached, epoch: own) }
        let notified = !binding.loggedIn && lastHomeRefreshObservation?.scope == binding.scope &&
            lastHomeRefreshObservation?.authorization.isActive == true
        let read = MusicNativeHomeFirstRead(load: load, task: task, notificationHandled: notified)
        homeFirstRead = read
        return read
    }

    private func runHomeFirstRead(_ load: MusicNativeHomeLoad, cached: MusicNativePublicBrowseDiscovery?,
                                  epoch own: UUID) async -> MusicNativeHomeReadResult {
        defer {
            if homeFirstRead?.load.id == load.id { homeFirstRead?.completed = true }
        }
        do {
            let page = try await readHomeBatch(load, cursor: nil, cached: cached, epoch: own)
            return .init(id: load.id, result: .success(page))
        } catch {
            let failure = MusicServiceError.sanitized(error)
            if failure.isAuthenticationFailure, load.binding.loggedIn, load.source == .netease {
                do {
                    let guest = try await recoverHomeFirstRead(after: load, epoch: own, notify: false)
                    return await guest.task.value
                } catch {
                    return .init(id: load.id, result: .failure(MusicServiceError.sanitized(error)))
                }
            }
            return .init(id: load.id, result: .failure(failure))
        }
    }

    private func recoverHomeFirstRead(after failed: MusicNativeHomeLoad, epoch own: UUID, notify: Bool) async throws
        -> MusicNativeHomeFirstRead {
        let binding = try await currentHomeBinding(source: failed.source)
        try checkHomeSource(failed.source, revision: failed.sourceRevision, epoch: own)
        try musicRequire(!binding.loggedIn && binding != failed.binding, .staleResult)
        // Account observation may have already started this exact guest read
        // while binding acquisition suspended. Reuse success and failure once.
        let read: MusicNativeHomeFirstRead
        if let current = homeFirstRead, current.load.source == failed.source,
           current.load.sourceRevision == failed.sourceRevision, current.load.binding == binding {
            read = current
        } else {
            try musicRequire(homeLoad?.id == failed.id, .staleResult)
            read = beginHomeFirstRead(source: failed.source, sourceRevision: failed.sourceRevision,
                                      binding: binding, refresh: true, epoch: own)
        }
        if notify, !read.delivered, !read.notificationHandled, homeFirstRead?.load.id == read.load.id {
            homeFirstRead?.handoff = true; homeFirstRead?.notificationHandled = true
            for observer in homeObservers.values { observer.yield(()) }
        }
        return read
    }

    private func waitForHomeFirstRead(_ read: MusicNativeHomeFirstRead, epoch own: UUID) async throws -> MusicPublicBrowsePage {
        // The service owns the read; a UI retirement only releases this waiter.
        // Source/access retirement still cancels physical musicHome work.
        let waiter = MusicNativeOperation<MusicNativeHomeReadResult>()
        let task = Task { waiter.finish(.success(await read.task.value)) }
        defer { task.cancel() }
        let result = try await withTaskCancellationHandler {
            try await waiter.value()
        } onCancel: {
            task.cancel(); waiter.finish(.failure(MusicServiceError.cancelled))
        }
        try check(own)
        try musicRequire(homeLoad?.id == result.id, .staleResult)
        if case .success = result.result, let load = homeLoad {
            try await checkHomeBinding(load.binding, epoch: own)
            try checkHomeSource(load.source, revision: load.sourceRevision, epoch: own)
            try musicRequire(homeLoad?.id == result.id, .staleResult)
            return try load.binding.authorization.perform {
                if homeFirstRead?.load.id == result.id { homeFirstRead?.delivered = true }
                return try result.result.get()
            }
        }
        if homeFirstRead?.load.id == result.id { homeFirstRead?.delivered = true }
        return try result.result.get()
    }

    private func readHomeBatch(_ load: MusicNativeHomeLoad, cursor: String?,
                               cached: MusicNativePublicBrowseDiscovery? = nil, epoch own: UUID) async throws -> MusicPublicBrowsePage {
        try checkHomeSource(load.source, revision: load.sourceRevision, epoch: own)
        try musicRequire(homeLoad?.id == load.id, .staleResult)
        let key = load.source.rawValue + ":home:" + load.sourceRevision.uuidString + ":" +
            load.binding.revision.uuidString + ":" + load.id.uuidString
        let position = try MusicNativePublicBrowseCursor.read(cursor, key: key, epoch: own, now: now())
        let value: MusicNativePublicBrowseDiscovery
        if let cached { value = cached }
        else { value = try await readHomeContent(.home, position: position, source: load.source, binding: load.binding) }
        try await checkHomeBinding(load.binding, epoch: own)
        try checkHomeSource(load.source, revision: load.sourceRevision, epoch: own)
        let songs = try await publicSongMetadata(value)
        try await checkHomeBinding(load.binding, epoch: own)
        try checkHomeSource(load.source, revision: load.sourceRevision, epoch: own)
        guard var current = homeLoad, current.id == load.id,
              cursor == nil || current.nextCursor == cursor else { throw MusicServiceError.staleResult }
        return try load.binding.authorization.perform {
            // finishHome checks access itself; never nest that check inside
            // the non-recursive access commit lock.
            let page = try finishHome(value, destination: .home, position: position, key: key,
                                      source: load.source, epoch: own, binding: load.binding, songs: songs)
            return try access.perform {
                // Section errors are failed reads, never a new empty snapshot.
                guard value.sections.allSatisfy({ $0.error == nil }) else { return page }
                current.nextCursor = page.cursor
                current.sections = cursor == nil ? value.sections : mergeHomeSections(current.sections, value.sections)
                homeLoad = current
                // Reusing the real first-response cache is not a newer
                // successful network batch; keep the display, but seed only
                // this new chain's first page for its later continuations.
                if cached != nil { return page }
                let display = MusicNativePublicBrowseDiscovery(sections: current.sections)
                homeDisplay = (load.source, display); homeDisplayPrepared = true
                if cursor == nil {
                    homeCache = (load.source, load.binding.scope, load.binding.revision,
                                 now().addingTimeInterval(600), value)
                }
                if let data = try? MusicNativeHomeSnapshot.encode(display, source: load.source) {
                    // Serialization/storage failure keeps the previous durable
                    // success without discarding the usable in-memory display.
                    try? homeSnapshot?.write(data)
                }
                return page
            }
        }
    }

    private func mergeHomeSections(_ previous: [MusicNativePublicBrowseSection], _ next: [MusicNativePublicBrowseSection])
        -> [MusicNativePublicBrowseSection] {
        var result = previous
        for section in next {
            if let index = result.firstIndex(where: { $0.id == section.id && $0.layout == section.layout }) {
                var ids = Set(result[index].items.map(\.id))
                var merged = section
                merged.items = result[index].items + section.items.filter { ids.insert($0.id).inserted }
                result[index] = merged
            } else { result.append(section) }
        }
        return result
    }
    private func finishHome(_ value: MusicNativePublicBrowseDiscovery, destination: MusicPublicBrowseDestination,
                            position: MusicNativePublicBrowseCursor, key: String, source: MusicPublicBrowsePlatform, epoch own: UUID,
                            binding: MusicNativeHomeBinding, songs: [Data: MJ]) throws -> MusicPublicBrowsePage {
        try check(own)
        try musicRequire(value.sections.count <= 60 && value.sections.reduce(0, { $0 + $1.items.count }) <= 600, .limit)
        let prefix = source.rawValue + ":" + MusicNativePublicBrowseDiscovery.key(destination) + ":"
        let sections = try publicSections(value, prefix: prefix, binding: binding, songs: songs)
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
        result = try await hydrateHomeHeartbeat(result, account: account, binding: binding)
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

    func hydrateHomeHeartbeat(_ input: MusicNativePublicBrowseDiscovery,
                              account: any MusicNativeHomeProviding,
                              binding: MusicNativeHomeBinding) async throws -> MusicNativePublicBrowseDiscovery {
        func needsSeed(_ item: MusicNativePublicBrowseItem) -> Bool {
            if case .action(.heartbeat) = item.content { return true }
            return false
        }
        guard binding.loggedIn, input.sections.contains(where: {
            $0.items.contains(where: needsSeed) || $0.tabs.contains(where: { $0.items.contains(where: needsSeed) })
        }) else { return input }
        let raw = try MJ.parse(await account.nativeHomePayload(.heartbeatSeed, binding: binding),
                               maximum: 2 * 1024 * 1024)["song"]
        let id = raw == .null ? nil : try MusicPlatformProtocol.numericID(raw["id"])
        let track = raw == .null ? MJ.null : try MJ.parse(Self.browseWYSong(raw).data)
        try await account.checkNativeHomeBinding(binding)
        func hydrate(_ items: [MusicNativePublicBrowseItem]) -> [MusicNativePublicBrowseItem] {
            items.map { item in
                guard needsSeed(item) else { return item }
                var result = item
                guard let id else {
                    result.content = .unavailable
                    return result
                }
                result.content = .action(.heartbeatSong(songID: id))
                result.subtitle = [track["name"].string, track["singer"].string].filter { !$0.isEmpty }.joined(separator: " · ")
                result.artwork = MusicNativePublicBrowseSupport.artwork(track["img"])
                return result
            }
        }
        var result = input
        for index in result.sections.indices {
            result.sections[index].items = hydrate(result.sections[index].items)
            for tab in result.sections[index].tabs.indices {
                result.sections[index].tabs[tab].items = hydrate(result.sections[index].tabs[tab].items)
            }
        }
        return result
    }
}
