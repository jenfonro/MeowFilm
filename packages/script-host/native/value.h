#pragma once
#include <node_api.h>
#include "quickjs.h"
#include <cmath>
#include <cstdint>
#include <memory>
#include <stdexcept>
#include <string>
#include <utility>
#include <vector>

namespace meow {
constexpr unsigned kDataDepth = 64;
constexpr double kSafeInteger = 9007199254740991.0;
using Bytes = std::vector<uint8_t>;

struct Fault : std::runtime_error {
  explicit Fault(const std::string& code) : std::runtime_error(code) {}
};
void Check(bool ok, const char* code = "INVALID_ARGUMENT");
void NCheck(napi_status status);
bool ValidUnicode(const std::u16string& text);
std::string Utf8(const std::u16string& text);
std::u16string Utf16(const std::string& text);
bool SafeCode(const std::string& text);

struct Value {
  enum Kind { Null, Boolean, Number, String, Array, Object, Binary } kind = Null;
  bool boolean = false;
  double number = 0;
  std::u16string string;
  std::vector<Value> array;
  std::vector<std::pair<std::u16string, Value>> object;
  std::shared_ptr<Bytes> bytes;
  static Value Bool(bool b);
  static Value Num(double n);
  static Value Str(const std::string& s);
  static Value Bin(Bytes b);
  static Value Obj();
  void Set(const char* name, Value v);
  const Value& Get(const char* name) const;
  const Value& At(size_t index) const;
  std::string Text() const;
  uint64_t Integer() const;
};

struct QValue {
  JSContext* ctx;
  JSValue value;
  QValue(JSContext* c, JSValue v) : ctx(c), value(v) {}
  ~QValue() { JS_FreeValue(ctx, value); }
  QValue(const QValue&) = delete;
  QValue& operator=(const QValue&) = delete;
  QValue(QValue&& other) noexcept : ctx(other.ctx), value(other.value) { other.value = JS_UNDEFINED; }
  JSValue Release() { JSValue v = value; value = JS_UNDEFINED; return v; }
};

std::u16string QString(JSContext* ctx, JSValueConst value);
std::string ExceptionCode(JSContext* ctx);
JSValue QError(JSContext* ctx, const std::string& code);
JSValue QCheck(JSContext* ctx, JSValue value);
Value FromNode(napi_env env, napi_value value, unsigned depth = 0);
// Positional lists are transport structure; each argument is a data root.
Value ArgsFromNode(napi_env env, napi_value args);
napi_value ToNode(napi_env env, const Value& value);
Value FromQuick(JSContext* ctx, JSValueConst value, unsigned depth = 0);
Value ArgsFromQuick(JSContext* ctx, JSValueConst args);
JSValue ToQuick(JSContext* ctx, const Value& value);
}  // namespace meow
