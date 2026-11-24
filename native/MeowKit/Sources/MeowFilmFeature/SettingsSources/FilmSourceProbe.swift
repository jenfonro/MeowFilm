import Foundation
import MeowRuntime

/// Delegates cat source probing to the shared Node owner (`cat.script.probe`).
struct FilmSourceProbe {
    private static let validResults: Set<String> = ["valid", "search_error", "category_error", "invalid"]
    private static let staleCancellationCodes: Set<String> = [
        "CANCELLED", "STALE_SESSION", "HOST_CLOSED", "CLOSED", "DISABLED", "STARTING",
    ]

    let transport: CatScriptTransport
    let cat: FilmCatSource
    let site: FilmVideoSource

    init(transport: CatScriptTransport, cat: FilmCatSource, site: FilmVideoSource) {
        self.transport = transport; self.cat = cat; self.site = site
    }

    func run() async throws -> FilmSourceDetection {
        try Task.checkCancellation()
        let response: RuntimeValue
        do {
            response = try await transport.call("probe", [
                "installationId": .string(cat.installationId),
                "generation": .number(Double(cat.generation)),
                "siteId": .string(site.key.siteKey),
                "siteApi": .string(site.api),
                "actions": .array(site.actions.map(RuntimeValue.string)),
            ])
        } catch let error as FilmSourceError where Self.staleCancellationCodes.contains(error.code) {
            throw CancellationError()
        }
        try Task.checkCancellation()
        return try Self.decode(response)
    }

    private static func decode(_ value: RuntimeValue) throws -> FilmSourceDetection {
        guard case .object(let fields) = value,
              let result = fields["result"]?.catText, validResults.contains(result),
              let checkedAt = fields["checkedAt"]?.catInteger,
              case .bool(let playbackResolved)? = fields["playbackResolved"],
              let rawSteps = fields["steps"]?.catArray else {
            throw FilmSourceError(code: "INVALID_RESPONSE")
        }
        let steps: [FilmSourceProbeStep] = try rawSteps.map { entry in
            guard case .object(let step) = entry,
                  let operation = step["operation"]?.catText, !operation.isEmpty,
                  let status = step["status"]?.catText, !status.isEmpty else {
                throw FilmSourceError(code: "INVALID_RESPONSE")
            }
            let reason: String?
            if let rawReason = step["reason"], rawReason != .null {
                guard let text = rawReason.catText else { throw FilmSourceError(code: "INVALID_RESPONSE") }
                reason = text
            } else {
                reason = nil
            }
            return FilmSourceProbeStep(operation, status, reason)
        }
        return FilmSourceDetection(
            result: result,
            checkedAt: Int64(checkedAt),
            playbackResolved: playbackResolved,
            steps: steps
        )
    }
}
