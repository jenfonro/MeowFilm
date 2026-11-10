#include "video_internal.h"
#include <vlc_plugin.h>
#include <libavutil/avstring.h>
#include <libavutil/log.h>
#include <libavutil/mem.h>
#include <errno.h>

#if LIBAVFORMAT_VERSION_MAJOR != 62 || LIBAVCODEC_VERSION_MAJOR != 62 || LIBAVUTIL_VERSION_MAJOR != 60
#error The private bridge requires the locked FFmpeg 8.1.2 public ABI
#endif

static SRWLOCK registry_lock = SRWLOCK_INIT;
static CONDITION_VARIABLE registry_drained = CONDITION_VARIABLE_INIT;
static mvk_owner *registry;
static INIT_ONCE log_once = INIT_ONCE_STATIC_INIT;

static void no_log(void *object, int level, const char *format, va_list arguments) { }
static BOOL CALLBACK initialize_log(PINIT_ONCE once, PVOID parameter, PVOID *context)
{
    av_log_set_callback(no_log); /* Private static FFmpeg, not VLC's older avcodec. */
    return TRUE;
}

MVK_EXPORT const char *MVK_CALL mvk_build_identity(void) { return MVK_BUILD_IDENTITY; }

MVK_EXPORT int MVK_CALL mvk_create(const mvk_callbacks *callbacks, const char *uri, int format,
                                  const mvk_clear_key *keys, uint32_t count,
                                  const uint8_t *default_key, const char *token, void **result)
{
    if (!callbacks || callbacks->size != sizeof(*callbacks) || callbacks->abi != MVK_ABI ||
        !callbacks->open || !callbacks->location || !callbacks->read || !callbacks->seek || !callbacks->close ||
        !callbacks->interrupted || !callbacks->failed || !uri || !token || !result ||
        strlen(token) != 32 || format < MVK_AUTO || format > MVK_DASH ||
        (count && !keys)) return -1;
    for (unsigned i = 0; i < 32; i++)
        if (!((token[i] >= '0' && token[i] <= '9') || (token[i] >= 'a' && token[i] <= 'f'))) return -1;
    InitOnceExecuteOnce(&log_once, initialize_log, NULL, NULL);
    mvk_owner *owner = calloc(1, sizeof(*owner));
    if (!owner) return -1;
    owner->uri = strdup(uri);
    owner->keys = count ? malloc(count * sizeof(*keys)) : NULL;
    if (!owner->uri || (count && !owner->keys)) {
        free(owner->uri); free(owner->keys); free(owner); return -1;
    }
    owner->callbacks = *callbacks;
    owner->format = format;
    owner->key_count = count;
    if (count) memcpy(owner->keys, keys, count * sizeof(*keys));
    if (default_key) {
        memcpy(owner->default_key, default_key, 16);
        owner->has_default = 1;
    }
    memcpy(owner->token, token, 33);
    AcquireSRWLockExclusive(&registry_lock);
    for (mvk_owner *current = registry; current; current = current->next) {
        if (!strcmp(current->token, token)) {
            ReleaseSRWLockExclusive(&registry_lock);
            SecureZeroMemory(owner->keys, count * sizeof(*keys));
            free(owner->keys); free(owner->uri);
            SecureZeroMemory(owner, sizeof(*owner)); free(owner); return -1;
        }
    }
    owner->next = registry; registry = owner;
    ReleaseSRWLockExclusive(&registry_lock);
    *result = owner;
    return 0;
}
MVK_EXPORT void MVK_CALL mvk_cancel(void *opaque)
{
    InterlockedExchange(&((mvk_owner *)opaque)->cancelled, 1);
}
MVK_EXPORT void MVK_CALL mvk_release(void *opaque)
{
    mvk_owner *owner = opaque;
    mvk_cancel(owner);
    AcquireSRWLockExclusive(&registry_lock);
    owner->closing = 1;
    mvk_owner **current = &registry;
    while (*current && *current != owner) current = &(*current)->next;
    if (*current) *current = owner->next;
    while (owner->users) SleepConditionVariableSRW(&registry_drained, &registry_lock, INFINITE, 0);
    ReleaseSRWLockExclusive(&registry_lock);
    SecureZeroMemory(owner->keys, owner->key_count * sizeof(*owner->keys));
    free(owner->keys);
    SecureZeroMemory(owner->uri, strlen(owner->uri));
    free(owner->uri);
    SecureZeroMemory(owner, sizeof(*owner)); free(owner);
}

