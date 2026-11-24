#include "film_io.h"
#include <stdlib.h>
#include <string.h>
#include <libavutil/aes.h>
#include <libavutil/mem.h>
#include <libavutil/avstring.h>
#include <errno.h>

typedef struct {
    struct AVAES *aes;
    uint8_t iv[16];
    uint8_t cipher[MFK_IO_BUFFER];
    uint8_t plain[MFK_IO_BUFFER];
    int cipher_count, plain_count, plain_offset, eof, error;
} mfk_crypto;

struct mfk_io {
    const AVClass *options_class;
    char *location;
    mfk_input *owner;
    AVIOContext *context;
    uint64_t handle;
    int64_t size;
    int seekable;
    mfk_crypto *crypto;
    mfk_io *next;
};

static const AVOption io_options[] = {
    { "location", NULL, offsetof(mfk_io, location), AV_OPT_TYPE_STRING },
    { NULL }
};
static const char *io_name(void *ignored) { return "meowfilm controlled AVIO"; }
static const AVClass io_options_class = {
    .class_name = "meowfilm controlled HTTP",
    .item_name = io_name, .option = io_options,
    .version = LIBAVUTIL_VERSION_INT,
};
static void *io_child(void *object, void *previous)
{
    return previous ? NULL : ((AVIOContext *)object)->opaque;
}
static const AVClass io_context_class = {
    .class_name = "meowfilm AVIO", .item_name = io_name,
    .version = LIBAVUTIL_VERSION_INT, .child_next = io_child,
};

int mfk_interrupted(void *opaque)
{
    mfk_input *owner = opaque;
    return InterlockedCompareExchange(&owner->cancelled, 0, 0) ||
           owner->callbacks.interrupted(owner->callbacks.opaque);
}

static int host_read(mfk_io *io, uint8_t *buffer, int size)
{
    if (size <= 0) return 0;
    if (mfk_interrupted(io->owner)) return AVERROR_EXIT;
    int count = io->owner->callbacks.read(io->owner->callbacks.opaque, io->handle, buffer, size);
    if (mfk_interrupted(io->owner)) return AVERROR_EXIT;
    if (count < 0 || count > size) return AVERROR(EIO);
    return count ? count : AVERROR_EOF;
}

static int crypto_fill(mfk_io *io)
{
    mfk_crypto *c = io->crypto;
    while (!c->eof && c->cipher_count < 32) {
        int count = host_read(io, c->cipher + c->cipher_count,
                              MFK_IO_BUFFER - c->cipher_count);
        if (count == AVERROR_EOF) c->eof = 1;
        else if (count < 0) return count;
        else c->cipher_count += count;
    }
    if (c->eof && (!c->cipher_count || c->cipher_count % 16))
        return c->cipher_count ? AVERROR_INVALIDDATA : AVERROR_EOF;
    int bytes = (c->cipher_count / 16 - (c->eof ? 0 : 1)) * 16;
    if (!bytes) return AVERROR_INVALIDDATA;
    av_aes_crypt(c->aes, c->plain, c->cipher, bytes / 16, c->iv, 1);
    c->cipher_count -= bytes;
    memmove(c->cipher, c->cipher + bytes, c->cipher_count);
    if (c->eof) {
        unsigned padding = c->plain[bytes - 1];
        if (!padding || padding > 16) return AVERROR_INVALIDDATA;
        for (unsigned i = 0; i < padding; i++)
            if (c->plain[bytes - 1 - i] != padding) return AVERROR_INVALIDDATA;
        bytes -= padding;
    }
    c->plain_offset = 0;
    c->plain_count = bytes;
    return bytes ? 0 : AVERROR_EOF;
}

static int io_read(void *opaque, uint8_t *buffer, int size)
{
    mfk_io *io = opaque;
    if (size <= 0) return 0;
    if (!io->crypto) return host_read(io, buffer, size);
    if (mfk_interrupted(io->owner)) return AVERROR_EXIT;
    mfk_crypto *c = io->crypto;
    if (c->error) return c->error;
    if (c->plain_offset == c->plain_count) {
        int result = crypto_fill(io);
        if (result < 0) {
            c->error = result;
            return result;
        }
    }
    int count = size < c->plain_count - c->plain_offset ? size : c->plain_count - c->plain_offset;
    memcpy(buffer, c->plain + c->plain_offset, count);
    c->plain_offset += count;
    return count;
}

