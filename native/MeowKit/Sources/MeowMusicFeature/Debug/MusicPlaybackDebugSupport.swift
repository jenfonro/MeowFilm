#if os(iOS)
import UIKit
import MeowDebug
import MeowPlayback
import MeowUI

// MARK: - MusicViewModel Debug Playback Control & State

@MainActor
public extension MusicViewModel {
    func debugPlaybackState(playback: PlaybackController) -> DebugJSONValue {
        let currentEntry = current
        let currentIndex = currentEntry.flatMap { entry in queue.firstIndex(where: { $0.id == entry.id }) }
        let selection = activePlaybackSelection
        let isMusicOwner = playback.snapshot.owner == "music"
        let phaseString: String
        if !isMusicOwner {
            phaseString = "stopped"
        } else {
            switch playback.snapshot.phase {
            case .idle: phaseString = "stopped"
            case .preparing, .buffering: phaseString = "buffering"
            case .playing: phaseString = playback.isOutputPlaying || playback.wantsPlayback ? "playing" : "paused"
            case .paused: phaseString = "paused"
            case .ended: phaseString = "ended"
            case .failed: phaseString = "error"
            }
        }
        let lyricText: String? = currentLyricIndex.flatMap { idx in
            lyricLines.indices.contains(idx) ? lyricLines[idx].text : nil
        }
        let trackJSON: DebugJSONValue
        if let track = currentEntry?.track {
            trackJSON = .object([
                "occurrenceId": .string(currentEntry?.id ?? ""),
                "songId": .string(track.songId),
                "title": .string(track.title),
                "artist": .string(track.artist),
                "album": .string(track.album),
                "duration": .number(track.duration),
                "artworkUrl": .optional(track.artwork?.absoluteString)
            ])
        } else {
            trackJSON = .null
        }
        let sourceJSON: DebugJSONValue = selection.map { sel in
            .object([
                "installationId": .string(sel.installationId),
                "siteId": .string(sel.siteId),
                "sourceKey": .string(sel.sourceKey),
                "quality": .string(sel.quality)
            ])
        } ?? .null

        let playbackDebug = playback.debugSnapshot()
        let audioDebug = playbackDebug["audio"]
        return .object([
            "state": .string(phaseString),
            "track": trackJSON,
            "source": sourceJSON,
            "quality": .optional(selection?.quality ?? state.requestQuality),
            "rate": .number(isMusicOwner ? playback.effectiveRate : 1),
            "canSeek": .bool(isMusicOwner && (playbackDebug["canSeek"]?.boolValue == true)),
            "positionSec": .number(isMusicOwner ? playback.currentPosition : 0),
            "durationSec": .optional(duration ?? currentEntry?.track.duration),
            "bufferedSec": .number(isMusicOwner ? (audioDebug?["bufferedSeconds"]?.doubleValue ?? 0) : 0),
            "underrunCount": .int(isMusicOwner ? (audioDebug?["underrunCount"]?.intValue ?? 0) : 0),
            "inputRoute": .optional(isMusicOwner ? audioDebug?["inputRoute"]?.stringValue : nil),
            "mode": .string(state.mode.title),
            "modeRaw": .int(state.mode.rawValue),
            "queueIndex": .optional(currentIndex),
            "queueCount": .int(queue.count),
            "lyricLine": .optional(lyricText),
            "lyricCount": .int(lyricLines.count),
            "sourceTitle": .string(playbackSourceTitle),
            "isFMActive": .bool(isFMActive),
            "availableOptionsCount": .int(options.count),
            "lastError": .optional(error ?? (playback.snapshot.owner == "music" ? playback.snapshot.error : nil))
        ])
    }

