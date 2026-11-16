/* Standalone executable: compile this translation unit, not an additional
 * film_io.c, and link the locked FFmpeg libraries. All transport and key bytes
 * are synthetic; no network, credentials, device or UI is used. */
#include "film_io.c"
#include <stdio.h>

#define CHECK(condition) do { if (!(condition)) { \
    fprintf(stderr, "%s:%d: %s\n", __FILE__, __LINE__, #condition); \
    abort(); \
} } while (0)

typedef struct {
    mfk_input input;
    AVFormatContext format;
    const char *address, *location;
    const uint8_t *data;
    int bytes, chunk, seekable;
    int64_t size, offset, end, positions[8], seek_offset;
    int opens, handles, locations, reads, seeks, whence, closes[8];
    int fail_open, fail_location, fail_read, fail_seek, cancel_read, interrupted;
    int failures, error;
} transport;

static int MFK_CALL open_resource(void *opaque, const char *uri, int64_t offset,
    int64_t end, uint64_t *handle, int64_t *size, int *seekable, uint32_t *location_size)
{
    transport *host = opaque;
    CHECK(!strcmp(uri, host->address));
    host->opens++; host->offset = offset; host->end = end;
    if (host->fail_open) return -1;
    CHECK(host->handles < 7);
    *handle = ++host->handles;
    host->positions[*handle] = offset;
    *size = host->size; *seekable = host->seekable;
    *location_size = (uint32_t)strlen(host->location) + 1;
    return 0;
}

static int MFK_CALL resource_location(void *opaque, uint64_t handle, char *out, uint32_t size)
{
    transport *host = opaque;
    CHECK(handle && handle <= (uint64_t)host->handles && !host->closes[handle]);
    CHECK(size == strlen(host->location) + 1);
    host->locations++;
    if (host->fail_location) return -1;
    memcpy(out, host->location, size);
    return 0;
}

static int MFK_CALL read_resource(void *opaque, uint64_t handle, uint8_t *out, int size)
{
    transport *host = opaque;
    CHECK(handle && handle <= (uint64_t)host->handles && !host->closes[handle] && size > 0);
    host->reads++;
    if (host->cancel_read) host->interrupted = 1;
    if (host->fail_read) return host->fail_read < 0 ? -1 : size + 1;
    int64_t end = host->end < 0 || host->end > host->bytes ? host->bytes : host->end;
    int64_t available = end - host->positions[handle];
    int count = available <= 0 ? 0 : available < size ? (int)available : size;
    if (count > host->chunk) count = host->chunk;
    if (count) memcpy(out, host->data + host->positions[handle], count);
    host->positions[handle] += count;
    return count;
}

static int64_t MFK_CALL seek_resource(void *opaque, uint64_t handle, int64_t offset, int whence)
{
    transport *host = opaque;
    CHECK(handle && handle <= (uint64_t)host->handles && !host->closes[handle]);
    host->seeks++; host->seek_offset = offset; host->whence = whence;
    if (host->fail_seek) return -1;
    int64_t position = offset + (whence == SEEK_CUR ? host->positions[handle] :
                                whence == SEEK_END ? host->bytes : 0);
    if (position < 0) return -1;
    return host->positions[handle] = position;
}

static void MFK_CALL close_resource(void *opaque, uint64_t handle)
{
    transport *host = opaque;
    CHECK(handle && handle <= (uint64_t)host->handles && !host->closes[handle]);
    host->closes[handle]++;
}
static int MFK_CALL interrupted(void *opaque) { return ((transport *)opaque)->interrupted; }
static void MFK_CALL failed(void *opaque, int error)
{
    transport *host = opaque;
    host->failures++; host->error = error;
}

static void init(transport *host, const uint8_t *data, int bytes)
{
    memset(host, 0, sizeof(*host));
    host->address = "https://origin.invalid/root";
    host->location = "https://redirect.invalid/path/%2Ffilm?part=1&part=2";
    host->data = data; host->bytes = bytes; host->size = bytes;
    host->chunk = 3; host->seekable = 1;
    host->input.callbacks = (mfk_callbacks) {
        sizeof(mfk_callbacks), MFK_ABI, host, open_resource, resource_location,
        read_resource, seek_resource, close_resource, interrupted, failed
    };
    host->input.uri = (char *)host->address;
    host->format.opaque = &host->input;
    host->format.io_open = mfk_io_open; host->format.io_close2 = mfk_io_close;
}

