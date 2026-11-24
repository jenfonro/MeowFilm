import Foundation
import Combine
import MeowDebug
import MeowRuntime

@MainActor
extension FilmPlaybackModel {
    func publishDirectory(_ value: FilmPlaybackSnapshot) {
        let key = FilmPlaybackDirectoryKey(value)
        if value.browsing.error == "STALE_SOURCE" || value.browsing.error == "SOURCE_NOT_FOUND" {
            let revoked = directoryOrder.filter { $0.source == key.source }
            for key in revoked { directories.removeValue(forKey: key) }
            directoryOrder.removeAll { revoked.contains($0) }
        }
        let completed = !value.browsing.loading && !value.browsing.refreshing && value.browsing.error == nil &&
            value.entryResolution != "loading" && value.entryResolution != "error" &&
            (value.browsing.sourceID != nil || value.metadataError == nil)
        if completed {
            directories[key] = .init(value)
            if !directoryOrder.contains(key) { directoryOrder.append(key) }
        }
        let base = value.retainingLoadedPresentation(presentation)
        if selectedDirectory == nil || !directorySelectionExplicit { selectedDirectory = key }
        if selectedDirectory?.accepts(key) == true {
            selectedDirectory = key
            directoryLoading = value.browsing.loading
            directoryError = value.browsing.error
            if let error = value.browsing.error {
                directoryLoading = false
                presentation = base.loadingDirectory(key, error: error)
                return
            }
            if value.browsing.loading || value.browsing.refreshing ||
                value.browsing.sourceID == nil && value.metadataError != nil,
               let cached = directories[key] {
                directoryLoading = false
                presentation = value.browsing.loading || value.browsing.refreshing
                    ? base.refreshingDirectory(cached) : base.displaying(cached)
                return
            }
            presentation = base.displaying(.init(value))
        } else if let selectedDirectory {
            if !directoryLoading, directoryError == nil, let cached = directories[selectedDirectory] {
                presentation = base.displaying(cached)
            } else {
                presentation = base.loadingDirectory(selectedDirectory, error: directoryError)
            }
        }
    }

    func reconcileMetadataView() {
        guard !closed, !runtimeClosed, !restoringRead, let snapshot,
              let selected = snapshot.metadataSources.first(where: { $0.provider == metadataViewProvider }) ??
                snapshot.metadataSources.first,
              presentation?.metadataSourceID != selected.id else { return }
        browseMetadataDirectory(selected.id)
    }

    func loadedDirectory(sourceID: String, lineID: FilmPlaybackIdentifier?,
                         metadataSourceID: String?, season: Int?) -> FilmPlaybackDirectorySlice? {
        directories[.init(source: sourceID, line: lineID, metadata: metadataSourceID, season: season)]
    }

    private func selectDirectory(_ requested: FilmPlaybackDirectoryKey,
                                 matching: (FilmPlaybackDirectoryKey) -> Bool) -> Bool {
        directoryAdmission = UUID()
        directorySelectionExplicit = true
        if let key = directoryOrder.reversed().first(where: matching), let cached = directories[key] {
            selectedDirectory = key; directoryLoading = false; directoryError = nil
            directoryOrder.removeAll { $0 == key }; directoryOrder.append(key)
            if let snapshot { presentation = snapshot.displaying(cached) }
            return true
        }
        selectedDirectory = requested; directoryLoading = true; directoryError = nil
        if let snapshot { presentation = snapshot.loadingDirectory(requested) }
        return false
    }

    private func browsingFields(_ key: FilmPlaybackDirectoryKey) -> RuntimeObject {
        var fields: RuntimeObject = ["sourceId": key.source.map(RuntimeValue.string) ?? .null,
                                    "metadataSourceId": key.metadata.map(RuntimeValue.string) ?? .null]
        fields["lineId"] = key.line?.value
        fields["season"] = key.season.map { .number(Double($0)) }
        return fields
    }