    func debugPerformPlaybackCommand(
        _ command: String,
        params: [String: DebugJSONValue],
        playback: PlaybackController
    ) async throws -> DebugJSONValue {
        DebugRuntime.log(.musicPlayback, sub: "debug.command", event: command, data: .object(params))
        switch command {
        case "play":
            if playback.snapshot.owner == "music", playback.isOutputPlaying || playback.wantsPlayback {
                return debugPlaybackState(playback: playback)
            }
            if current == nil, let first = queue.first {
                await selectOccurrence(first.id)
            } else {
                await togglePlayback()
            }
        case "pause":
            if playback.snapshot.owner == "music", playback.wantsPlayback || playback.isOutputPlaying {
                await togglePlayback()
            }
        case "toggle":
            await togglePlayback()
        case "stop":
            stopForMediaHandoff()
        case "next":
            await next()
        case "previous", "prev":
            await previous()
        case "seek":
            guard let seconds = params["seconds"]?.doubleValue else {
                throw DebugAPIError.invalidArgument("Missing numeric 'seconds' for seek")
            }
            guard seek(to: seconds) else {
                throw DebugAPIError.invalidState("Music playback is not seekable right now")
            }
        case "mode":
            let mode: MusicPlaybackMode
            if let raw = params["mode"]?.intValue, let m = MusicPlaybackMode(rawValue: raw) {
                mode = m
            } else if let str = params["mode"]?.stringValue?.lowercased() {
                switch str {
                case "loop", "list", "列表循环": mode = .loop
                case "shuffle", "random", "随机播放": mode = .shuffle
                case "single", "repeat_one", "单曲循环": mode = .single
                default: throw DebugAPIError.invalidArgument("Unknown music playback mode: \(str)")
                }
            } else {
                throw DebugAPIError.invalidArgument("Missing 'mode' parameter")
            }
            await setMode(mode)
        case "play_queue_index":
            guard let index = params["index"]?.intValue else {
                throw DebugAPIError.invalidArgument("Missing 'index' for play_queue_index")
            }
            guard queue.indices.contains(index) else {
                throw DebugAPIError.invalidArgument("Queue index \(index) out of range (0..<\(queue.count))")
            }
            await selectOccurrence(queue[index].id)
        case "select_source":
            let chosen: MusicPlaybackOption
            if let idx = params["index"]?.intValue {
                guard options.indices.contains(idx) else {
                    throw DebugAPIError.invalidArgument("Source option index \(idx) out of range")
                }
                chosen = options[idx]
            } else if let id = params["id"]?.stringValue {
                guard let found = options.first(where: { $0.id == id || $0.sourceKey == id }) else {
                    throw DebugAPIError.notFound("Music source option not found: \(id)")
                }
                chosen = found
            } else {
                throw DebugAPIError.invalidArgument("Provide 'index' or 'id' for select_source")
            }
            let quality = params["quality"]?.stringValue ?? chosen.qualities.first ?? state.requestQuality
            let selection = MusicPlaybackSelection(
                installationId: chosen.installationId,
                generation: chosen.generation,
                siteId: chosen.siteId,
                sourceKey: chosen.sourceKey,
                quality: quality
            )
            await choose(selection)
        case "start_fm":
            let modeStr = params["mode"]?.stringValue ?? "DEFAULT"
            let fmMode = MusicHomeAction.FMMode(rawValue: modeStr) ?? .default
            let submode = params["submode"]?.stringValue ?? ""
            try await playHomeAction(.fm(mode: fmMode, submode: submode, entry: nil), title: "私人漫游", forceReload: false)
        default:
            throw DebugAPIError.invalidArgument("Unsupported music playback command: \(command)")
        }
        return debugPlaybackState(playback: playback)
    }

    func debugSettingsJSON(appearance: MusicAppearancePreferencesStore) -> DebugJSONValue {
        .object([
            "requestQuality": .string(state.requestQuality),
            "qualityEnabled": .array(state.qualityEnabled.map(DebugJSONValue.string)),
            "qualityOrder": .array(state.qualityOrder.map(DebugJSONValue.string)),
            "spatialEnabled": .array(state.spatialEnabled.map(DebugJSONValue.string)),
            "spatialOrder": .array(state.spatialOrder.map(DebugJSONValue.string)),
            "lyricsEnabled": .bool(state.lyricsEnabled),
            "showTranslation": .bool(state.showTranslation),
            "officialSources": .array(state.officialSources.map { .string($0.rawValue) }),
            "lockLyric": .bool(appearance.preferences.lockLyric),
            "colorMode": .string(appearance.preferences.colorMode.rawValue),
            "playlistView": .string(appearance.preferences.playlistView.rawValue),
            "mode": .string(state.mode.title),
            "platformsCount": .int(platforms.count),
            "collectionsCount": .int(collections.count),
            "scriptSourceError": .optional(scriptSourceError)
        ])
    }
}

// MARK: - MusicPlayerViewController

