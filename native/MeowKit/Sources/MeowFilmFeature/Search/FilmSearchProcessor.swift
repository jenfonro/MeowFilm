import Foundation
import FilmSearchProgram
import MeowJavaScript
import MeowRuntime

/// The existing native QuickJS worker owns serialization and cancellation.
/// No search cleaning, grouping or sorting is implemented in Swift.
public actor FilmSearchProcessor {
    private let engine: any JavaScriptJSONEngine
    public init(engine: any JavaScriptJSONEngine) { self.engine = engine }
    public init() throws { engine = NativeJavaScriptJSONEngine(source: try FilmSearchProgram.source()) }
    func invoke(_ fields: [String: RuntimeValue]) async throws -> RuntimeValue {
        try Task.checkCancellation()
        let data = try await engine.invoke(RuntimeJSON.encode(.object(RuntimeObject(fields))))
        try Task.checkCancellation()
        return try RuntimeJSON.decode(data)
    }
    public func close() async { await engine.close() }
}