static AVIOContext *open_io(transport *host, const char *uri, AVDictionary **options)
{
    AVIOContext *io = NULL;
    CHECK(host->format.io_open(&host->format, &io, uri, AVIO_FLAG_READ, options) == 0 && io);
    uint8_t *location = NULL;
    CHECK(av_opt_get(io, "location", AV_OPT_SEARCH_CHILDREN, &location) == 0);
    CHECK(location && !strcmp((char *)location, host->location));
    av_free(location);
    return io;
}

static void close_all(transport *host)
{
    mfk_io_close_all(&host->input);
    CHECK(!host->input.io);
    mfk_io_close_all(&host->input);
    for (int i = 1; i <= host->handles; i++) CHECK(host->closes[i] == 1);
}

static void routing_tests(void)
{
    transport host, other;
    init(&host, NULL, 0); init(&other, NULL, 0);
    AVIOContext *root = open_io(&host, host.input.uri, NULL);
    CHECK(host.opens == 1 && host.locations == 1 && host.offset == 0 && host.end == -1);
    const char *addresses[] = {
        "http://child.invalid/segment", "HtTpS://child.invalid/%2Fkey?x=1&x=2"
    };
    const char *locations[] = {
        "https://redirect.invalid/segment?range=1", "https://keys.invalid/%2Fkey?x=1&x=2"
    };
    for (unsigned i = 0; i < sizeof(addresses) / sizeof(*addresses); i++) {
        host.address = addresses[i]; host.location = locations[i];
        open_io(&host, host.address, NULL);
    }
    open_io(&other, other.input.uri, NULL);
    CHECK(host.format.io_close2(&host.format, root) == 0 && host.closes[1] == 1);
    close_all(&host);
    CHECK(other.closes[1] == 0 && other.input.io);
    close_all(&other);

    const char *denied[] = {
        "file:///synthetic", "tcp://host.invalid:80", "udp://host.invalid:80",
        "data:text/plain,synthetic", "pipe:0", "concat:https://host.invalid/a",
        "httpx://host.invalid/a", "//host.invalid/a", "segment.ts",
        "crypto+file:///synthetic", "crypto:tcp://host.invalid:80",
        "crypto+crypto+https://host.invalid/a"
    };
    init(&host, NULL, 0);
    for (unsigned i = 0; i < sizeof(denied) / sizeof(*denied); i++) {
        AVIOContext *io = NULL;
        CHECK(mfk_io_open(&host.format, &io, denied[i], AVIO_FLAG_READ, NULL) == AVERROR(EPERM));
        CHECK(!io && !host.opens);
    }
    AVIOContext *io = NULL;
    CHECK(mfk_io_open(&host.format, &io, host.address, AVIO_FLAG_WRITE, NULL) == AVERROR_EXIT);
    CHECK(!io && !host.opens);
}

static void range_tests(void)
{
    const struct { const char *offset, *end; int64_t first, last; } ranges[] = {
        { NULL, NULL, 0, -1 }, { "7", "19", 7, 19 }, { "7", "-1", 7, -1 },
        { "4294967301", "4294967401", INT64_C(4294967301), INT64_C(4294967401) }
    };
    for (unsigned i = 0; i < sizeof(ranges) / sizeof(*ranges); i++) {
        transport host; init(&host, NULL, 0);
        AVDictionary *options = NULL;
        if (ranges[i].offset) CHECK(av_dict_set(&options, "offset", ranges[i].offset, 0) == 0);
        if (ranges[i].end) CHECK(av_dict_set(&options, "end_offset", ranges[i].end, 0) == 0);
        AVIOContext *io = open_io(&host, host.address, &options);
        CHECK(host.offset == ranges[i].first && host.end == ranges[i].last);
        CHECK(io->pos == ranges[i].first);
        mfk_clear_dictionary(&options); close_all(&host);
    }
    const char *invalid[][2] = {
        { "-1", NULL }, { "x", NULL }, { "1x", NULL },
        { "9223372036854775808", NULL }, { "0", "-2" },
        { "4", "4" }, { "4", "3" }, { "0", "x" }, { "0", "9223372036854775808" }
    };
    for (unsigned i = 0; i < sizeof(invalid) / sizeof(*invalid); i++) {
        transport host; init(&host, NULL, 0);
        AVDictionary *options = NULL;
        CHECK(av_dict_set(&options, "offset", invalid[i][0], 0) == 0);
        if (invalid[i][1]) CHECK(av_dict_set(&options, "end_offset", invalid[i][1], 0) == 0);
        AVIOContext *io = NULL;
        CHECK(mfk_io_open(&host.format, &io, host.address, AVIO_FLAG_READ, &options) == AVERROR_INVALIDDATA);
        CHECK(!io && !host.opens);
        mfk_clear_dictionary(&options);
    }
}

