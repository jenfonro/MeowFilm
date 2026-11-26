import Foundation
import MeowDebug
import MeowRuntime

@MainActor
extension FilmPlaybackModel {
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
        }
        let explicitPlayback = action == "play" || action == "select.item"
        if (explicitPlayback || runtimeClosed || openingTask != nil),
           closed || openingTask != nil || reopeningTask != nil {
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
            // Do not block a prepared `prepareOnly` attempt when `play` promotes it to active pending!
            if action != "play" && action != "block" && action != "media.retry" && action != "select.item" {
                blockedAttemptID = snapshot?.attempt?.id
                cancelPreparation()
            }
        }
        let browsing = action.hasPrefix("browse.")
        let key = browsing ? "browse.selection" : intent == nil ? action : "media.intent"
        enqueue(.init(action: action, parameters: parameters, key: key, intent: intent, ui: ui))
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
            do {
                if command.action == "stop" {
                    await preparationWorker?.value
                    await cancellingPreparation?.value
                    await stoppingMedia?.value
                    try await media.stop(sessionID: sessionID)
                    mediaStopped = true
                    try await drainFeedback(sessionID: sessionID)
                    try await flushProgress()
                    committed = nil; mediaState = FilmMediaState(); nativeStatus = nil
                }
                let previousCurrent = committed
                let value = try await callCommand(command.action, fields: command.parameters, sessionID: sessionID)
                if command.ui, closed { continue }
                if let intent = command.intent, intent == awaitingIntent {
                    if command.action == "block" || command.action == "media.retry" {
                        continuingFrom = committed?.attempt.id
                    }
                    awaitingIntent = nil; blockedAttemptID = nil
                }
                accept(value, reconcileMedia: false)
                reconcilePreparation()
                if command.action == "play", command.intent == commandAdmission,
                   let previousCurrent, committed === previousCurrent,
                   previousCurrent.started, !previousCurrent.retired, !previousCurrent.terminal,
                   snapshot?.attempt == nil, snapshot?.current?.id == previousCurrent.attempt.id {
                    media.resume(sessionID: sessionID, attemptID: previousCurrent.attempt.id, userInitiated: true)
                }
            } catch {
                if command.ui, closed { continue }
                if command.intent == awaitingIntent { awaitingIntent = nil }
                if !Self.cancelled(error), command.intent == nil || command.intent == commandAdmission,
                   !queuedCommands.contains(where: { $0.key == command.key }),
                   command.ui || (error as? FilmPlaybackError)?.code != "STALE_SESSION" {
                    actionError = Self.failure(error)
                }
                if command.action == "block" || command.action == "media.retry" ||
                   command.action == "select.item" || command.action == "play" {
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
            catch let error as FilmPlaybackError where error.code == "STALE_SESSION" { }
        }
    }

    func callCommand(_ action: String, fields: RuntimeObject, sessionID: UUID) async throws -> FilmPlaybackSnapshot {
        var fields = fields
        fields["sessionId"] = .string(sessionID.uuidString.lowercased()); fields["action"] = .string(action)
        let value = try FilmPlaybackSnapshot(value: await transport.call("session.command", parameters: .object(fields)))
        guard value.sessionID == sessionID else { throw FilmPlaybackError.invalidResponse }
        return value
    }

    @discardableResult
    public func selectItem(_ itemID: String, prepareOnly: Bool = false) -> Task<Void, Never>? {
        var fields: RuntimeObject = ["itemId": .string(itemID)]
        if prepareOnly { fields["prepareOnly"] = .bool(true) }
        return send("select.item", parameters: fields)
    }

    public func browseSource(_ sourceID: String) {
        actionError = nil
        send("browse.source", parameters: ["sourceId": .string(sourceID)])
    }

    public func browseLine(_ lineID: String) {
        actionError = nil
        send("browse.line", parameters: ["lineId": .string(lineID)])
    }

    public func browseSeason(_ number: Int) {
        actionError = nil
        send("browse.season", parameters: ["season": .number(Double(number))])
    }

    @discardableResult
    public func loadMoreEpisodes() -> Task<Void, Never>? {
        actionError = nil
        return send("episodes.more")
    }

    @discardableResult
    public func loadMoreSources() -> Task<Void, Never>? {
        actionError = nil
        return send("search.more")
    }

    @discardableResult
    public func refreshBrowsing() -> Task<Void, Never>? {
        actionError = nil
        return send("browse.refresh")
    }

    @discardableResult
    public func retryMedia(attemptID: String? = nil) -> Task<Void, Never>? {
        guard let id = attemptID ?? snapshot?.retryMediaAttemptID else { return nil }
        actionError = nil
        return send("media.retry", parameters: ["attemptId": .string(id)])
    }

    public func selectPan(_ provider: FilmPanProvider) {
        send("pan", parameters: ["provider": .string(provider.rawValue)])
    }

    public func selectQuality(_ quality: FilmResourceQuality) {
        send("quality", parameters: ["quality": .string(quality.rawValue)])
    }

    public func setPreview(_ enabled: Bool) {
        send("preview", parameters: ["enabled": .bool(enabled)])
    }

    public func setShortVideo(_ enabled: Bool) {
        send("shortvideo", parameters: ["enabled": .bool(enabled)])
    }
}
