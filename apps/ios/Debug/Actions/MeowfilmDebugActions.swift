import UIKit
import MeowContracts
import MeowDebug
import MeowUI
import MeowPlayback
import MeowMusicFeature
import MeowFilmFeature
import MeowNovelFeature

@MainActor
extension MeowfilmDebugNavigator {
    // MARK: - UI Actions

    func uiAction(_ body: [String: DebugJSONValue]) async throws -> DebugJSONValue {
        guard let action = body["action"]?.stringValue?.trimmingCharacters(in: .whitespacesAndNewlines),
              !action.isEmpty else {
            throw DebugAPIError.invalidArgument("Missing required string field 'action'")
        }
        let params = body["params"]?.objectValue ?? [:]
        let waitIdle = body["waitIdle"]?.boolValue ?? false
        let timeoutMs = body["timeoutMs"]?.intValue ?? 15000

        DebugRuntime.log(.uiAction, sub: "debug.action", event: action, data: .object(params))

        let result: DebugJSONValue
        switch action {
        case "dialog.action":
            result = try await performDialogAction(params)

        case "search.submit":
            guard let query = params["query"]?.stringValue?.trimmingCharacters(in: .whitespacesAndNewlines),
                  !query.isEmpty else {
                throw DebugAPIError.invalidArgument("Missing non-empty 'query' for search.submit")
            }
            var navParams: [String: DebugJSONValue] = ["query": .string(query)]
            if let t = params["target"] { navParams["target"] = t }
            if let tab = params["tab"] { navParams["tab"] = tab }
            result = try await navOpen(["target": .string("search"), "params": .object(navParams)])

        case "setting.set", "setting.action":
            if let active = activeBusinessController() as? any DebugPageActionable {
                do {
                    result = try await active.debugPerformAction(DebugActionRequest(action: action, params: params))
                } catch let err as DebugAPIError where err.code == .invalidArgument {
                    if let fallback = try await performGlobalSettingAction(action: action, params: params) {
                        result = fallback
                    } else {
                        throw err
                    }
                }
            } else if let fallback = try await performGlobalSettingAction(action: action, params: params) {
                result = fallback
            } else {
                throw DebugAPIError.invalidState("Current page does not support '\(action)'")
            }

        default:
            guard let active = activeBusinessController() as? any DebugPageActionable else {
                throw DebugAPIError.invalidState("Current page does not support ui/action '\(action)'")
            }
            result = try await active.debugPerformAction(DebugActionRequest(action: action, params: params))
        }

        if waitIdle {
            _ = try await uiWait(["condition": .string("idle"), "timeoutMs": .int(timeoutMs)])
        }

        return .object([
            "action": .string(action),
            "result": result,
            "state": uiState()
        ])
    }