static void read_seek_tests(void)
{
    uint8_t data[97], out[97];
    for (unsigned i = 0; i < sizeof(data); i++) data[i] = (uint8_t)(i * 37 + 11);
    transport host; init(&host, data, sizeof(data));
    AVDictionary *options = NULL;
    CHECK(av_dict_set(&options, "offset", "7", 0) == 0);
    CHECK(av_dict_set(&options, "end_offset", "19", 0) == 0);
    AVIOContext *io = open_io(&host, host.address, &options);
    CHECK(avio_read(io, out, sizeof(out)) == 12 && !memcmp(out, data + 7, 12));
    CHECK(host.reads > 1 && avio_read(io, out, 1) == AVERROR_EOF);
    mfk_clear_dictionary(&options); close_all(&host);

    init(&host, data, sizeof(data));
    io = open_io(&host, host.address, NULL);
    CHECK(io->seekable == AVIO_SEEKABLE_NORMAL);
    CHECK(io->read_packet(io->opaque, out, 10) == 3 && !memcmp(out, data, 3));
    CHECK(io->seek(io->opaque, 0, AVSEEK_SIZE) == sizeof(data) && !host.seeks);
    CHECK(io->seek(io->opaque, 20, SEEK_SET | AVSEEK_FORCE) == 20);
    CHECK(host.seek_offset == 20 && host.whence == SEEK_SET);
    CHECK(io->read_packet(io->opaque, out, 10) == 3 && !memcmp(out, data + 20, 3));
    CHECK(io->seek(io->opaque, -2, SEEK_CUR) == 21 && host.whence == SEEK_CUR);
    CHECK(io->seek(io->opaque, -1, SEEK_END) == 96 && host.whence == SEEK_END);
    CHECK(io->read_packet(io->opaque, out, 10) == 1 && out[0] == data[96]);
    CHECK(io->read_packet(io->opaque, out, 10) == AVERROR_EOF);
    host.fail_seek = 1;
    CHECK(io->seek(io->opaque, 0, SEEK_SET) == AVERROR(EIO));
    host.size = -1; close_all(&host);
    io = open_io(&host, host.address, NULL);
    CHECK(io->seek(io->opaque, 0, AVSEEK_SIZE) == AVERROR(ENOSYS));
    close_all(&host);
    init(&host, data, sizeof(data)); host.seekable = 0;
    io = open_io(&host, host.address, NULL);
    CHECK(!io->seekable && io->seek(io->opaque, 0, SEEK_SET) == AVERROR(ENOSYS));
    CHECK(io->seek(io->opaque, 0, AVSEEK_SIZE) == sizeof(data) && !host.seeks);
    close_all(&host);
}