static mvk_owner *owner_acquire(const char *token)
{
    mvk_owner *found = NULL;
    AcquireSRWLockExclusive(&registry_lock);
    for (mvk_owner *owner = registry; owner; owner = owner->next) {
        if (!strcmp(owner->token, token) && !owner->closing && !owner->users &&
            !InterlockedCompareExchange(&owner->cancelled, 0, 0)) {
            found = owner; owner->users++; break;
        }
    }
    ReleaseSRWLockExclusive(&registry_lock);
    return found;
}
static void owner_drop(mvk_owner *owner)
{
    AcquireSRWLockExclusive(&registry_lock);
    owner->users--;
    WakeAllConditionVariable(&registry_drained);
    ReleaseSRWLockExclusive(&registry_lock);
}

static void clear_dictionary(AVDictionary **dictionary)
{
    AVDictionaryEntry *entry = NULL;
    while ((entry = av_dict_get(*dictionary, "", entry, AV_DICT_IGNORE_SUFFIX)))
        SecureZeroMemory(entry->value, strlen(entry->value));
    av_dict_free(dictionary);
}
static void to_hex(const uint8_t *bytes, char out[33])
{
    static const char hex[] = "0123456789abcdef";
    for (unsigned i = 0; i < 16; i++) { out[i * 2] = hex[bytes[i] >> 4]; out[i * 2 + 1] = hex[bytes[i] & 15]; }
    out[32] = '\0';
}
static int key_options(mvk_owner *owner, AVDictionary **options)
{
    AVDictionary *pairs = NULL;
    int result = 0;
    for (unsigned i = 0; i < owner->key_count; i++) {
        char kid[33], key[33];
        to_hex(owner->keys[i].kid, kid); to_hex(owner->keys[i].key, key);
        result = av_dict_set(&pairs, kid, key, 0);
        SecureZeroMemory(key, sizeof(key));
        if (result < 0) break;
    }
    char *serialized = NULL;
    if (result >= 0 && pairs) result = av_dict_get_string(pairs, &serialized, '=', ':');
    if (result >= 0 && serialized) {
        result = av_dict_set(options, "decryption_keys", serialized, 0);
        if (result >= 0) result = av_dict_set(options, "cenc_decryption_keys", serialized, 0);
    }
    if (serialized) { SecureZeroMemory(serialized, strlen(serialized)); av_free(serialized); }
    clear_dictionary(&pairs);
    if (result >= 0 && owner->has_default) {
        char key[33];
        to_hex(owner->default_key, key);
        result = av_dict_set(options, "decryption_key", key, 0);
        if (result >= 0) result = av_dict_set(options, "cenc_decryption_key", key, 0);
        SecureZeroMemory(key, sizeof(key));
    }
    return result;
}

static void demux_cleanup(demux_t *demux)
{
    demux_sys_t *sys = demux->p_sys;
    if (!sys) return;
    mvk_delete_tracks(demux);
    av_packet_free(&sys->packet);
    avformat_close_input(&sys->format);
    mvk_io_close_all(sys->owner);
    owner_drop(sys->owner);
    free(sys); demux->p_sys = NULL;
}