    func observeSearchContext() {
        guard !closed, let context = searchContext else { return }
        if searchObservation == nil {
            searchObservation = context.session.$snapshot.sink { [weak self] _ in
                // @Published sends before assignment; build from the settled
                // context on the next actor turn, with one coalescing worker.
                self?.scheduleSearchUpdate()
            }
        }
        scheduleSearchUpdate()
    }

    private func scheduleSearchUpdate() {
        guard !closed, !runtimeClosed, restorePresentation == nil, allocatedSessionID != nil,
              searchUpdateTask == nil, let context = searchContext else { return }
        let generation = generation
        searchUpdateTask = Task { [weak self] in
            guard let self else { return }
            defer { self.searchUpdateTask = nil }
            while !Task.isCancelled, !self.closed, !self.runtimeClosed, self.generation == generation,
                  Int(context.snapshot.sequence) > self.searchRevision {
                guard let update = context.playbackUpdate(sources: self.openedSources) else { return }
                guard !Task.isCancelled, !self.closed, !self.runtimeClosed, self.generation == generation else { return }
                self.searchRevision = update.revision
                await self.enqueueAction("search.update", parameters: update.fields, ui: true)?.value
            }
        }
    }

    /// One serial RPC worker. UI intents coalesce by operation group; progress
    /// keeps only its latest queued value. Actual start/terminal facts stay ordered.
    @discardableResult
    public func send(_ action: String, parameters: RuntimeObject = [:]) -> Task<Void, Never>? {
        DebugRuntime.log(.filmPlayback, sub: "session", event: "action.\(action)", level: .debug, data: .object([
            "action": .string(action),
            "sessionId": .optional(sessionID?.uuidString.lowercased()),
        ]))
        if Self.mediaActions.contains(action) { cancelTemporaryRate() }
        if action == "stop", closed || closingTask != nil || openingTask != nil || reopeningTask != nil {
            let admission = UUID(); commandAdmission = admission
            let generation = generation
            return Task { [weak self] in
                guard let self, self.generation == generation, self.commandAdmission == admission else { return }
                await self.close()
            }
        }
        if openingTask == nil, restorePresentation != nil {
            restorePresentation = nil
            observeSearchContext()
        }
        let explicitPlayback = action == "play" || action == "select.target"
        if (explicitPlayback || runtimeClosed || openingTask != nil),
           closed || openingTask != nil || reopeningTask != nil {
            // Admit the explicit intent before drain/reopen can publish an older attempt.
            commandAdmission = UUID(); awaitingIntent = commandAdmission
            let opening = open()
            let reopeningAdmission = reopeningAdmission, commandAdmission = commandAdmission
            return Task { [weak self] in
                await opening?.value
                guard let self else { return }
                defer {
                    if self.commandAdmission == commandAdmission { self.awaitingIntent = nil }
                }
                guard !Task.isCancelled, !self.closed,
                      self.reopeningAdmission == reopeningAdmission, self.commandAdmission == commandAdmission else { return }
                guard self.allocatedSessionID != nil else { return }
                await self.enqueueAction(action, parameters: parameters, ui: true)?.value
            }
        }
        guard allocatedSessionID != nil, !closed else { return nil }
        return enqueueAction(action, parameters: parameters, ui: true)
    }

    /// System navigation belongs to the actual native attempt, including a
    /// legitimately retained PiP owner. It does not resurrect a released session.
    public func navigateFromSystem(forward: Bool) -> Bool {
        guard (!closed || retained), !released, allocatedSessionID != nil,
              let committed, committed.started, !committed.terminal, !committed.retired else { return false }
        enqueueAction(forward ? "next" : "previous", parameters: [:], ui: false)
        return true
    }

