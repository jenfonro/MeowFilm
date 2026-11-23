import MeowMusicFeature

enum MusicNativeQueueDisplayIdentity {
    static func make(song: MusicSongRecord, occurrence: String, context: MusicPlayContextRecord) -> MusicQueueDisplayIdentity {
        // The native local-library queue records collectionId, not playlistId.
        // Preserve its actual stable ID; equal collection names are not equal
        // sources. Other contexts follow queue-sheet.js's ordered source keys.
        let local = context.collectionId ?? ""
        let type = local.isEmpty ? (context.sourceType ?? "") : "collection"
        let source = local.isEmpty
            ? MusicModel.firstText(context.playlistId, context.sourceId, context.playlist, context.sourceTitle)
            : local
        return .init(songID: song.songID, occurrenceID: occurrence,
                     sourceType: type, sourceID: source, hasContext: !context.isEmpty)
    }
}