static int demux_packet(demux_t *demux)
{
    demux_sys_t *sys = demux->p_sys;
    if (mvk_interrupted(sys->owner)) return VLC_DEMUXER_EGENERIC;
    for (unsigned i = 0; i < sys->track_count; i++) {
        bool selected = false;
        if (sys->tracks[i].es) es_out_Control(demux->out, ES_OUT_GET_ES_STATE, sys->tracks[i].es, &selected);
        sys->format->streams[i]->discard = selected ? AVDISCARD_DEFAULT : AVDISCARD_ALL;
    }
    int result = av_read_frame(sys->format, sys->packet);
    if (result == AVERROR_EOF) return VLC_DEMUXER_EOF;
    if (result < 0 || mvk_add_tracks(demux) != VLC_SUCCESS) {
        mvk_failure(sys->owner, MVK_READ_FAILED); return VLC_DEMUXER_EGENERIC;
    }
    const AVPacket *packet = sys->packet;
    if (packet->stream_index < 0 || (unsigned)packet->stream_index >= sys->track_count) {
        av_packet_unref(sys->packet); mvk_failure(sys->owner, MVK_READ_FAILED);
        return VLC_DEMUXER_EGENERIC;
    }
    size_t side_size;
    if (av_packet_get_side_data(packet, AV_PKT_DATA_ENCRYPTION_INFO, &side_size)) {
        av_packet_unref(sys->packet); mvk_failure(sys->owner, MVK_ENCRYPTION_UNSUPPORTED);
        return VLC_DEMUXER_EGENERIC; /* Encrypted bytes are never passed off as decrypted packets. */
    }
    mvk_track *track = &sys->tracks[packet->stream_index];
    const AVStream *stream = sys->format->streams[packet->stream_index];
    if (!track->es || packet->size <= 0) { av_packet_unref(sys->packet); return VLC_DEMUXER_SUCCESS; }
    block_t *block = mvk_packet_block(packet, stream, sys->start);
    if (!block) { av_packet_unref(sys->packet); mvk_failure(sys->owner, MVK_READ_FAILED); return VLC_DEMUXER_EGENERIC; }
    int64_t clock = block->i_dts > VLC_TS_INVALID ? block->i_dts : block->i_pts;
    if (stream->codecpar->codec_type == AVMEDIA_TYPE_VIDEO || stream->codecpar->codec_type == AVMEDIA_TYPE_AUDIO)
        track->dts = clock;
    int64_t earliest = INT64_MAX;
    for (unsigned i = 0; i < sys->track_count; i++)
        if (sys->tracks[i].es && sys->format->streams[i]->discard != AVDISCARD_ALL &&
            sys->tracks[i].dts > VLC_TS_INVALID &&
            sys->tracks[i].dts < earliest) earliest = sys->tracks[i].dts;
    if (earliest != INT64_MAX && earliest >= sys->time) {
        sys->time = earliest;
        es_out_SetPCR(demux->out, earliest);
    }
    es_out_Send(demux->out, track->es, block);
    av_packet_unref(sys->packet);
    return VLC_DEMUXER_SUCCESS;
}

static int demux_seek(demux_t *demux, int64_t time)
{
    demux_sys_t *sys = demux->p_sys;
    if (time < 0 || mvk_interrupted(sys->owner) ||
        (sys->format->duration > 0 && time > sys->format->duration)) return VLC_EGENERIC;
    int64_t target = time + sys->start;
    if (avformat_seek_file(sys->format, -1, INT64_MIN, target, target, AVSEEK_FLAG_BACKWARD) < 0) {
        mvk_failure(sys->owner, MVK_SEEK_FAILED); return VLC_EGENERIC;
    }
    avformat_flush(sys->format);
    av_packet_unref(sys->packet);
    for (unsigned i = 0; i < sys->track_count; i++) sys->tracks[i].dts = VLC_TS_INVALID;
    es_out_Control(demux->out, ES_OUT_RESET_PCR);
    es_out_Control(demux->out, ES_OUT_SET_NEXT_DISPLAY_TIME, time + VLC_TS_0);
    sys->time = time + VLC_TS_0;
    return VLC_SUCCESS;
}
static int demux_control(demux_t *demux, int query, va_list arguments)
{
    demux_sys_t *sys = demux->p_sys;
    int64_t duration = sys->format->duration > 0 ? sys->format->duration : 0;
    switch (query) {
    case DEMUX_CAN_SEEK:
        *va_arg(arguments, bool *) = duration > 0 &&
            !(sys->format->ctx_flags & AVFMTCTX_UNSEEKABLE) &&
            (sys->root->seekable || av_match_name("hls", sys->format->iformat->name) ||
                                   av_match_name("dash", sys->format->iformat->name));
        return VLC_SUCCESS;
    case DEMUX_CAN_PAUSE: case DEMUX_CAN_CONTROL_PACE:
        *va_arg(arguments, bool *) = true; return VLC_SUCCESS;
    case DEMUX_CAN_CONTROL_RATE:
        *va_arg(arguments, bool *) = false; return VLC_SUCCESS;
    case DEMUX_SET_PAUSE_STATE: return VLC_SUCCESS;
    case DEMUX_GET_PTS_DELAY:
        *va_arg(arguments, int64_t *) = DEFAULT_PTS_DELAY; return VLC_SUCCESS;
    case DEMUX_GET_LENGTH:
        *va_arg(arguments, int64_t *) = duration; return VLC_SUCCESS;
    case DEMUX_GET_TIME:
        *va_arg(arguments, int64_t *) = sys->time > VLC_TS_0 ? sys->time - VLC_TS_0 : 0;
        return VLC_SUCCESS;
    case DEMUX_GET_POSITION:
        *va_arg(arguments, double *) = duration ? (double)(sys->time - VLC_TS_0) / duration : 0;
        return VLC_SUCCESS;
    case DEMUX_SET_POSITION: {
        double position = va_arg(arguments, double);
        return position >= 0 && position <= 1 && duration ? demux_seek(demux, duration * position) : VLC_EGENERIC;
    }
    case DEMUX_SET_TIME: return demux_seek(demux, va_arg(arguments, int64_t));
    default: return VLC_EGENERIC;
    }
}