static void failure_tests(void)
{
    uint8_t data[] = { 1, 2, 3 }, out[8];
    transport host; init(&host, data, sizeof(data));
    AVIOContext *io = open_io(&host, host.address, NULL);
    host.fail_read = -1;
    CHECK(io->read_packet(io->opaque, out, sizeof(out)) == AVERROR(EIO));
    host.fail_read = 1;
    CHECK(io->read_packet(io->opaque, out, sizeof(out)) == AVERROR(EIO));
    host.fail_read = 0; host.cancel_read = 1;
    CHECK(io->read_packet(io->opaque, out, sizeof(out)) == AVERROR_EXIT && host.reads == 3);
    CHECK(io->read_packet(io->opaque, out, sizeof(out)) == AVERROR_EXIT && host.reads == 3);
    CHECK(io->seek(io->opaque, 0, SEEK_SET) == AVERROR_EXIT && !host.seeks);
    mfk_failure(&host.input, MFK_READ_FAILED);
    CHECK(!host.failures);
    close_all(&host);

    init(&host, data, sizeof(data));
    io = open_io(&host, host.address, NULL);
    InterlockedExchange(&host.input.cancelled, 1);
    CHECK(io->read_packet(io->opaque, out, sizeof(out)) == AVERROR_EXIT && !host.reads);
    CHECK(io->seek(io->opaque, 0, AVSEEK_SIZE) == AVERROR_EXIT);
    AVIOContext *child = NULL;
    CHECK(mfk_io_open(&host.format, &child, host.address, AVIO_FLAG_READ, NULL) == AVERROR_EXIT);
    CHECK(!child && host.opens == 1);
    close_all(&host);

    init(&host, NULL, 0); host.fail_open = 1;
    io = NULL;
    CHECK(mfk_io_open(&host.format, &io, host.address, AVIO_FLAG_READ, NULL) == AVERROR(EIO));
    CHECK(!io && host.opens == 1 && !host.handles && !host.locations);
    close_all(&host);
    init(&host, NULL, 0); host.fail_location = 1;
    CHECK(mfk_io_open(&host.format, &io, host.address, AVIO_FLAG_READ, NULL) == AVERROR(EIO));
    CHECK(!io && host.locations == 1 && host.closes[1] == 1);
    close_all(&host);
    mfk_failure(&host.input, MFK_READ_FAILED);
    CHECK(host.failures == 1 && host.error == MFK_READ_FAILED);
}

static AVDictionary *crypto_options(void)
{
    AVDictionary *options = NULL;
    CHECK(av_dict_set(&options, "key", "000102030405060708090a0b0c0d0e0f", 0) == 0);
    CHECK(av_dict_set(&options, "iv", "101112131415161718191a1b1c1d1e1f", 0) == 0);
    return options;
}

static uint8_t *encrypt(const uint8_t *plain, int bytes)
{
    uint8_t key[16], iv[16];
    for (int i = 0; i < 16; i++) { key[i] = (uint8_t)i; iv[i] = (uint8_t)(i + 16); }
    struct AVAES *aes = av_aes_alloc();
    uint8_t *cipher = av_malloc(bytes);
    CHECK(aes && cipher && bytes % 16 == 0 && av_aes_init(aes, key, 128, 0) == 0);
    av_aes_crypt(aes, cipher, plain, bytes / 16, iv, 0);
    av_free(aes);
    return cipher;
}

