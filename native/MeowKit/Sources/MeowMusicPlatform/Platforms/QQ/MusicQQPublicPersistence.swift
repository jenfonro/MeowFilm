import Foundation
import MeowStorage

/// Existing QQ namespace and codec stay byte-compatible. Only the shared
/// protected-document and admission mechanisms are factored out.
public final class MusicQQPublicPersistenceDocument: MusicClientContextDocument, @unchecked Sendable {
    public static let namespace = "music.qq-public.v1"
    public static let key = "qq-public-device-v1"
    public init(storeFactory: @escaping @Sendable () throws -> EncryptedDocumentStore) {
        super.init(key: Self.key, storeFactory: storeFactory)
    }
}

final class MusicQQPublicPersistenceState: MusicClientContextState<[String: Any]>, @unchecked Sendable {
    init(document: (any MusicClientContextPersisting)?, compatibilityOpen: Bool = false) {
        super.init(document: document, compatibilityOpen: compatibilityOpen,
                   decode: { try MusicQQPublicPersistenceCodec.decode($0) },
                   encode: { try MusicQQPublicPersistenceCodec.encode($0) })
    }
}