    @discardableResult
    func enqueueAction(_ action: String, parameters: RuntimeObject, ui: Bool) -> Task<Void, Never>? {
        var intent: UUID?
        if Self.mediaActions.contains(action) {
            commandAdmission = UUID(); awaitingIntent = commandAdmission
            intent = commandAdmission
            if action != "block" && action != "media.retry" {
                continuingFrom = (action == "pan" || action == "quality" || action == "switch")
                    ? committed?.attempt.id : nil
            }
            // Blocking or retry may reject without replacing anything.
            // Hold publication, not the exact pending media/receipt it can retain.
            if action != "block" && action != "media.retry" && action != "select.target" {
                blockedAttemptID = snapshot?.attempt?.id
                cancelPreparation()
            }
        }
        let browsing = action == "browse" || action.hasPrefix("browse.")
        let key = browsing ? "browse.selection" : intent == nil ? action : "media.intent"
        enqueue(.init(action: action, parameters: parameters, key: key, intent: intent, ui: ui,
                      directoryAdmission: browsing ? directoryAdmission : nil))
        return commandWorker
    }

    func enqueue(_ command: Command) {
        if let index = queuedCommands.firstIndex(where: { $0.key == command.key }) {
            queuedCommands[index] = command
        } else if !command.ui, let index = queuedCommands.firstIndex(where: { $0.ui }) {
            queuedCommands.insert(command, at: index)
        } else { queuedCommands.append(command) }
        if commandWorker == nil {
            commandWorker = Task { [weak self] in
                guard let self else { return }
                await self.runCommands()
            }
        }
    }

    private func runCommands() async {
        while !queuedCommands.isEmpty, let sessionID = allocatedSessionID, !released {
            let command = queuedCommands.removeFirst()
            if closed && (command.ui || !retained && command.intent != nil) { continue }
            if let intent = command.intent, command.key == "media.intent", intent != commandAdmission { continue }
            if let admission = command.directoryAdmission, admission != directoryAdmission { continue }
            do {
                if command.action == "stop" {
                    await preparationWorker?.value
                    await cancellingPreparation?.value
                    await stoppingMedia?.value
                    try await media.stop(sessionID: sessionID)
                    mediaStopped = true
                    // Native disposal may precede its terminal callback. Those
                    // captured facts must reach Node before stop replaces them.
                    try await drainFeedback(sessionID: sessionID)
                    try await flushProgress()
                    committed = nil; mediaState = FilmMediaState(); nativeStatus = nil
                }
                let previousCurrent = committed
                let value = try await callCommand(command.action, fields: command.parameters, sessionID: sessionID)
                // Close drains admitted RPCs without returning publication to a
                // departed UI. Native/system feedback still belongs to its owner.
                if command.ui, closed { continue }
                if let intent = command.intent, intent == awaitingIntent {
                    if command.action == "block" || command.action == "media.retry" {
                        continuingFrom = committed?.attempt.id
                    }
                    // The RPC snapshot is the latest command's admission barrier.
                    // Older SSE attempts cannot reacquire publication while it waits.
                    awaitingIntent = nil; blockedAttemptID = nil
                }
                accept(value, reconcileMedia: false)
                reconcilePreparation()
                if command.action == "play", command.intent == commandAdmission,
                   let previousCurrent, committed === previousCurrent,
                   previousCurrent.started, !previousCurrent.retired, !previousCurrent.terminal,
                   snapshot?.attempt == nil, snapshot?.current?.attemptID == previousCurrent.attempt.id {
                    media.resume(sessionID: sessionID, attemptID: previousCurrent.attempt.id, userInitiated: true)
                }
            } catch {
                if command.ui, closed { continue }
                if command.intent == awaitingIntent { awaitingIntent = nil }
                if let admission = command.directoryAdmission {
                    guard admission == directoryAdmission else { continue }
                    directoryLoading = false
                    directoryError = Self.failure(error).code
                    if directoryError == "STALE_SOURCE" || directoryError == "SOURCE_NOT_FOUND",
                       let source = selectedDirectory?.source {
                        directories = directories.filter { $0.key.source != source }
                        directoryOrder.removeAll { $0.source == source }
                    }
                    if let snapshot, let selectedDirectory {
                        let base = snapshot.retainingLoadedPresentation(presentation)
                        if command.action == "browse.refresh", let cached = directories[selectedDirectory] {
                            directoryError = nil
                            presentation = base.displaying(cached)
                        } else {
                            presentation = base.loadingDirectory(selectedDirectory, error: directoryError)
                        }
                    }
                }
                if !Self.cancelled(error), command.intent == nil || command.intent == commandAdmission,
                   !queuedCommands.contains(where: { $0.key == command.key }),
                   command.ui || (error as? FilmPlaybackError)?.code != "STALE_SESSION" {
                    actionError = Self.failure(error)
                }
                if command.action == "block" || command.action == "media.retry" || command.action == "select.target" {
                    reconcilePreparation()
                }
            }
        }
        commandWorker = nil
    }