static int crypto_tests(void)
{
    const int lengths[] = { 0, 1, 15, 16, 17, 31, 32, MFK_IO_BUFFER - 1,
                            MFK_IO_BUFFER, MFK_IO_BUFFER + 17 };
    const int chunks[] = { 1, 15, 16, 17, 31, MFK_IO_BUFFER };
    for (unsigned i = 0; i < sizeof(lengths) / sizeof(*lengths); i++) {
        int length = lengths[i], padding = 16 - length % 16, bytes = length + padding;
        uint8_t *plain = av_malloc(bytes), *out = av_malloc(bytes);
        CHECK(plain && out);
        for (int n = 0; n < length; n++) plain[n] = (uint8_t)(n * 37 + 11);
        memset(plain + length, padding, padding);
        uint8_t *cipher = encrypt(plain, bytes);
        for (unsigned j = 0; j < sizeof(chunks) / sizeof(*chunks); j++) {
            transport host; init(&host, cipher, bytes); host.chunk = chunks[j];
            AVDictionary *options = crypto_options();
            AVIOContext *io = open_io(&host, j % 2 ? "crypto:https://origin.invalid/root" :
                                                       "crypto+https://origin.invalid/root", &options);
            CHECK(!io->seekable && io->seek(io->opaque, 0, AVSEEK_SIZE) == AVERROR(ENOSYS));
            CHECK(io->seek(io->opaque, 0, SEEK_SET) == AVERROR(ENOSYS) && !host.seeks);
            int total = 0, result;
            while ((result = io->read_packet(io->opaque, out + total, 7)) > 0) {
                CHECK(result <= 7 && total + result <= length);
                total += result;
            }
            CHECK(result == AVERROR_EOF && total == length && !memcmp(out, plain, length));
            CHECK(host.positions[1] == bytes);
            host.interrupted = 1;
            CHECK(io->read_packet(io->opaque, out, 1) == AVERROR_EXIT);
            mfk_clear_dictionary(&options); close_all(&host);
        }
        av_free(cipher); av_free(plain); av_free(out);
    }

    const char *invalid[] = { NULL, "00", "000102030405060708090a0b0c0d0e0g" };
    for (unsigned i = 0; i < sizeof(invalid) / sizeof(*invalid); i++) {
        for (int iv = 0; iv < 2; iv++) {
            transport host; init(&host, NULL, 0);
            AVDictionary *options = crypto_options();
            CHECK(av_dict_set(&options, iv ? "iv" : "key", invalid[i], 0) == 0);
            AVIOContext *io = NULL;
            CHECK(mfk_io_open(&host.format, &io, "crypto+https://origin.invalid/root",
                             AVIO_FLAG_READ, &options) == AVERROR_INVALIDDATA);
            CHECK(!io && host.handles == 1 && host.closes[1] == 1);
            mfk_clear_dictionary(&options); close_all(&host);
        }
    }

    uint8_t block[32], output[32];
    memset(block, 2, sizeof(block));
    uint8_t *cipher = encrypt(block, sizeof(block));
    for (int mode = 0; mode < 3; mode++) {
        transport host; init(&host, cipher, sizeof(block));
        AVDictionary *options = crypto_options();
        AVIOContext *io = open_io(&host, "crypto+https://origin.invalid/root", &options);
        if (mode == 2) {
            CHECK(io->read_packet(io->opaque, output, 1) == 1 && output[0] == block[0]);
            host.interrupted = 1;
        } else host.fail_read = mode ? 1 : -1;
        int reads = host.reads;
        CHECK(io->read_packet(io->opaque, output, sizeof(output)) ==
              (mode == 2 ? AVERROR_EXIT : AVERROR(EIO)));
        CHECK(host.reads == reads + (mode == 2 ? 0 : 1));
        mfk_clear_dictionary(&options); close_all(&host);
    }
    av_free(cipher);
    puts("film_io_tests: AES-CBC streaming, keys and failure/cancellation passed");

    /* A streamed prefix is allowed; the malformed final block is not.
     * FFmpeg's EOF probe may call read_packet again after a read error. */
    int padding_failures = 0;
    for (int kind = 0; kind < 6; kind++) {
        uint8_t plain[32], out[32];
        memset(plain, 2, sizeof(plain));
        if (kind == 0) plain[31] = 0;
        if (kind == 1) plain[31] = 17;
        if (kind == 2) plain[30] = 3;
        uint8_t *cipher = encrypt(plain, sizeof(plain));
        transport host; init(&host, cipher, kind < 3 ? 32 : kind == 3 ? 1 : kind == 4 ? 15 : 17);
        AVDictionary *options = crypto_options();
        AVIOContext *io = open_io(&host, "crypto+https://origin.invalid/root", &options);
        int total = 0, result;
        while ((result = avio_read(io, out, sizeof(out))) > 0) {
            CHECK(total + result <= (kind < 3 ? 16 : 0) && !memcmp(out, plain + total, result));
            total += result;
        }
        CHECK(result == AVERROR_INVALIDDATA);
        CHECK(avio_read(io, out, sizeof(out)) == AVERROR_INVALIDDATA);
        int eof = avio_feof(io);
        result = avio_read(io, out, sizeof(out));
        if (!eof || result != AVERROR_INVALIDDATA) {
            fprintf(stderr, "invalid padding case %d: EOF probe=%d, subsequent read=%d\n", kind, eof, result);
            padding_failures++;
        }
        mfk_clear_dictionary(&options); close_all(&host); av_free(cipher);
    }
    return padding_failures;
}

int main(void)
{
    routing_tests();
    range_tests();
    read_seek_tests();
    failure_tests();
    puts("film_io_tests: routing, ranges and read/seek/failure/cancellation passed");
    if (crypto_tests()) return EXIT_FAILURE;
    puts("film_io_tests: routing, ranges, read/seek, failure/cancellation and AES-CBC passed");
    return 0;
}