    private func performDialogAction(_ params: [String: DebugJSONValue]) async throws -> DebugJSONValue {
        let dialogAction = params["action"]?.stringValue ?? "confirm"
        if let alert = activeDialogController() as? UIAlertController {
            let registered = DebugAlertActionRegistry.shared.actions(for: alert) ?? []
            let chosen: DebugAlertActionRegistry.RegisteredAction?
            switch dialogAction {
            case "cancel":
                chosen = registered.first(where: { $0.kind == "cancel" }) ?? registered.first
            case "confirm":
                chosen = registered.first(where: { $0.kind != "cancel" }) ?? registered.first
            case "dismiss":
                if alert.isBeingPresented {
                    await waitForTransitionIfNeeded(of: alert)
                }
                await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
                    alert.dismiss(animated: false) { continuation.resume() }
                }
                await waitForTransitionIfNeeded(of: alert, expectDismissed: true)
                return .object(["dismissed": .bool(true), "type": .string("UIAlertController")])
            case "button":
                if let idx = params["index"]?.intValue {
                    chosen = registered.first(where: { $0.index == idx })
                } else if let label = params["label"]?.stringValue {
                    chosen = registered.first(where: { $0.label == label })
                } else {
                    throw DebugAPIError.invalidArgument("Provide 'index' or 'label' for dialog button action")
                }
            default:
                throw DebugAPIError.invalidArgument("Unknown dialog action: \(dialogAction)")
            }
            if let chosen {
                if alert.isBeingPresented {
                    await waitForTransitionIfNeeded(of: alert)
                }
                chosen.handler()
                if alert.isBeingDismissed {
                    await waitForTransitionIfNeeded(of: alert, expectDismissed: true)
                } else if alert.presentingViewController != nil {
                    await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
                        alert.dismiss(animated: false) { continuation.resume() }
                    }
                    await waitForTransitionIfNeeded(of: alert, expectDismissed: true)
                }
                return .object([
                    "invoked": .string(chosen.label),
                    "index": .int(chosen.index),
                    "type": .string("UIAlertController")
                ])
            }
            throw DebugAPIError.invalidState("Alert action could not be resolved in DebugAlertActionRegistry")
        }

        if let dialogVC = activeDialogController(), let dialog = dialogVC as? any DebugDialogControllable {
            if dialogVC.isBeingPresented {
                await waitForTransitionIfNeeded(of: dialogVC)
            }
            let snapshot = dialog.debugDialogSnapshot()
            switch dialogAction {
            case "confirm":
                let label = try dialog.debugInvokeDialogAction(kind: "confirm", index: nil, label: nil)
                if dialogVC.isBeingDismissed {
                    await waitForTransitionIfNeeded(of: dialogVC, expectDismissed: true)
                }
                return .object(["invoked": .string(label), "type": .string(snapshot.typeName)])
            case "cancel":
                let label = try dialog.debugInvokeDialogAction(kind: "cancel", index: nil, label: nil)
                if dialogVC.isBeingDismissed {
                    await waitForTransitionIfNeeded(of: dialogVC, expectDismissed: true)
                } else if dialogVC.presentingViewController != nil {
                    await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
                        dialogVC.dismiss(animated: false) { continuation.resume() }
                    }
                    await waitForTransitionIfNeeded(of: dialogVC, expectDismissed: true)
                }
                return .object(["invoked": .string(label), "dismissed": .bool(true), "type": .string(snapshot.typeName)])
            case "dismiss":
                if let label = try? dialog.debugInvokeDialogAction(kind: "cancel", index: nil, label: nil) {
                    if dialogVC.isBeingDismissed {
                        await waitForTransitionIfNeeded(of: dialogVC, expectDismissed: true)
                    } else if dialogVC.presentingViewController != nil {
                        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
                            dialogVC.dismiss(animated: false) { continuation.resume() }
                        }
                        await waitForTransitionIfNeeded(of: dialogVC, expectDismissed: true)
                    }
                    return .object(["invoked": .string(label), "dismissed": .bool(true), "type": .string(snapshot.typeName)])
                }
                await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
                    dialogVC.dismiss(animated: false) { continuation.resume() }
                }
                await waitForTransitionIfNeeded(of: dialogVC, expectDismissed: true)
                return .object(["dismissed": .bool(true), "type": .string(snapshot.typeName)])
            case "button":
                let idx = params["index"]?.intValue
                let labelArg = params["label"]?.stringValue
                guard idx != nil || labelArg != nil else {
                    throw DebugAPIError.invalidArgument("Provide 'index' or 'label' for dialog button action")
                }
                let invoked = try dialog.debugInvokeDialogAction(kind: nil, index: idx, label: labelArg)
                if dialogVC.isBeingDismissed {
                    await waitForTransitionIfNeeded(of: dialogVC, expectDismissed: true)
                }
                var payload: [String: DebugJSONValue] = [
                    "invoked": .string(invoked),
                    "type": .string(snapshot.typeName)
                ]
                if let idx { payload["index"] = .int(idx) }
                return .object(payload)
            default:
                throw DebugAPIError.invalidArgument("Unknown dialog action: \(dialogAction)")
            }
        }

        if dialogAction == "dismiss", let sheet = presentedModalChain().last {
            if sheet.isBeingPresented {
                await waitForTransitionIfNeeded(of: sheet)
            }
            await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
                sheet.dismiss(animated: false) { continuation.resume() }
            }
            await waitForTransitionIfNeeded(of: sheet, expectDismissed: true)
            return .object(["dismissed": .bool(true), "type": .string(String(describing: type(of: sheet)))])
        }
        throw DebugAPIError.invalidState("No active dialog is currently presented")
    }

    private func waitForTransitionIfNeeded(of controller: UIViewController, expectDismissed: Bool = false) async {
        var spins = 0
        while spins < 75 {
            let transitioning = controller.isBeingPresented || controller.isBeingDismissed
            let stillPresented = expectDismissed && controller.presentingViewController != nil
            if !transitioning && !stillPresented { break }
            spins += 1
            try? await Task.sleep(nanoseconds: 20_000_000)
        }
    }

    private func performGlobalSettingAction(
        action: String,
        params: [String: DebugJSONValue]
    ) async throws -> DebugJSONValue? {
        guard let assembly else { return nil }
        let domain = params["domain"]?.stringValue ?? "general"
        if action == "setting.set" {
            guard let key = params["key"]?.stringValue, let val = params["value"] else {
                throw DebugAPIError.invalidArgument("setting.set requires 'key' and 'value'")
            }
            switch domain {
            case "general":
                switch key {
                case "colorMode":
                    guard let raw = val.stringValue, let mode = MusicColorMode(rawValue: raw) else {
                        throw DebugAPIError.invalidArgument("Invalid colorMode: \(val)")
                    }
                    let saved = await assembly.appearance.set(\.colorMode, mode)
                    return .object(["key": .string(key), "value": .string(mode.rawValue), "saved": .bool(saved)])
                case "lockLyric":
                    guard let b = val.boolValue else { throw DebugAPIError.invalidArgument("lockLyric must be bool") }
                    let saved = await assembly.appearance.set(\.lockLyric, b)
                    return .object(["key": .string(key), "value": .bool(b), "saved": .bool(saved)])
                case "playlistView":
                    guard let raw = val.stringValue, let v = MusicPlaylistAppearance(rawValue: raw) else {
                        throw DebugAPIError.invalidArgument("Invalid playlistView: \(val)")
                    }
                    let saved = await assembly.appearance.set(\.playlistView, v)
                    return .object(["key": .string(key), "value": .string(v.rawValue), "saved": .bool(saved)])
                default:
                    return nil
                }
            case "music":
                guard let music = assembly.music else { throw DebugAPIError.invalidState("Music service unavailable") }
                switch key {
                case "requestQuality":
                    guard let q = val.stringValue else { throw DebugAPIError.invalidArgument("requestQuality must be string") }
                    await music.updateSettings(.init(ifRevision: music.state.settingsRevision, requestQuality: q))
                    return .object(["key": .string(key), "value": .string(q)])
                case "lyricsEnabled":
                    guard let b = val.boolValue else { throw DebugAPIError.invalidArgument("lyricsEnabled must be bool") }
                    await music.updateSettings(.init(ifRevision: music.state.settingsRevision, lyricsEnabled: b))
                    return .object(["key": .string(key), "value": .bool(b)])
                case "showTranslation":
                    guard let b = val.boolValue else { throw DebugAPIError.invalidArgument("showTranslation must be bool") }
                    await music.updateSettings(.init(ifRevision: music.state.settingsRevision, showTranslation: b))
                    return .object(["key": .string(key), "value": .bool(b)])
                default:
                    return nil
                }
            default:
                return nil
            }
        }
        if action == "setting.action" {
            let cmd = params["command"]?.stringValue ?? ""
            if domain == "general" && cmd == "reset_user_data" {
                guard let appDelegate else { throw DebugAPIError.invalidState("AppDelegate unavailable") }
                try await appDelegate.requestUserDataReset()
                return .object(["requested": .bool(true)])
            }
            if domain == "novel" && cmd == "refresh_sources" {
                guard let novel = assembly.novel else { throw DebugAPIError.invalidState("Novel feature unavailable") }
                try await novel.refreshSources()
                return .object(["sourcesCount": .int(novel.sources.count)])
            }
            if domain == "music" && cmd == "clear_cache" {
                try await assembly.clearAllMusicCaches()
                return .object(["executed": .bool(true), "command": .string(cmd)])
            }
            if domain == "general" && cmd == "clear_artwork_cache" {
                URLCache.shared.removeAllCachedResponses()
                assembly.initializedFilmImagesForDiagnostics?.debugClearMemoryCache()
                assembly.musicImages.debugClearMemoryCache()
                assembly.novel?.images.debugClearMemoryCache()
                return .object(["executed": .bool(true), "command": .string(cmd)])
            }
        }
        return nil
    }

    // MARK: - Playback & Novel Direct Control

    func playbackState() -> DebugJSONValue {
        let playback = assembly?.playback
        let musicJSON = assembly?.music.flatMap { m in
            playback.map { m.debugPlaybackState(playback: $0) }
        } ?? .null
        let filmModel = assembly?.filmMediaPlayback ?? assembly?.filmPlayback
        let filmJSON = filmModel?.debugPlaybackState() ?? .null
        return .object([
            "activeOwner": .optional(playback?.snapshot.owner),
            "music": musicJSON,
            "film": filmJSON
        ])
    }

    func playbackCommand(_ body: [String: DebugJSONValue]) async throws -> DebugJSONValue {
        guard let domain = body["domain"]?.stringValue,
              let command = body["command"]?.stringValue else {
            throw DebugAPIError.invalidArgument("playback/command requires 'domain' and 'command'")
        }
        let params = body["params"]?.objectValue ?? [:]
        switch domain {
        case "music":
            guard let music = assembly?.music, let playback = assembly?.playback else {
                throw DebugAPIError.invalidState("Music playback is not ready")
            }
            let updated = try await music.debugPerformPlaybackCommand(command, params: params, playback: playback)
            return .object(["domain": .string("music"), "command": .string(command), "state": updated])
        case "film":
            guard let film = assembly?.filmMediaPlayback ?? assembly?.filmPlayback else {
                throw DebugAPIError.invalidState("No active film playback model")
            }
            let updated = try await film.debugPerformPlaybackCommand(command, params: params)
            return .object(["domain": .string("film"), "command": .string(command), "state": updated])
        default:
            throw DebugAPIError.invalidArgument("Unsupported playback domain: \(domain)")
        }
    }

    func novelCommand(_ body: [String: DebugJSONValue]) async throws -> DebugJSONValue {
        guard let command = body["command"]?.stringValue else {
            throw DebugAPIError.invalidArgument("novel/command requires 'command'")
        }
        let params = body["params"]?.objectValue ?? [:]
        guard let novel = assembly?.novel else {
            throw DebugAPIError.invalidState("Novel feature is not ready")
        }
        DebugRuntime.log(.novel, sub: "debug.command", event: command, data: .object(params))

        switch command {
        case "open_book":
            guard let bookId = params["bookId"]?.stringValue, !bookId.isEmpty else {
                throw DebugAPIError.invalidArgument("open_book requires 'bookId'")
            }
            return try await navOpen(["target": .string("novel.book"), "params": .object(["bookId": .string(bookId)])])

        case "start_reading":
            var p: [String: DebugJSONValue] = [:]
            if let bookId = params["bookId"] { p["bookId"] = bookId }
            return try await navOpen(["target": .string("novel.reader"), "params": .object(p)])

        case "select_chapter":
            guard let reader = activeBusinessController() as? NovelReaderViewController else {
                throw DebugAPIError.invalidState("NovelReaderViewController is not active")
            }
            guard let idx = params["index"]?.intValue else {
                throw DebugAPIError.invalidArgument("select_chapter requires numeric 'index'")
            }
            guard reader.session.chapters.indices.contains(idx) else {
                throw DebugAPIError.invalidArgument("Chapter index \(idx) out of range (0..<\(reader.session.chapters.count))")
            }
            reader.session.jump(idx, ratio: 0)
            return reader.debugContent(maxItems: 50, includeRaw: false).toJSON()

        case "next_chapter":
            guard let reader = activeBusinessController() as? NovelReaderViewController else {
                throw DebugAPIError.invalidState("NovelReaderViewController is not active")
            }
            let nextIdx = reader.session.chapterIndex + 1
            guard reader.session.chapters.indices.contains(nextIdx) else {
                throw DebugAPIError.invalidState("Already at the last chapter")
            }
            reader.session.jump(nextIdx, ratio: 0)
            return reader.debugContent(maxItems: 50, includeRaw: false).toJSON()

        case "prev_chapter":
            guard let reader = activeBusinessController() as? NovelReaderViewController else {
                throw DebugAPIError.invalidState("NovelReaderViewController is not active")
            }
            let prevIdx = reader.session.chapterIndex - 1
            guard reader.session.chapters.indices.contains(prevIdx) else {
                throw DebugAPIError.invalidState("Already at the first chapter")
            }
            reader.session.jump(prevIdx, ratio: 0)
            return reader.debugContent(maxItems: 50, includeRaw: false).toJSON()

        case "next_page":
            guard let reader = activeBusinessController() as? NovelReaderViewController else {
                throw DebugAPIError.invalidState("NovelReaderViewController is not active")
            }
            let moved = reader.debugNextPage()
            return .object(["moved": .bool(moved), "content": reader.debugContent(maxItems: 50, includeRaw: false).toJSON()])

        case "prev_page":
            guard let reader = activeBusinessController() as? NovelReaderViewController else {
                throw DebugAPIError.invalidState("NovelReaderViewController is not active")
            }
            let moved = reader.debugPrevPage()
            return .object(["moved": .bool(moved), "content": reader.debugContent(maxItems: 50, includeRaw: false).toJSON()])

        case "switch_source":
            guard let reader = activeBusinessController() as? NovelReaderViewController else {
                throw DebugAPIError.invalidState("NovelReaderViewController is not active")
            }
            let candidate: NovelBook
            if let bookId = params["bookId"]?.stringValue, let found = findNovelBook(bookId: bookId) {
                candidate = found
            } else if let sourceId = params["sourceId"]?.stringValue,
                      let entry = novel.local.entries.first(where: { $0.book.installationId == sourceId }) {
                candidate = entry.book
            } else if let sIdx = params["sourceIndex"]?.intValue,
                      novel.sources.indices.contains(sIdx) {
                let targetSource = novel.sources[sIdx]
                if let entry = novel.local.entries.first(where: { $0.book.installationId == targetSource.installationId }) {
                    candidate = entry.book
                } else {
                    throw DebugAPIError.notFound("No candidate book found for source index \(sIdx)")
                }
            } else {
                throw DebugAPIError.invalidArgument("Provide 'bookId', 'sourceId', or 'sourceIndex' for switch_source")
            }
            reader.session.switchSource(candidate)
            return reader.debugContent(maxItems: 50, includeRaw: false).toJSON()

        case "toggle_shelf":
            let targetBook: NovelBook
            if let bookId = params["bookId"]?.stringValue {
                guard let found = findNovelBook(bookId: bookId) else {
                    throw DebugAPIError.notFound("Novel book not found: \(bookId)")
                }
                targetBook = found
            } else if let detail = activeBusinessController() as? NovelDetailViewController {
                targetBook = detail.debugBook
            } else if let reader = activeBusinessController() as? NovelReaderViewController {
                targetBook = reader.session.book
            } else {
                throw DebugAPIError.invalidArgument("Provide 'bookId' or open a book for toggle_shelf")
            }
            let currentlyOnShelf = novel.local.entries.first(where: { $0.book.id == targetBook.id })?.onShelf ?? false
            let desired = params["present"]?.boolValue ?? !currentlyOnShelf
            try await novel.setShelf(targetBook, present: desired)
            return .object(["bookId": .string(targetBook.id), "onShelf": .bool(desired)])

        default:
            throw DebugAPIError.invalidArgument("Unsupported novel command: \(command)")
        }
    }

    // MARK: - Helper Utilities

    func resolveAccountPlatform(_ raw: String?, music: MusicViewModel) -> MusicAccountPlatform {
        if let raw = raw?.lowercased() {
            switch raw {
            case "wy", "netease": return .netease
            case "tx", "qq": return .qq
            case "kg", "kugou": return .kugou
            default: break
            }
        }
        return .netease
    }

    func parseBrowsePlatform(_ raw: String) -> MusicPublicBrowsePlatform? {
        switch raw.lowercased() {
        case "wy", "netease": return .netease
        case "tx", "qq": return .qq
        case "kg", "kugou": return .kugou
        case "kw", "kuwo": return .kuwo
        case "mg", "migu": return .migu
        default: return MusicPublicBrowsePlatform(rawValue: raw)
        }
    }

    func findNovelBook(bookId: String) -> NovelBook? {
        if let novel = assembly?.novel,
           let entry = novel.local.entries.first(where: { $0.book.id == bookId || $0.book.bookUrl == bookId }) {
            return entry.book
        }
        if let home = coordinator?.novelHome,
           let found = home.discovery.books.first(where: { $0.id == bookId || $0.bookUrl == bookId }) {
            return found
        }
        return nil
    }
}