    private func drainFeedback(sessionID: UUID) async throws {
        while let index = queuedCommands.firstIndex(where: { !$0.ui && $0.intent == nil }) {
            let command = queuedCommands.remove(at: index)
            do { accept(try await callCommand(command.action, fields: command.parameters, sessionID: sessionID)) }
            catch let error as FilmPlaybackError where error.code == "STALE_SESSION" {
                // A later operation can replace one of Node's two identities.
                // Never retarget the retired fact to the new attempt.
            }
        }
    }

    func callCommand(_ action: String, fields: RuntimeObject, sessionID: UUID) async throws -> FilmPlaybackSnapshot {
        var fields = fields
        fields["sessionId"] = .string(sessionID.uuidString.lowercased()); fields["action"] = .string(action)
        let value = try FilmPlaybackSnapshot(value: await transport.call("session.command", parameters: .object(fields)))
        guard value.sessionID == sessionID else { throw FilmPlaybackError.invalidResponse }
        return value
    }

    public func playbackFile(in snapshot: FilmPlaybackSnapshot) -> FilmPlaybackFile? {
        let selected = [
            snapshot.target.value["source"]?.filmObject?["candidateId"]?.filmText,
            snapshot.attempt?.candidate.id,
            snapshot.current?.candidate.id,
        ].compactMap { $0 }.first { id in
            snapshot.browsing.files.contains { $0.candidateID == id }
        }
        return selected.flatMap { id in snapshot.browsing.files.first { $0.candidateID == id } } ??
            snapshot.browsing.files.first(where: { $0.candidateID != nil })
    }

    public func canPlayPresentation(_ snapshot: FilmPlaybackSnapshot) -> Bool {
        guard canSelectFromPresentation, !directoryHasNoVideoSource else { return false }
        if snapshot.browsing.sourceID != nil {
            return playbackFile(in: snapshot) != nil
        }
        guard snapshot.metadataCache?.status != .stale,
              let selected = snapshot.metadataSources.first(where: { $0.id == snapshot.metadataSourceID }) else { return false }
        return selected.kind == .movie || selected.kind == .tv && !snapshot.episodes.isEmpty
    }

    public func playSelectionTarget() -> (target: FilmPlaybackTarget, metadataSourceID: String?)? {
        guard let presentation, canPlayPresentation(presentation) else { return nil }
        if presentation.browsing.sourceID != nil {
            guard let file = playbackFile(in: presentation) else { return nil }
            return (file.playbackTarget, presentation.metadataSourceID)
        }
        let target = presentation.episodes.first { $0.position == snapshot?.target.position }?.playbackTarget ??
            presentation.episodes.first?.playbackTarget ??
            presentation.metadataSources.first { $0.id == presentation.metadataSourceID }?.playbackTarget
        guard let target else { return nil }
        return (target, presentation.metadataSourceID)
    }

