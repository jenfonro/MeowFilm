import Foundation
import Combine
import MeowRuntime

@MainActor
public final class NovelReadingSession: ObservableObject {
    typealias Transform = @Sendable (NovelContent, NovelBook, NovelSource, [NovelRule], String) async throws -> NovelContent
    private struct ProcessingInput: Equatable {
        let rules: [NovelRule]
        let conversion: String
    }
    private enum InitialLoad {
        case start
        case chapter(book: NovelBook, source: NovelSource, chapters: [NovelChapter], index: Int)
    }
    private struct PaidContext {
        var book: NovelBook
        var chapter: NovelChapter
        let source: NovelSource
    }
    public let id = UUID()
    public let feature: NovelFeature
    @Published public private(set) var book: NovelBook
    @Published public private(set) var chapters: [NovelChapter] = []
    @Published public private(set) var content: NovelContent?
    @Published public private(set) var chapterIndex = 0
    @Published public private(set) var anchor: NovelAnchor?
    @Published public private(set) var loading = false
    @Published public var error: String?
    @Published public var automatic = false
    @Published public private(set) var sourceSelection: (book: NovelBook, chapters: [NovelChapter], source: NovelSource)?
    @Published public private(set) var sourceConfirmation: NovelBook?
    public private(set) var closed = false
    private var original: NovelContent?
    private var bookIdentity: String?
    private var initialLoad: InitialLoad?
    private var paidContext: PaidContext?
    private var activeSource: NovelSource?
    private let transform: Transform
    private var appliedInput: ProcessingInput?
    private var contentGeneration: UUID?
    private var suspended = false
    private var processingInput: ProcessingInput { .init(rules: feature.local.rules, conversion: feature.local.settings.conversion) }
    private var generation = UUID()
    private var pending: Task<Void, Never>?
    private var running: [UUID: Task<Void, Never>] = [:]
    private var imageTasks: [UUID: Task<Void, Never>] = [:]
    private var saveDebounce: Task<Void, Never>?
    private var saving: Task<Void, Never>?
    private var closeTask: Task<Void, Never>?
    private var lastDisplayedAnchor: NovelAnchor?
    private var positionDirty = false
    var contentSource: NovelSource? { activeSource }
    init(feature: NovelFeature, book: NovelBook, transform: @escaping Transform) {
        self.feature = feature; self.book = book; self.transform = transform
    }
    public func start() {
        guard !closed else { return }
        initialLoad = .start
        run { session, token in
            let local = try await session.feature.retain(session.book)
            try session.check(token)
            let source = try session.feature.source(for: local)
            let enriched = try await session.feature.sourcesClient.detail(local, source: source)
            let catalog = try await session.feature.sourcesClient.chapters(enriched, source: source)
            let chapters = catalog.chapters
            try session.check(token, source: source)
            let saved = try await session.feature.retainWithIdentity(catalog.book)
            try session.check(token, source: source)
            guard !chapters.isEmpty else { throw NovelError.noChapter }
            let restored = session.feature.local.entries.first(where: { $0.id == local.id })?.anchor
            let index: Int
            if let restored {
                let matched = try await session.feature.rules.match(chapters, anchor: restored)
                try session.check(token, source: source)
                guard let matched else {
                    session.initialLoad = nil
                    session.anchor = restored; session.sourceSelection = (saved.book, chapters, source); return
                }
                index = matched
            } else {
                index = 0
            }
            try await session.load(index, book: saved.book, identity: saved.identity, source: source,
                                   chapters: chapters, restore: restored, token: token)
        }
    }
    public func jump(_ index: Int, ratio: Double = 0, continuingAutomatic: Bool = false) {
        guard chapters.indices.contains(index), let identity = bookIdentity else { return }
        initialLoad = nil
        run(resumeAutomatic: continuingAutomatic) { session, token in
            let chapter = session.chapters[index]
            let anchor = NovelAnchor(chapterUrl: chapter.chapterUrl, chapterTitle: chapter.name, chapterIndex: index, blockIndex: -1, ratio: ratio)
            let source = try session.feature.source(for: session.book)
            var target = session.book, targetIdentity = identity
            if let paid = session.acceptedPurchase(for: target, source: source) {
                if paid.chapter.chapterUrl == chapter.chapterUrl {
                    try await session.refreshPurchasedChapter(paid, restore: anchor, token: token)
                    return
                }
                target = paid.book
                targetIdentity = try await session.feature.rules.identity(target)
                try session.check(token, source: source)
            }
            try await session.load(index, book: target, identity: targetIdentity, source: source,
                                   chapters: session.chapters, restore: anchor, token: token)
        }
    }
    private func load(_ index: Int, book readingBook: NovelBook, identity: String, source: NovelSource,
                      chapters: [NovelChapter], restore: NovelAnchor?, token: UUID,
                      requiresProcessedContent: Bool = false) async throws {
        guard chapters.indices.contains(index) else { throw NovelError.noChapter }
        let chapter = chapters[index]
        let response = try await feature.sourcesClient.content(readingBook, source: source, chapter: chapter,
            nextChapterURL: chapters.indices.contains(index + 1) ? chapters[index + 1].chapterUrl : nil)
        let raw = response.content
        try check(token, source: source)
        var updated = chapters, target = readingBook
        updated[index] = response.chapter; target.variable = response.chapter.variable
        if var paid = acceptedPurchase(for: target, source: source) {
            paid.book = target
            if paid.chapter.chapterUrl == chapter.chapterUrl { paid.chapter = response.chapter }
            else { paid.chapter.variable = target.variable }
            paidContext = paid
        }
        await flush()
        try check(token, source: source)
        let processed: NovelContent
        let input: ProcessingInput?
        do {
            let result = try await transformLatest(raw, book: target, source: source, token: token)
            processed = result.content; input = result.input
        }
        catch {
            try check(token, source: source)
            if requiresProcessedContent { throw error }
            self.error = error.localizedDescription; processed = raw; input = nil
        }
        try check(token, source: source)
        let previous = activeSource?.id == source.id && anchor?.chapterUrl == chapter.chapterUrl ? content : nil
        if let paid = acceptedPurchase(for: target, source: source), paid.chapter.chapterUrl == chapter.chapterUrl, input != nil {
            paidContext = nil
        }
        bookIdentity = identity; book = target
        self.chapters = updated; chapterIndex = index
        activeSource = source; original = raw; content = processed
        appliedInput = input; contentGeneration = token
        var resolved = restore ?? .init(chapterUrl: chapter.chapterUrl, chapterTitle: chapter.name, chapterIndex: index)
        // A shortened catalog must finish restoring against the actual chapter.
        if resolved.chapterUrl != chapter.chapterUrl {
            resolved.blockIndex = -1; resolved.utf16Offset = 0
        }
        resolved = NovelAnchorResolver.resolve(resolved, previous: previous ?? processed, next: processed) ?? resolved
        resolved.chapterUrl = chapter.chapterUrl; resolved.chapterTitle = chapter.name; resolved.chapterIndex = index
        anchor = resolved; lastDisplayedAnchor = nil; positionDirty = false
        initialLoad = nil
    }
    public func display(_ value: NovelAnchor) {
        guard !closed, !loading, let content, content.blocks.contains(where: { $0.type == "image" || !($0.text ?? "").isEmpty }),
              chapters.indices.contains(chapterIndex), value.chapterUrl == chapters[chapterIndex].chapterUrl else { return }
        guard lastDisplayedAnchor != value else { return }
        anchor = value; lastDisplayedAnchor = value; positionDirty = true
        if saveDebounce == nil {
            saveDebounce = Task { [weak self] in
                do { try await Task.sleep(nanoseconds: 700_000_000) } catch { return }
                guard let self else { return }; saveDebounce = nil; savePosition()
            }
        }
    }
    private func savePosition() {
        guard positionDirty, let anchor, let identity = bookIdentity, lastDisplayedAnchor != nil else { return }
        positionDirty = false
        let previous = saving, library = feature.library, book = book
        // Accepted writes outlive a page task's cancellation and drain in order.
        saving = Task { [weak self] in
            await previous?.value
            do {
                try await library.record(book, identity: identity, anchor: anchor)
                try await self?.feature.reloadLocal()
            }
            catch {
                if self?.anchor == anchor { self?.positionDirty = true }
                let message = "阅读进度保存失败：" + error.localizedDescription
                self?.error = message
                if self?.closed == true { self?.feature.error = message }
            }
        }
    }
    public func refreshCatalog() {
        initialLoad = nil
        run { session, token in
            let source = try session.feature.source(for: session.book)
            let catalog: NovelCatalog
            if let paid = session.acceptedPurchase(for: session.book, source: source) {
                let refreshed = try await session.refreshPurchaseCatalog(paid, token: token)
                catalog = refreshed.catalog
            } else {
                catalog = try await session.feature.sourcesClient.chapters(session.book, source: source)
            }
            let chapters = catalog.chapters
            try session.check(token, source: source)
            guard !chapters.isEmpty else { throw NovelError.noChapter }
            let target: Int
            if let anchor = session.anchor {
                let matched = try await session.feature.rules.match(chapters, anchor: anchor)
                try session.check(token, source: source)
                guard let matched else { session.sourceSelection = (catalog.book, chapters, source); return }
                target = matched
            } else {
                target = 0
            }
            let identity = try await session.feature.rules.identity(catalog.book)
            try session.check(token, source: source)
            try await session.load(target, book: catalog.book, identity: identity, source: source,
                                   chapters: chapters, restore: session.anchor, token: token, requiresProcessedContent: true)
        }
    }
    public func switchSource(_ candidate: NovelBook) {
        switchSource(candidate, confirmedUnknown: false)
    }
    public func cancelSourceConfirmation() { sourceConfirmation = nil }
    public func confirmUnknownSource() {
        guard let candidate = sourceConfirmation else { return }
        sourceConfirmation = nil; switchSource(candidate, confirmedUnknown: true)
    }
    private func switchSource(_ candidate: NovelBook, confirmedUnknown: Bool) {
        initialLoad = nil
        sourceConfirmation = nil
        run { session, token in
            let source = try session.feature.source(for: candidate)
            var target = try await session.feature.sourcesClient.detail(candidate, source: source)
            let currentIdentity = try await session.feature.rules.identityDetails(session.book)
            let targetIdentity = try await session.feature.rules.identityDetails(target)
            try session.check(token, source: source)
            guard currentIdentity.name == targetIdentity.name,
                  currentIdentity.author.isEmpty || targetIdentity.author.isEmpty || currentIdentity.author == targetIdentity.author else { throw NovelError.differentBook }
            if !confirmedUnknown, currentIdentity.author.isEmpty || targetIdentity.author.isEmpty {
                session.sourceConfirmation = target; return
            }
            target.id = session.book.id
            let catalog = try await session.feature.sourcesClient.chapters(target, source: source)
            target = catalog.book
            let chapters = catalog.chapters
            try session.check(token, source: source)
            guard !chapters.isEmpty else { throw NovelError.noChapter }
            let current = session.anchor ?? .init(chapterUrl: "", chapterTitle: "", chapterIndex: session.chapterIndex)
            if let index = try await session.feature.rules.match(chapters, anchor: current) {
                try await session.commitSource(target, source: source, chapters: chapters, index: index, token: token)
            } else {
                try session.check(token, source: source)
                session.sourceSelection = (target, chapters, source)
            }
        }
    }
    public func selectSourceChapter(_ index: Int) {
        guard let selection = sourceSelection, selection.chapters.indices.contains(index) else { return }
        sourceSelection = nil
        guard feature.accepts(selection.source) else {
            error = "当前书源已更新或停用，请重新载入"; return
        }
        loadSelectedChapter(selection.book, source: selection.source, chapters: selection.chapters, index: index)
    }
    private func loadSelectedChapter(_ book: NovelBook, source: NovelSource, chapters: [NovelChapter], index: Int) {
        if original == nil { initialLoad = .chapter(book: book, source: source, chapters: chapters, index: index) }
        run { session, token in
            try await session.commitSource(book, source: source, chapters: chapters, index: index, token: token)
        }
    }
    public func cancelSourceSelection() { sourceSelection = nil }
    func ownImageTask(_ task: Task<Void, Never>, id: UUID) {
        guard !closed else { task.cancel(); return }
        imageTasks[id] = task
    }
    func imageTaskFinished(_ id: UUID) { imageTasks[id] = nil }
    private func commitSource(_ target: NovelBook, source: NovelSource, chapters: [NovelChapter], index: Int, token: UUID) async throws {
        try check(token, source: source)
        if original == nil { initialLoad = .chapter(book: target, source: source, chapters: chapters, index: index) }
        let identity = try await feature.rules.identity(target)
        try check(token, source: source)
        let restore = NovelAnchor(chapterUrl: chapters[index].chapterUrl, chapterTitle: chapters[index].name,
            chapterIndex: index, blockIndex: -1, ratio: anchor?.ratio ?? 0)
        try await load(index, book: target, identity: identity, source: source, chapters: chapters,
                       restore: restore, token: token, requiresProcessedContent: true)
    }
    public var purchasableChapter: NovelChapter? {
        guard chapters.indices.contains(chapterIndex), let source = activeSource, source.actions.contains("payAction") else { return nil }
        let chapter = chapters[chapterIndex]; return chapter.isVip && !chapter.isPay ? chapter : nil
    }
    /// Called only by the concrete book/chapter confirmation action in UIKit.
    public func confirmPurchase(chapter: NovelChapter, bookID: String) {
        guard book.id == bookID, purchasableChapter == chapter else { return }
        run { session, token in
            let source = try session.feature.source(for: session.book)
            if let paid = session.acceptedPurchase(for: session.book, source: source), paid.chapter.chapterUrl == chapter.chapterUrl {
                try await session.refreshPurchasedChapter(paid, restore: session.anchor, token: token)
                return
            }
            let payment = try await session.feature.sourcesClient.invoke(source, action: "payAction", payload: session.feature.sourcesClient.payload(session.book, chapter: chapter))
            try session.check(token, source: source)
            var paidChapter = chapter, paidBook = session.book
            if payment["chapter"] != .null {
                paidChapter = try NovelChapter.parse(payment["chapter"], index: session.chapterIndex, fallback: chapter)
                paidBook.variable = paidChapter.variable
            }
            // An accepted payment cannot be rolled back with a failed body.
            // Keep its context for an explicit refresh, never replay payAction.
            let paid = PaidContext(book: paidBook, chapter: paidChapter, source: source)
            session.paidContext = paid
            try await session.refreshPurchasedChapter(paid, restore: session.anchor, token: token)
        }
    }
    private func acceptedPurchase(for book: NovelBook, source: NovelSource) -> PaidContext? {
        guard let paidContext, paidContext.source.id == source.id, paidContext.source.revision == source.revision,
              paidContext.book.id == book.id, paidContext.book.bookUrl == book.bookUrl else { return nil }
        return paidContext
    }
    private func refreshPurchasedChapter(_ paid: PaidContext, restore: NovelAnchor?, token: UUID) async throws {
        let (catalog, index) = try await refreshPurchaseCatalog(paid, token: token)
        let identity = try await feature.rules.identity(catalog.book)
        try check(token, source: paid.source)
        try await load(index, book: catalog.book, identity: identity, source: paid.source,
                       chapters: catalog.chapters, restore: restore, token: token, requiresProcessedContent: true)
    }
    private func refreshPurchaseCatalog(_ paid: PaidContext, token: UUID) async throws -> (catalog: NovelCatalog, index: Int) {
        let catalog = try await feature.sourcesClient.chapters(paid.book, source: paid.source)
        var updated = catalog.chapters
        try check(token, source: paid.source)
        guard let index = updated.firstIndex(where: { $0.chapterUrl == paid.chapter.chapterUrl }) else { throw NovelError.noChapter }
        // Fresh catalog metadata/paid flags win; missing content/pay fields
        // survive, and the paid context's latest variable wins over toc.
        var refreshed = try NovelChapter.parse(updated[index].wire, index: index, fallback: paid.chapter)
        refreshed.variable = paid.chapter.variable
        updated[index] = refreshed
        var target = catalog.book; target.variable = paid.chapter.variable
        paidContext = PaidContext(book: target, chapter: refreshed, source: paid.source)
        return (.init(book: target, chapters: updated), index)
    }
    private func transformLatest(_ raw: NovelContent, book: NovelBook, source: NovelSource,
                                 token: UUID) async throws -> (content: NovelContent, input: ProcessingInput) {
        while true {
            try check(token, source: source)
            let input = processingInput
            do {
                let content = try await transform(raw, book, source, input.rules, input.conversion)
                try check(token, source: source)
                if input == processingInput { return (content, input) }
            } catch {
                try check(token, source: source)
                if input == processingInput { throw error }
            }
            // Only a newer input triggers another pass, always from raw.
        }
    }
    func reprocess() {
        guard !suspended, !loading, appliedInput != processingInput,
              let raw = original, let source = activeSource, feature.accepts(source) else { return }
        let restore = anchor
        run(reprocessOnCompletion: false) { session, token in
            let result = try await session.transformLatest(raw, book: session.book, source: source, token: token)
            session.anchor = NovelAnchorResolver.resolve(restore, previous: session.content, next: result.content)
            session.content = result.content; session.appliedInput = result.input
        }
    }
    func sourcesChanged() {
        if let paidContext, !feature.accepts(paidContext.source) { self.paidContext = nil }
        var source = activeSource
        if let initialLoad, case let .chapter(_, initialSource, _, _) = initialLoad { source = initialSource }
        if let selection = sourceSelection, !feature.accepts(selection.source) {
            sourceSelection = nil; source = selection.source
        }
        if let source, !feature.accepts(source) {
            initialLoad = nil
            cancel(); automatic = false; error = "当前书源已更新或停用，请重新载入"
        }
    }
    public func flush() async { saveDebounce?.cancel(); saveDebounce = nil; savePosition(); await saving?.value }
    public func pauseAutomatic() { automatic = false; saveDebounce?.cancel(); saveDebounce = nil; savePosition() }
    public func suspend() { suspended = true; pauseAutomatic(); cancel() }
    public func resume() {
        guard !closed else { return }
        suspended = false
        if let initialLoad {
            guard !loading else { return }
            switch initialLoad {
            case .start: start()
            case let .chapter(book, source, chapters, index): loadSelectedChapter(book, source: source, chapters: chapters, index: index)
            }
        } else { reprocess() }
    }
    private func run(resumeAutomatic: Bool = false, reprocessOnCompletion: Bool = true,
                     _ operation: @escaping @MainActor (NovelReadingSession, UUID) async throws -> Void) {
        guard !closed else { return }
        pauseAutomatic(); cancel(); suspended = false
        let token = UUID(); generation = token; loading = true
        pending = Task { [weak self] in
            guard let self else { return }
            var succeeded = false
            defer {
                running[token] = nil
                if generation == token {
                    pending = nil; loading = false; automatic = resumeAutomatic && succeeded
                    // Confirmation, ambiguity or a failed source operation may
                    // leave the old body retained while settings have changed.
                    if reprocessOnCompletion, contentGeneration != token { reprocess() }
                }
            }
            do { try await operation(self, token); succeeded = true }
            catch is CancellationError { }
            catch { if generation == token, !closed, !Task.isCancelled { self.error = error.localizedDescription } }
        }
        running[token] = pending
    }
    private func check(_ token: UUID, source: NovelSource? = nil) throws {
        try Task.checkCancellation()
        guard !closed, generation == token else { throw CancellationError() }
        if let source, !feature.accepts(source) { throw CancellationError() }
    }
    private func cancel() {
        generation = UUID()
        pending?.cancel()
        pending = nil; loading = false
    }
    public func close() async {
        if let closeTask { await closeTask.value; return }
        suspend(); closed = true; initialLoad = nil; cancel()
        let images = Array(imageTasks.values)
        for task in images { task.cancel() }
        let tasks = Array(running.values) + images, save = saving
        let task = Task { for task in tasks { await task.value }; await save?.value }
        closeTask = task; await task.value
        content = nil; original = nil; paidContext = nil; feature.releaseReader(self)
    }
}