extension MusicPlayerViewController: DebugPageInspectable, DebugPageActionable {
    public var debugPageStatus: DebugPageStatus {
        DebugPageStatus(
            target: "music.player",
            moduleId: "music",
            route: "music/player",
            title: content.model.current?.track.title ?? "播放器",
            isBusy: content.model.isResolving,
            isError: content.model.error != nil,
            errorText: content.model.error
        )
    }

    public func debugContent(maxItems: Int, includeRaw: Bool) -> DebugContentSnapshot {
        let playerState = content.model.debugPlaybackState(playback: content.playback)
        return DebugContentSnapshot(
            target: "music.player",
            kind: "player",
            loading: content.model.isResolving,
            error: content.model.error,
            player: playerState,
            settings: .object([
                "lyricExpanded": .bool(isLyricExpanded)
            ])
        )
    }

    public func debugPerformAction(_ request: DebugActionRequest) async throws -> DebugJSONValue {
        switch request.action {
        case "lyric.toggle":
            let next = request.params["expanded"]?.boolValue ?? !isLyricExpanded
            setLyricExpanded(next)
            return .object(["lyricExpanded": .bool(isLyricExpanded)])
        case "tab.select":
            guard let idx = request.params["index"]?.intValue, idx == 0 || idx == 1 else {
                throw DebugAPIError.invalidArgument("MusicPlayerViewController tab.select index must be 0 (record) or 1 (lyrics)")
            }
            setLyricExpanded(idx == 1)
            return .object(["lyricExpanded": .bool(isLyricExpanded)])
        default:
            throw DebugAPIError.invalidArgument("Unsupported action '\(request.action)' on MusicPlayerViewController")
        }
    }
}

// MARK: - MusicQueueViewController

extension MusicQueueViewController: DebugPageInspectable, DebugPageActionable {
    public func debugSelectTab(_ tabId: String) {
        loadViewIfNeeded()
        switch tabId {
        case "current", "queue":
            pager.select("current", animated: false)
        case "songs", "history":
            pager.select("songs", animated: false)
        case "playlists", "archives":
            if let first = model.playedPlaylists.first {
                pager.select("history:" + first.id, animated: false)
            } else {
                pager.select("songs", animated: false)
            }
        default:
            pager.select(tabId, animated: false)
        }
    }

    public var debugPageStatus: DebugPageStatus {
        let target: String
        if selected == "songs" { target = "music.history" }
        else if selected.hasPrefix("history:") { target = "music.archives" }
        else { target = "music.queue" }
        return DebugPageStatus(
            target: target,
            moduleId: "music",
            route: "music/queue/\(selected)",
            title: "播放队列",
            isBusy: model.isChangingQueue,
            isError: model.error != nil,
            errorText: model.error
        )
    }

    public func debugContent(maxItems: Int, includeRaw: Bool) -> DebugContentSnapshot {
        let tabs: [DebugTabItem] = [
            DebugTabItem(group: "queue-tab", index: 0, id: "current", title: "当前播放", selected: selected == "current"),
            DebugTabItem(group: "queue-tab", index: 1, id: "songs", title: "历史单曲", selected: selected == "songs"),
            DebugTabItem(group: "queue-tab", index: 2, id: "playlists", title: "历史歌单", selected: selected.hasPrefix("history:"))
        ]
        let projectedRows: [DebugContentRow]
        if selected == "songs" {
            projectedRows = model.playedSongs.prefix(maxItems).enumerated().map { idx, row in
                DebugContentRow(
                    index: idx,
                    id: row.id,
                    title: row.track.title,
                    subtitle: "\(row.track.artist) · \(row.date)",
                    actions: ["play"]
                )
            }
        } else if selected.hasPrefix("history:") {
            projectedRows = model.playedPlaylists.prefix(maxItems).enumerated().map { idx, pl in
                DebugContentRow(
                    index: idx,
                    id: pl.id,
                    title: pl.title,
                    selected: selected == "history:" + pl.id,
                    actions: ["open", "play"]
                )
            }
        } else {
            projectedRows = entries.prefix(maxItems).enumerated().map { idx, entry in
                DebugContentRow(
                    index: idx,
                    id: entry.id,
                    title: entry.track.title,
                    subtitle: entry.track.artist,
                    selected: entry.id == model.current?.id,
                    actions: ["play", "delete"]
                )
            }
        }
        return DebugContentSnapshot(
            target: debugPageStatus.target,
            kind: "list",
            loading: model.isChangingQueue,
            error: model.error,
            tabs: tabs,
            rows: projectedRows
        )
    }

