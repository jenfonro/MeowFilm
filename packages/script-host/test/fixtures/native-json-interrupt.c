#include "quickjs.h"
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <time.h>
#ifdef _WIN32
#include <fcntl.h>
#include <io.h>
#endif

/* A raw same-Context probe: a permanent cancellation flag or owner reset must
 * not be able to hide a JavaScript catch that swallowed the native interrupt. */
typedef struct {
    int fired;
    int64_t deadline;
} Pulse;

static int64_t now_ms(void)
{
    struct timespec now;
    timespec_get(&now, TIME_UTC);
    return (int64_t)now.tv_sec * 1000 + now.tv_nsec / 1000000;
}

static int interrupt(JSContext *ctx, void *opaque)
{
    Pulse *pulse = opaque;
    (void)ctx;
    if (!pulse->fired && pulse->deadline && now_ms() >= pulse->deadline) {
        pulse->fired = 1;
        return 1;
    }
    return 0;
}

static char *frame(size_t *length)
{
    uint8_t header[4];
    if (fread(header, 1, 4, stdin) != 4)
        return NULL;
    *length = (size_t)header[0] | (size_t)header[1] << 8 |
              (size_t)header[2] << 16 | (size_t)header[3] << 24;
    char *text = malloc(*length + 1);
    if (!text)
        return NULL;
    if (fread(text, 1, *length, stdin) != *length) {
        free(text);
        return NULL;
    }
    text[*length] = 0;
    return text;
}

static JSValue invoke(JSContext *ctx, JSValue function, const char *text, size_t length)
{
    JSValue input = JS_ParseJSON(ctx, text, length, "<request>");
    if (JS_IsException(input))
        return input;
    JSValue value = JS_Call(ctx, function, JS_UNDEFINED, 1, &input);
    JS_FreeValue(ctx, input);
    return value;
}

int main(void)
{
#ifdef _WIN32
    _setmode(_fileno(stdin), _O_BINARY);
#endif
    size_t lengths[4];
    char *texts[4] = {0};
    JSRuntime *rt = NULL;
    JSContext *ctx = NULL;
    JSValue function = JS_UNDEFINED;
    int status = 1;
    for (int i = 0; i < 4; i++) {
        texts[i] = frame(&lengths[i]);
        if (!texts[i])
            goto done;
    }
    rt = JS_NewRuntime();
    if (!rt)
        goto done;
    JS_SetMemoryLimit(rt, 64 * 1024 * 1024);
    JS_SetMaxStackSize(rt, 512 * 1024);
    ctx = JS_NewContext(rt);
    if (!ctx)
        goto done;
    Pulse pulse = {0};
    JS_SetInterruptHandler(rt, interrupt, &pulse);
    JSValue value = JS_Eval(ctx, texts[0], lengths[0], "<application>", JS_EVAL_TYPE_GLOBAL);
    if (JS_IsException(value))
        goto done;
    JS_FreeValue(ctx, value);
    JSValue global = JS_GetGlobalObject(ctx);
    function = JS_GetPropertyStr(ctx, global, "meowfilmProcess");
    JS_FreeValue(ctx, global);
    value = invoke(ctx, function, texts[1], lengths[1]);
    if (JS_IsException(value))
        goto done;
    JS_FreeValue(ctx, value);
    int64_t started = now_ms();
    pulse.deadline = started + 100;
    value = invoke(ctx, function, texts[2], lengths[2]);
    int failed = JS_IsException(value);
    JS_FreeValue(ctx, value);
    int64_t elapsed = now_ms() - started;
    if (!pulse.fired || !failed || elapsed >= 5000)
        goto done;
    value = JS_GetException(ctx);
    JS_FreeValue(ctx, value);
    pulse.deadline = 0;
    value = invoke(ctx, function, texts[3], lengths[3]);
    if (JS_IsException(value))
        goto done;
    JSValue json = JS_JSONStringify(ctx, value, JS_UNDEFINED, JS_UNDEFINED);
    JS_FreeValue(ctx, value);
    const char *output = JS_ToCString(ctx, json);
    if (output) {
        printf("{\"fired\":%d,\"failed\":true,\"elapsedMs\":%lld,\"probe\":%s}\n",
               pulse.fired, (long long)elapsed, output);
        JS_FreeCString(ctx, output);
        status = 0;
    }
    JS_FreeValue(ctx, json);
done:
    if (ctx) {
        JSValue error = JS_GetException(ctx);
        JS_FreeValue(ctx, error);
        JS_FreeValue(ctx, function);
        JS_FreeContext(ctx);
    }
    if (rt)
        JS_FreeRuntime(rt);
    for (int i = 0; i < 4; i++)
        free(texts[i]);
    return status;
}
