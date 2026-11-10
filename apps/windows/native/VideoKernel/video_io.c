#include "video_internal.h"
#include <libavutil/aes.h>
#include <libavutil/mem.h>
#include <libavutil/avstring.h>
#include <errno.h>

struct mvk_io {
    const AVClass *options_class;
    char *location;
    mvk_owner *owner;
    AVIOContext *context;
    uint64_t handle;
    int64_t size;
    int seekable;
    struct AVAES *aes;
    uint8_t iv[16];
    uint8_t cipher[MVK_IO_BUFFER];
    uint8_t plain[MVK_IO_BUFFER];
    int cipher_count, plain_count, plain_offset, eof;
    mvk_io *next;
};

static const AVOption io_options[] = {
    { "location", NULL, offsetof(mvk_io, location), AV_OPT_TYPE_STRING },
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

int mvk_interrupted(void *opaque)
{
    mvk_owner *owner = opaque;
    return InterlockedCompareExchange(&owner->cancelled, 0, 0) ||
           owner->callbacks.interrupted(owner->callbacks.opaque);
}
void mvk_failure(mvk_owner *owner, int code)
{
    if (!mvk_interrupted(owner))
        owner->callbacks.failed(owner->callbacks.opaque, code);
}

static int host_read(mvk_io *io, uint8_t *buffer, int size)
{
    if (mvk_interrupted(io->owner)) return AVERROR_EXIT;
    int count = io->owner->callbacks.read(io->owner->callbacks.opaque, io->handle, buffer, size);
    if (mvk_interrupted(io->owner)) return AVERROR_EXIT;
    if (count < 0 || count > size) return AVERROR(EIO);
    return count ? count : AVERROR_EOF;
}

static int crypto_fill(mvk_io *io)
{
    while (!io->eof && io->cipher_count < 32) {
        int count = host_read(io, io->cipher + io->cipher_count,
                              MVK_IO_BUFFER - io->cipher_count);
        if (count == AVERROR_EOF) io->eof = 1;
        else if (count < 0) return count;
        else io->cipher_count += count;
    }
    if (io->eof && (!io->cipher_count || io->cipher_count % 16))
        return io->cipher_count ? AVERROR_INVALIDDATA : AVERROR_EOF;
    int bytes = (io->cipher_count / 16 - (io->eof ? 0 : 1)) * 16;
    if (!bytes) return AVERROR_INVALIDDATA;
    av_aes_crypt(io->aes, io->plain, io->cipher, bytes / 16, io->iv, 1);
    io->cipher_count -= bytes;
    memmove(io->cipher, io->cipher + bytes, io->cipher_count);
    io->plain_offset = 0;
    io->plain_count = bytes;
    if (io->eof) {
        unsigned padding = io->plain[bytes - 1];
        if (!padding || padding > 16) return AVERROR_INVALIDDATA;
        for (unsigned i = 0; i < padding; i++)
            if (io->plain[bytes - 1 - i] != padding) return AVERROR_INVALIDDATA;
        io->plain_count -= padding;
    }
    return io->plain_count ? 0 : AVERROR_EOF;
}

static int io_read(void *opaque, uint8_t *buffer, int size)
{
    mvk_io *io = opaque;
    if (!io->aes) return host_read(io, buffer, size);
    if (mvk_interrupted(io->owner)) return AVERROR_EXIT;
    if (io->plain_offset == io->plain_count) {
        SecureZeroMemory(io->plain, sizeof(io->plain));
        int result = crypto_fill(io);
        if (result < 0) return result;
    }
    int count = size < io->plain_count - io->plain_offset ? size : io->plain_count - io->plain_offset;
    memcpy(buffer, io->plain + io->plain_offset, count);
    SecureZeroMemory(io->plain + io->plain_offset, count);
    io->plain_offset += count;
    return count;
}
static int64_t io_seek(void *opaque, int64_t offset, int whence)
{
    mvk_io *io = opaque;
    if (mvk_interrupted(io->owner)) return AVERROR_EXIT;
    if (whence & AVSEEK_SIZE) return io->aes || io->size < 0 ? AVERROR(ENOSYS) : io->size;
    if (!io->seekable || io->aes) return AVERROR(ENOSYS);
    int64_t result = io->owner->callbacks.seek(io->owner->callbacks.opaque,
                                              io->handle, offset, whence & ~AVSEEK_FORCE);
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
    char *end;
    errno = 0;
    int64_t value = strtoll(entry->value, &end, 10);
    return *end || errno == ERANGE ? INT64_MIN : value;
}

int mvk_io_open(AVFormatContext *format, AVIOContext **out, const char *uri,
                int flags, AVDictionary **options)
{
    mvk_owner *owner = format->opaque;
    if (flags != AVIO_FLAG_READ || mvk_interrupted(owner)) return AVERROR_EXIT;
    int crypto = !strncmp(uri, "crypto+", 7) || !strncmp(uri, "crypto:", 7);
    const char *address = crypto ? uri + 7 : uri;
    if (av_strncasecmp(address, "http://", 7) && av_strncasecmp(address, "https://", 8))
        return AVERROR(EPERM);
    int64_t offset = option_integer(options, "offset", 0);
    int64_t end = option_integer(options, "end_offset", -1);
    if (offset < 0 || end < -1 || (end >= 0 && end <= offset)) return AVERROR_INVALIDDATA;
    mvk_io *io = calloc(1, sizeof(*io));
    if (!io) return AVERROR(ENOMEM);
    io->options_class = &io_options_class;
    io->owner = owner;
    uint32_t location_size = 0;
    int result = owner->callbacks.open(owner->callbacks.opaque, address, offset, end,
                                       &io->handle, &io->size, &io->seekable,
                                       &location_size);
    if (result < 0) { free(io); return AVERROR(EIO); }
    io->location = location_size ? av_malloc(location_size) : NULL;
    if (!io->location || owner->callbacks.location(owner->callbacks.opaque, io->handle,
                                                   io->location, location_size) < 0) {
        int error = io->location ? AVERROR(EIO) : AVERROR(ENOMEM);
        av_free(io->location);
        owner->callbacks.close(owner->callbacks.opaque, io->handle);
        free(io); return error;
    }
    uint8_t *buffer = av_malloc(MVK_IO_BUFFER);
    io->context = buffer && io->location
        ? avio_alloc_context(buffer, MVK_IO_BUFFER, 0, io, io_read, NULL, io_seek) : NULL;
    if (!io->context) {
        av_free(buffer); av_free(io->location);
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
        io->aes = av_aes_alloc();
        int valid = io->aes && key && iv && !unhex(key->value, key_bytes) &&
                    !unhex(iv->value, io->iv) && av_aes_init(io->aes, key_bytes, 128, 1) >= 0;
        SecureZeroMemory(key_bytes, sizeof(key_bytes));
        if (!valid) { mvk_io_close(format, *out); *out = NULL; return AVERROR_INVALIDDATA; }
        io->context->seekable = 0;
    }
    return 0;
}

int mvk_io_close(AVFormatContext *format, AVIOContext *context)
{
    if (!context) return 0;
    mvk_io *io = context->opaque;
    mvk_owner *owner = io->owner;
    mvk_io **current = &owner->io;
    while (*current && *current != io) current = &(*current)->next;
    if (*current) *current = io->next;
    owner->callbacks.close(owner->callbacks.opaque, io->handle);
    if (context->buffer) SecureZeroMemory(context->buffer, context->buffer_size);
    av_freep(&context->buffer);
    avio_context_free(&context);
    av_free(io->location);
    if (io->aes) { SecureZeroMemory(io->aes, av_aes_size); av_free(io->aes); }
    SecureZeroMemory(io, sizeof(*io));
    free(io);
    return 0;
}
void mvk_io_close_all(mvk_owner *owner)
{
    while (owner->io) mvk_io_close(NULL, owner->io->context);
}