    public func debugPerformAction(_ request: DebugActionRequest) async throws -> DebugJSONValue {
        switch request.action {
        case "tab.select":
            if let id = request.params["id"]?.stringValue {
                debugSelectTab(id)
            } else if let idx = request.params["index"]?.intValue {
                let ids = ["current", "songs", "playlists"]
                guard ids.indices.contains(idx) else { throw DebugAPIError.invalidArgument("Queue tab index out of range") }
                debugSelectTab(ids[idx])
            } else {
                throw DebugAPIError.invalidArgument("Provide 'id' or 'index'")
            }
            return .object(["selected": .string(selected)])
        case "list.item.invoke":
            let verb = request.params["verb"]?.stringValue ?? "play"
            if selected == "songs" {
                guard let idx = request.params["index"]?.intValue, model.playedSongs.indices.contains(idx) else {
                    throw DebugAPIError.invalidArgument("Invalid played song index")
                }
                await model.playHistorySong(model.playedSongs[idx])
                return .object(["playedHistorySongIndex": .int(idx)])
            } else if selected.hasPrefix("history:") {
                guard let idx = request.params["index"]?.intValue, model.playedPlaylists.indices.contains(idx) else {
                    throw DebugAPIError.invalidArgument("Invalid played playlist index")
                }
                await model.restoreHistory(model.playedPlaylists[idx])
                return .object(["restoredPlaylistId": .string(model.playedPlaylists[idx].id)])
            } else {
                guard let idx = request.params["index"]?.intValue, entries.indices.contains(idx) else {
                    throw DebugAPIError.invalidArgument("Invalid queue index")
                }
                let entry = entries[idx]
                if verb == "delete" {
                    await model.removeOccurrence(entry.id)
                } else {
                    await model.selectOccurrence(entry.id)
                }
                return .object(["occurrenceId": .string(entry.id), "verb": .string(verb)])
            }
        default:
            throw DebugAPIError.invalidArgument("Unsupported action '\(request.action)' on MusicQueueViewController")
        }
    }
}


// MARK: - MusicFMSettingsViewController

extension MusicFMSettingsViewController: DebugPageInspectable, DebugPageActionable {
    public var debugPageStatus: DebugPageStatus {
        DebugPageStatus(
            target: "music.fm",
            moduleId: "music",
            route: "music/fm/settings",
            title: "私人漫游",
            isBusy: model.pendingChoice != nil,
            isError: model.error != nil,
            errorText: model.error
        )
    }
    public func debugContent(maxItems: Int, includeRaw: Bool) -> DebugContentSnapshot {
        let rows = (MusicFMModeChoice.main + MusicFMModeChoice.scenes).prefix(maxItems).enumerated().map { idx, choice in
            DebugContentRow(
                index: idx,
                id: choice.id,
                title: choice.title,
                selected: choice.matches(model.selection),
                actions: ["select"]
            )
        }
        return DebugContentSnapshot(
            target: "music.fm",
            kind: "list",
            loading: model.pendingChoice != nil,
            error: model.error,
            rows: rows
        )
    }
    public func debugPerformAction(_ request: DebugActionRequest) async throws -> DebugJSONValue {
        guard request.action == "list.item.invoke" || request.action == "section.item.invoke" else {
            throw DebugAPIError.invalidArgument("Unsupported action '\(request.action)' on MusicFMSettingsViewController")
        }
        let choices = MusicFMModeChoice.main + MusicFMModeChoice.scenes
        let choice: MusicFMModeChoice
        if let idx = (request.params["index"] ?? request.params["itemIndex"])?.intValue {
            guard choices.indices.contains(idx) else {
                throw DebugAPIError.invalidArgument("FM choice index \(idx) out of range (0..<\(choices.count))")
            }
            choice = choices[idx]
        } else if let id = (request.params["id"] ?? request.params["itemId"])?.stringValue {
            guard let found = choices.first(where: { $0.id == id || $0.submode == id || $0.title == id }) else {
                throw DebugAPIError.notFound("FM choice not found: \(id)")
            }
            choice = found
        } else {
            throw DebugAPIError.invalidArgument("Provide 'index' or 'id' for \(request.action)")
        }
        model.select(choice)
        return .object(["choiceId": .string(choice.id), "title": .string(choice.title)])
    }
}

#endif
