import FilmRuleProgram

/// Adapts result text to Foundation's scalar-only JSON strings. The shared
/// program still performs every rule operation before this transport step.
enum FilmRuleNativeProgram {
    static func load() throws -> String {
        try FilmRuleProgram.load() + """

        ;(function () {
          'use strict';
          const process = globalThis.meowfilmProcess;
          function text(value) {
            if (value === null || value.isWellFormed()) return value;
            return Array.from({ length: value.length }, (_, index) => value.charCodeAt(index));
          }
          globalThis.meowfilmProcess = function (request) {
            const result = process(request);
            if (request.operation !== 'process') return result;
            for (const key of ['text', 'inputText', 'processedText', 'rewrittenText', 'groupKey', 'trailingDigits']) {
              result[key] = text(result[key]);
            }
            result.previews = result.previews.map(text);
            for (const error of result.errors) error.text = text(error.text);
            return result;
          };
        })();
        """
    }
}
