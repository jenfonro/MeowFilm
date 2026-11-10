import Foundation
import MeowRuntime

public enum FilmHomeError: Error, Equatable, Sendable, LocalizedError {
    case network
    case timeout
    case invalidResponse
    case responseTooLarge
    case upstream(statusCode: Int)
    case runtime(code: String)

    public var errorDescription: String? {
        switch self {
        case .network: return "影视数据加载失败"
        case .timeout: return "影视数据请求超时"
        case .invalidResponse: return "影视数据格式无效"
        case .responseTooLarge: return "影视数据超出大小限制"
        case .upstream: return "影视数据源暂时不可用"
        case .runtime: return "影视数据加载失败"
        }
    }

    static func from(_ error: Error) -> FilmHomeError {
        if let error = error as? FilmHomeError { return error }
        if let error = error as? RuntimeHTTPError {
            switch error {
            case .timeout: return .timeout
            case .invalidResponse: return .invalidResponse
            case .remote(let code): return .runtime(code: code)
            default: return .network
            }
        }
        if (error as? URLError)?.code == .timedOut { return .timeout }
        return .network
    }
}