static int plugin_open(vlc_object_t *object)
{
    demux_t *demux = (demux_t *)object;
    mvk_owner *owner = owner_acquire(demux->psz_location);
    if (!owner) return VLC_EGENERIC;
    demux_sys_t *sys = calloc(1, sizeof(*sys));
    if (!sys) { owner_drop(owner); return VLC_ENOMEM; }
    demux->p_sys = sys; sys->owner = owner;
    sys->format = avformat_alloc_context();
    sys->packet = av_packet_alloc();
    if (!sys->format || !sys->packet) goto failed;
    AVFormatContext *format = sys->format;
    format->opaque = owner;
    format->io_open = mvk_io_open; format->io_close2 = mvk_io_close;
    format->interrupt_callback = (AVIOInterruptCB){ mvk_interrupted, owner };
    format->flags |= AVFMT_FLAG_CUSTOM_IO;
    if (mvk_io_open(format, &sys->root, owner->uri, AVIO_FLAG_READ, NULL) < 0) goto failed;
    format->pb = sys->root;
    uint8_t *location = NULL;
    av_opt_get(sys->root, "location", AV_OPT_SEARCH_CHILDREN, &location);
    const AVInputFormat *forced = owner->format == MVK_HLS ? av_find_input_format("hls") :
                                 owner->format == MVK_DASH ? av_find_input_format("dash") : NULL;
    AVDictionary *options = NULL;
    int result = key_options(owner, &options);
    av_dict_set(&options, "http_persistent", "0", 0);
    av_dict_set(&options, "http_multiple", "0", 0);
    if (result >= 0) result = avformat_open_input(&sys->format, location ? (char *)location : owner->uri,
                                                 forced, &options);
    clear_dictionary(&options); av_free(location);
    if (result < 0) goto failed;
    format = sys->format;
    int adaptive = av_match_name("hls", format->iformat->name) || av_match_name("dash", format->iformat->name);
    if ((owner->format == MVK_PROGRESSIVE && adaptive) ||
        ((owner->key_count || owner->has_default) &&
         !av_match_name("dash", format->iformat->name) && !av_match_name("mov", format->iformat->name))) {
        mvk_failure(owner, MVK_FORMAT_UNSUPPORTED); demux_cleanup(demux); return VLC_EGENERIC;
    }
    if (avformat_find_stream_info(format, NULL) < 0 || !format->nb_streams ||
        mvk_add_tracks(demux) != VLC_SUCCESS) goto failed;
    unsigned playable = 0;
    for (unsigned i = 0; i < sys->track_count; i++) if (sys->tracks[i].es) playable++;
    if (!playable) {
        mvk_failure(owner, MVK_FORMAT_UNSUPPORTED); demux_cleanup(demux); return VLC_EGENERIC;
    }
    sys->start = format->start_time == AV_NOPTS_VALUE ? 0 : format->start_time;
    sys->time = VLC_TS_0;
    demux->pf_demux = demux_packet; demux->pf_control = demux_control;
    return VLC_SUCCESS;
failed:
    mvk_failure(owner, MVK_OPEN_FAILED);
    demux_cleanup(demux);
    return VLC_EGENERIC;
}
static void plugin_close(vlc_object_t *object) { demux_cleanup((demux_t *)object); }

vlc_module_begin()
    set_shortname("MeowFilm private demux")
    set_description("Controlled FFmpeg packet demux")
    set_capability("access_demux", 0)
    add_shortcut("meowfilm-ffmpeg")
    set_callbacks(plugin_open, plugin_close)
vlc_module_end()
