#ifndef MEOWFILM_JS_H
#define MEOWFILM_JS_H

#include <stddef.h>

#if defined(_WIN32)
#define MF_EXPORT __declspec(dllexport)
#else
#define MF_EXPORT __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct mf_engine mf_engine;
typedef int (*mf_interrupt)(void *);

MF_EXPORT mf_engine *mf_js_create(const char *source, size_t length,
    size_t memory_limit, size_t stack_limit, mf_interrupt interrupt, void *opaque);
MF_EXPORT int mf_js_invoke(mf_engine *engine, const char *json, size_t length,
    char **output, size_t *output_length);
MF_EXPORT void mf_js_free(void *memory);
MF_EXPORT void mf_js_destroy(mf_engine *engine);

#ifdef __cplusplus
}
#endif
#endif
