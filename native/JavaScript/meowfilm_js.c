#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include "quickjs.h"

#if defined(_WIN32)
#define MF_EXPORT __declspec(dllexport)
#else
#define MF_EXPORT __attribute__((visibility("default")))
#endif

typedef int (*mf_interrupt)(void *);
typedef struct {
    JSRuntime *runtime;
    JSContext *context;
    JSValue function;
    mf_interrupt interrupt;
    void *opaque;
    int interrupted;
} mf_engine;

static int interrupt_handler(JSContext *context, void *opaque)
{
    (void)context;
    mf_engine *engine = opaque;
    if (engine->interrupt && engine->interrupt(engine->opaque)) {
        engine->interrupted = 1;
        return 1;
    }
    return 0;
}

MF_EXPORT void mf_js_destroy(mf_engine *engine)
{
    if (!engine) return;
    if (engine->context) {
        JS_FreeValue(engine->context, engine->function);
        JS_FreeContext(engine->context);
    }
    if (engine->runtime) JS_FreeRuntime(engine->runtime);
    free(engine);
}

/* All functions except mf_js_free are called by the one owning thread.
 * The interrupt callback only reads cancellation/deadline state. */
MF_EXPORT mf_engine *mf_js_create(const char *source, size_t length,
    size_t memory_limit, size_t stack_limit, mf_interrupt interrupt, void *opaque)
{
    mf_engine *engine = calloc(1, sizeof(*engine));
    if (!engine) return NULL;
    engine->function = JS_UNDEFINED;
    engine->interrupt = interrupt;
    engine->opaque = opaque;
    engine->runtime = JS_NewRuntime();
    if (!engine->runtime) goto failed;
    JS_SetMemoryLimit(engine->runtime, memory_limit);
    JS_SetMaxStackSize(engine->runtime, stack_limit);
    JS_SetInterruptHandler(engine->runtime, interrupt_handler, engine);
    JS_SetCanBlock(engine->runtime, 0);
    engine->context = JS_NewContext(engine->runtime);
    if (!engine->context) goto failed;
    JSValue result = JS_Eval(engine->context, source, length, "<application>", JS_EVAL_TYPE_GLOBAL);
    if (JS_IsException(result)) goto failed;
    JS_FreeValue(engine->context, result);
    JSValue global = JS_GetGlobalObject(engine->context);
    engine->function = JS_GetPropertyStr(engine->context, global, "meowfilmProcess");
    JS_FreeValue(engine->context, global);
    if (!JS_IsFunction(engine->context, engine->function) || engine->interrupted) goto failed;
    return engine;
failed:
    if (engine->context) {
        JSValue error = JS_GetException(engine->context);
        JS_FreeValue(engine->context, error);
    }
    mf_js_destroy(engine);
    return NULL;
}

/* Returns 0 on success, 1 for a JS/serialization failure, 2 for interruption.
 * No JSValue or borrowed QuickJS allocation crosses the C ABI. */
MF_EXPORT int mf_js_invoke(mf_engine *engine, const char *json, size_t length,
    char **output, size_t *output_length)
{
    *output = NULL;
    *output_length = 0;
    engine->interrupted = 0;
    JSContext *ctx = engine->context;
    JSValue request = JS_ParseJSON(ctx, json, length, "<request>");
    JSValue value = JS_UNDEFINED, serialized = JS_UNDEFINED;
    const char *text = NULL;
    int status = 1;
    if (JS_IsException(request)) goto done;
    value = JS_Call(ctx, engine->function, JS_UNDEFINED, 1, &request);
    if (JS_IsException(value)) goto done;
    serialized = JS_JSONStringify(ctx, value, JS_UNDEFINED, JS_UNDEFINED);
    if (JS_IsException(serialized) || JS_IsUndefined(serialized)) goto done;
    size_t size;
    text = JS_ToCStringLen(ctx, &size, serialized);
    if (!text || size == SIZE_MAX) goto done;
    char *copy = malloc(size + 1);
    if (!copy) goto done;
    memcpy(copy, text, size + 1);
    *output = copy;
    *output_length = size;
    status = 0;
done:
    if (text) JS_FreeCString(ctx, text);
    JS_FreeValue(ctx, serialized);
    JS_FreeValue(ctx, value);
    JS_FreeValue(ctx, request);
    if (status) {
        JSValue error = JS_GetException(ctx);
        JS_FreeValue(ctx, error);
    }
    if (engine->interrupted) status = 2;
    return status;
}

MF_EXPORT void mf_js_free(void *memory)
{
    free(memory);
}