static int64_t io_seek(void *opaque, int64_t offset, int whence)
{
    mfk_io *io = opaque;
    if (mfk_interrupted(io->owner)) return AVERROR_EXIT;
    if (whence & AVSEEK_SIZE) return io->crypto || io->size < 0 ? AVERROR(ENOSYS) : io->size;
    if (!io->seekable || io->crypto) return AVERROR(ENOSYS);
    int64_t result = io->owner->callbacks.seek(io->owner->callbacks.opaque,
                                              io->handle, offset, whence & ~AVSEEK_FORCE);
    if (mfk_interrupted(io->owner)) return AVERROR_EXIT;
    return result < 0 ? AVERROR(EIO) : result;
}

static int unhex(const char *text, uint8_t *out)
{
    if (!text || strlen(text) != 32) return -1;
    for (unsigned i = 0; i < 16; i++) {
        int value = 0;
        for (unsigned n = 0; n < 2; n++) {
            unsigned c = (unsigned char)text[i * 2 + n];
            int digit = c >= '0' && c <= '9' ? (int)c - '0' :
                        c >= 'a' && c <= 'f' ? (int)c - 'a' + 10 :
                        c >= 'A' && c <= 'F' ? (int)c - 'A' + 10 : -1;
            if (digit < 0) return -1;
            value = value * 16 + digit;
        }
        out[i] = (uint8_t)value;
    }
    return 0;
}

static int64_t option_integer(AVDictionary **options, const char *name, int64_t fallback)
{
    AVDictionaryEntry *entry = options ? av_dict_get(*options, name, NULL, 0) : NULL;
    if (!entry) return fallback;
    if (!entry->value || !entry->value[0]) return INT64_MIN;
    char *end;
    errno = 0;
    int64_t value = strtoll(entry->value, &end, 10);
    return *end || errno == ERANGE ? INT64_MIN : value;
}

int mfk_io_open(AVFormatContext *format, AVIOContext **out, const char *uri,
                int flags, AVDictionary **options)
{
    mfk_input *owner = format->opaque;
    if (!(flags & AVIO_FLAG_READ) || (flags & AVIO_FLAG_WRITE) || mfk_interrupted(owner))
        return AVERROR_EXIT;
    int crypto = !strncmp(uri, "crypto+", 7) || !strncmp(uri, "crypto:", 7);
    const char *address = crypto ? uri + 7 : uri;
    if (av_strncasecmp(address, "http://", 7) && av_strncasecmp(address, "https://", 8))
        return AVERROR(EPERM);
    int64_t offset = option_integer(options, "offset", 0);
    int64_t end = option_integer(options, "end_offset", -1);
    if (offset < 0 || end < -1 || (end >= 0 && end <= offset)) return AVERROR_INVALIDDATA;
    mfk_io *io = calloc(1, sizeof(*io));
    if (!io) return AVERROR(ENOMEM);
    io->options_class = &io_options_class;
    io->owner = owner;
    uint32_t location_size = 0;
    int result = owner->callbacks.open(owner->callbacks.opaque, address, offset, end,
                                       &io->handle, &io->size, &io->seekable,
                                       &location_size);
    if (mfk_interrupted(owner)) {
        if (result >= 0) owner->callbacks.close(owner->callbacks.opaque, io->handle);
        free(io);
        return AVERROR_EXIT;
    }
    if (result < 0) { free(io); return AVERROR(EIO); }
    io->location = location_size ? av_mallocz(location_size) : NULL;
    if (!io->location || owner->callbacks.location(owner->callbacks.opaque, io->handle,
                                                   io->location, location_size) < 0) {
        int error = mfk_interrupted(owner) ? AVERROR_EXIT : (io->location ? AVERROR(EIO) : AVERROR(ENOMEM));
        av_freep(&io->location);
        owner->callbacks.close(owner->callbacks.opaque, io->handle);
        free(io); return error;
    }
    uint8_t *buffer = av_malloc(MFK_IO_BUFFER);
    io->context = buffer
        ? avio_alloc_context(buffer, MFK_IO_BUFFER, 0, io, io_read, NULL, io_seek) : NULL;
    if (!io->context) {
        av_free(buffer); av_freep(&io->location);
        owner->callbacks.close(owner->callbacks.opaque, io->handle);
        free(io); return AVERROR(ENOMEM);
    }
    io->context->av_class = &io_context_class;
    io->context->seekable = io->seekable ? AVIO_SEEKABLE_NORMAL : 0;
    io->context->pos = offset;
    io->next = owner->io; owner->io = io;
    *out = io->context;
    if (crypto) {
        AVDictionaryEntry *key = options ? av_dict_get(*options, "key", NULL, 0) : NULL;
        AVDictionaryEntry *iv = options ? av_dict_get(*options, "iv", NULL, 0) : NULL;
        uint8_t key_bytes[16] = {0};
        io->crypto = calloc(1, sizeof(*io->crypto));
        if (io->crypto) io->crypto->aes = av_aes_alloc();
        int valid = io->crypto && io->crypto->aes && key && iv &&
                    !unhex(key->value, key_bytes) &&
                    !unhex(iv->value, io->crypto->iv) &&
                    av_aes_init(io->crypto->aes, key_bytes, 128, 1) >= 0;
        if (!valid) { mfk_io_close(format, *out); *out = NULL; return AVERROR_INVALIDDATA; }
        io->context->seekable = 0;
    }
    return 0;
}

