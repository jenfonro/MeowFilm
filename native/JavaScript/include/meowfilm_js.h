#ifndef MEOWFILM_JS_H
#define MEOWFILM_JS_H

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct mf_engine mf_engine;
typedef int (*mf_interrupt)(void *);

mf_engine *mf_js_create(const char *source, size_t length,
    size_t memory_limit, size_t stack_limit, mf_interrupt interrupt, void *opaque);
int mf_js_invoke(mf_engine *engine, const char *json, size_t length,
    char **output, size_t *output_length);
void mf_js_free(void *memory);
void mf_js_destroy(mf_engine *engine);

#ifdef __cplusplus
}
#endif
#endif
