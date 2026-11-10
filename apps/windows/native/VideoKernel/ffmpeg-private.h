#ifndef MEOWFILM_PRIVATE_FFMPEG_H
#define MEOWFILM_PRIVATE_FFMPEG_H

#include <stddef.h>
#include <stdint.h>
#include <string.h>
#include "libavutil/avstring.h"

/* Private static FFmpeg only. No protocol is registered or enabled by this
 * scheme recognition: every root and child still goes through host io_open. */
static inline const char *meowfilm_http_protocol(const char *url)
{
    if (!av_strncasecmp(url, "https://", 8)) return "https";
    if (!av_strncasecmp(url, "http://", 7)) return "http";
    return NULL;
}

static inline void meowfilm_erase(void *memory, size_t length)
{
    volatile uint8_t *bytes = memory;
    while (length--) *bytes++ = 0;
}
static inline void meowfilm_erase_string(char *value)
{
    if (value) meowfilm_erase(value, strlen(value));
}

#endif