int mfk_io_close(AVFormatContext *format, AVIOContext *context)
{
    if (!context) return 0;
    mfk_io *io = context->opaque;
    mfk_input *owner = io->owner;
    mfk_io **current = &owner->io;
    while (*current && *current != io) current = &(*current)->next;
    if (*current) *current = io->next;
    owner->callbacks.close(owner->callbacks.opaque, io->handle);
    av_freep(&context->buffer);
    avio_context_free(&context);
    av_free(io->location);
    if (io->crypto) {
        if (io->crypto->aes) av_free(io->crypto->aes);
        free(io->crypto);
    }
    free(io);
    return 0;
}

void mfk_io_close_all(mfk_input *owner)
{
    while (owner->io) mfk_io_close(NULL, owner->io->context);
}

void mfk_clear_dictionary(AVDictionary **dictionary)
{
    av_dict_free(dictionary);
}

static void to_hex(const uint8_t *bytes, char out[33])
{
    static const char hex[] = "0123456789abcdef";
    for (unsigned i = 0; i < 16; i++) { out[i * 2] = hex[bytes[i] >> 4]; out[i * 2 + 1] = hex[bytes[i] & 15]; }
    out[32] = '\0';
}

int mfk_key_options(mfk_input *owner, AVDictionary **options)
{
    AVDictionary *pairs = NULL;
    int result = 0;
    for (unsigned i = 0; i < owner->key_count; i++) {
        char kid[33], key[33];
        to_hex(owner->keys[i].kid, kid); to_hex(owner->keys[i].key, key);
        result = av_dict_set(&pairs, kid, key, 0);
        if (result < 0) break;
    }
    char *serialized = NULL;
    if (result >= 0 && pairs) result = av_dict_get_string(pairs, &serialized, '=', ':');
    if (result >= 0 && serialized) {
        result = av_dict_set(options, "decryption_keys", serialized, 0);
        if (result >= 0) result = av_dict_set(options, "cenc_decryption_keys", serialized, 0);
    }
    if (serialized) av_free(serialized);
    mfk_clear_dictionary(&pairs);
    if (result >= 0 && owner->has_default) {
        char key[33];
        to_hex(owner->default_key, key);
        result = av_dict_set(options, "decryption_key", key, 0);
        if (result >= 0) result = av_dict_set(options, "cenc_decryption_key", key, 0);
    }
    return result;
}