    @discardableResult
    public func selectTarget(_ target: FilmPlaybackTarget, metadataSourceID: String? = nil) -> Task<Void, Never>? {
        var fields: RuntimeObject = ["target": .object(target.value)]
        fields["metadataSourceId"] = metadataSourceID.map(RuntimeValue.string) ?? .null
        return send("select.target", parameters: fields)
    }
    public func browse(sourceID: String?, lineID: FilmPlaybackIdentifier? = nil) {
        let metadata = presentation?.metadataSourceID
        let season = presentation?.browsing.season
        let requested = FilmPlaybackDirectoryKey(source: sourceID, line: lineID, metadata: metadata, season: season)
        if selectDirectory(requested, matching: {
            $0.source == sourceID && $0.metadata == metadata && (lineID == nil || $0.line == lineID)
                && (lineID == nil || $0.season == season)
        }) { return }
        send("browse", parameters: browsingFields(requested))
    }
    public func browseAggregate(_ provider: FilmMetadataProvider) {
        guard snapshot?.metadataSources.contains(where: { $0.provider == provider }) == true else { return }
        selectedAggregateProvider = provider
        browse(sourceID: nil)
    }
    public func browseSeason(_ number: Int) {
        let key = FilmPlaybackDirectoryKey(source: presentation?.browsing.sourceID, line: presentation?.browsing.lineID,
            metadata: presentation?.metadataSourceID, season: number)
        if selectDirectory(key, matching: { $0 == key }) { return }
        send("browse.season", parameters: browsingFields(key))
    }
    @discardableResult
    public func refreshBrowsing() -> Task<Void, Never>? {
        actionError = nil
        directoryAdmission = UUID()
        if let key = selectedDirectory {
            directoryLoading = directories[key] == nil; directoryError = nil
            if let snapshot {
                let base = snapshot.retainingLoadedPresentation(presentation)
                if let cached = directories[key] { presentation = base.refreshingDirectory(cached) }
                else { presentation = base.loadingDirectory(key) }
            }
        }
        return send("browse.refresh", parameters: selectedDirectory.map(browsingFields) ?? [:])
    }
    @discardableResult
    public func retryMedia(attemptID: String? = nil) -> Task<Void, Never>? {
        guard let id = attemptID ?? snapshot?.retryMediaAttemptID else { return nil }
        actionError = nil
        return send("media.retry", parameters: ["attemptId": .string(id)])
    }
    public func browseMetadata(_ id: String) {
        guard !closed, let selected = snapshot?.metadataSources.first(where: { $0.id == id }) else { return }
        guard let playbackPreferences else {
            metadataViewProvider = selected.provider
            browseMetadataDirectory(id)
            return
        }
        let previous = metadataSelectionTask
        let admission = UUID(); metadataSelectionAdmission = admission
        let generation = generation
        metadataSelectionTask = Task { [weak self] in
            await previous?.value
            guard let self else { return }
            defer { if metadataSelectionAdmission == admission { metadataSelectionTask = nil } }
            guard !closed, self.generation == generation else { return }
            do { try await playbackPreferences.setMetadataViewProvider(selected.provider) }
            catch {
                if !closed, self.generation == generation, metadataSelectionAdmission == admission {
                    actionError = Self.failure(error)
                }
                return
            }
            guard !closed, self.generation == generation, metadataSelectionAdmission == admission else { return }
            metadataViewProvider = selected.provider
            browseMetadataDirectory(id)
        }
    }
    private func browseMetadataDirectory(_ id: String) {
        if presentation?.metadataSourceID == id { return }
        let source = presentation?.browsing.sourceID
        let key = FilmPlaybackDirectoryKey(source: source, line: presentation?.browsing.lineID,
            metadata: id, season: nil)
        if selectDirectory(key, matching: { $0.source == source && $0.metadata == id && $0.line == key.line }) { return }
        var fields = browsingFields(key)
        fields["keepSource"] = .bool(true)
        send("browse.metadata", parameters: fields)
    }
    public func selectPan(_ provider: FilmPanProvider) { send("pan", parameters: ["provider": .string(provider.rawValue)]) }
    public func selectQuality(_ quality: FilmResourceQuality) { send("quality", parameters: ["quality": .string(quality.rawValue)]) }
    public func setPreview(_ enabled: Bool) { send("preview", parameters: ["enabled": .bool(enabled)]) }
    public func setShortVideo(_ enabled: Bool) { send("shortvideo", parameters: ["enabled": .bool(enabled)]) }
}
