#include "value.h"
#include <algorithm>
#include <cstring>
#include <limits>

namespace meow {
void Check(bool ok, const char* code) { if (!ok) throw Fault(code); }
void NCheck(napi_status status) { Check(status == napi_ok); }
bool SafeCode(const std::string& s) {
  if (s.empty() || s.size() > 80) return false;
  for (char c : s) if (!((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_')) return false;
  return true;
}
bool ValidUnicode(const std::u16string& s) {
  for (size_t i = 0; i < s.size(); ++i) {
    auto c = s[i];
    if (c >= 0xd800 && c <= 0xdbff) {
      if (++i == s.size() || s[i] < 0xdc00 || s[i] > 0xdfff) return false;
    } else if (c >= 0xdc00 && c <= 0xdfff) return false;
  }
  return true;
}
std::string Utf8(const std::u16string& s) {
  Check(ValidUnicode(s));
  std::string out;
  out.reserve(s.size());
  for (size_t i = 0; i < s.size(); ++i) {
    uint32_t c = s[i];
    if (c >= 0xd800 && c <= 0xdbff) c = 0x10000 + ((c - 0xd800) << 10) + (s[++i] - 0xdc00);
    if (c < 0x80) out.push_back(static_cast<char>(c));
    else if (c < 0x800) {
      out.push_back(static_cast<char>(0xc0 | (c >> 6)));
      out.push_back(static_cast<char>(0x80 | (c & 63)));
    } else if (c < 0x10000) {
      out.push_back(static_cast<char>(0xe0 | (c >> 12)));
      out.push_back(static_cast<char>(0x80 | ((c >> 6) & 63)));
      out.push_back(static_cast<char>(0x80 | (c & 63)));
    } else {
      out.push_back(static_cast<char>(0xf0 | (c >> 18)));
      out.push_back(static_cast<char>(0x80 | ((c >> 12) & 63)));
      out.push_back(static_cast<char>(0x80 | ((c >> 6) & 63)));
      out.push_back(static_cast<char>(0x80 | (c & 63)));
    }
  }
  return out;
}
std::u16string Utf16(const std::string& s) {
  std::u16string out;
  for (size_t i = 0; i < s.size();) {
    uint32_t c = static_cast<uint8_t>(s[i++]), min = 0;
    unsigned n = 0;
    if (c >= 0xc2 && c <= 0xdf) { c &= 31; n = 1; min = 0x80; }
    else if (c >= 0xe0 && c <= 0xef) { c &= 15; n = 2; min = 0x800; }
    else if (c >= 0xf0 && c <= 0xf4) { c &= 7; n = 3; min = 0x10000; }
    else Check(c < 0x80);
    Check(i + n <= s.size());
    for (unsigned j = 0; j < n; ++j) {
      auto b = static_cast<uint8_t>(s[i++]); Check((b & 0xc0) == 0x80);
      c = (c << 6) | (b & 63);
    }
    Check(c >= min && c <= 0x10ffff && !(c >= 0xd800 && c <= 0xdfff));
    if (c >= 0x10000) {
      c -= 0x10000; out.push_back(static_cast<char16_t>(0xd800 | (c >> 10)));
      out.push_back(static_cast<char16_t>(0xdc00 | (c & 1023)));
    } else out.push_back(static_cast<char16_t>(c));
  }
  return out;
}
Value Value::Bool(bool b) { Value v; v.kind = Boolean; v.boolean = b; return v; }
Value Value::Num(double n) { Value v; v.kind = Number; v.number = n; return v; }
Value Value::Str(const std::string& s) { Value v; v.kind = String; v.string = Utf16(s); return v; }
Value Value::Bin(Bytes b) { Value v; v.kind = Binary; v.bytes = std::make_shared<Bytes>(std::move(b)); return v; }
Value Value::Obj() { Value v; v.kind = Object; return v; }
void Value::Set(const char* name, Value v) { object.emplace_back(Utf16(name), std::move(v)); }
const Value& Value::Get(const char* name) const {
  static const Value empty;
  auto key = Utf16(name);
  for (const auto& entry : object) if (entry.first == key) return entry.second;
  return empty;
}
const Value& Value::At(size_t i) const {
  static const Value empty;
  return i < array.size() ? array[i] : empty;
}
std::string Value::Text() const { Check(kind == String); return Utf8(string); }
uint64_t Value::Integer() const {
  Check(kind == Number && std::isfinite(number) && number >= 0 &&
        number <= kSafeInteger && std::floor(number) == number);
  return static_cast<uint64_t>(number);
}

std::u16string QString(JSContext* ctx, JSValueConst value) {
  Check(JS_IsString(value));
  size_t length;
  const uint16_t* data = JS_ToCStringLenUTF16(ctx, &length, value);
  Check(data != nullptr, "RESOURCE_LIMIT");
  std::u16string result;
  try { result.assign(reinterpret_cast<const char16_t*>(data), length); }
  catch (...) { JS_FreeCStringUTF16(ctx, data); throw; }
  JS_FreeCStringUTF16(ctx, data);
  Check(ValidUnicode(result));
  return result;
}
std::string ExceptionCode(JSContext* ctx) {
  QValue error(ctx, JS_GetException(ctx));
  if (JS_IsObject(error.value)) {
    QValue code(ctx, JS_GetPropertyStr(ctx, error.value, "code"));
    if (JS_IsString(code.value)) {
      try { auto s = Utf8(QString(ctx, code.value)); if (SafeCode(s)) return s; }
      catch (...) {}
    }
    if (JS_IsException(code.value)) { QValue ignored(ctx, JS_GetException(ctx)); }
    QValue name(ctx, JS_GetPropertyStr(ctx, error.value, "name"));
    QValue message(ctx, JS_GetPropertyStr(ctx, error.value, "message"));
    if (JS_IsString(name.value) && JS_IsString(message.value)) {
      try {
        if (Utf8(QString(ctx, name.value)) == "InternalError") {
          auto text = Utf8(QString(ctx, message.value));
          if (text.find("stack overflow") != std::string::npos ||
              text.find("out of memory") != std::string::npos ||
              text.find("allocation failed") != std::string::npos) return "RESOURCE_LIMIT";
        }
      } catch (...) {}
    }
    if (JS_IsException(name.value) || JS_IsException(message.value)) {
      QValue ignored(ctx, JS_GetException(ctx));
    }
  }
  return "SCRIPT_EXCEPTION";
}
JSValue QError(JSContext* ctx, const std::string& code) {
  QValue error(ctx, JS_NewError(ctx));
  if (JS_IsException(error.value)) return error.Release();
  QValue text(ctx, JS_NewStringLen(ctx, code.data(), code.size()));
  if (JS_IsException(text.value)) return text.Release();
  if (JS_DefinePropertyValueStr(ctx, error.value, "code", JS_DupValue(ctx, text.value),
      JS_PROP_C_W_E | JS_PROP_THROW) < 0)
    return JS_HasException(ctx) ? JS_EXCEPTION : JS_ThrowOutOfMemory(ctx);
  if (JS_DefinePropertyValueStr(ctx, error.value, "message", text.Release(),
      JS_PROP_C_W_E | JS_PROP_THROW) < 0)
    return JS_HasException(ctx) ? JS_EXCEPTION : JS_ThrowOutOfMemory(ctx);
  return error.Release();
}
JSValue QCheck(JSContext* ctx, JSValue value) {
  if (JS_IsException(value)) throw Fault(ExceptionCode(ctx));
  return value;
}

static std::u16string NodeString(napi_env env, napi_value v) {
  size_t len;
  NCheck(napi_get_value_string_utf16(env, v, nullptr, 0, &len));
  std::u16string s(len + 1, 0);
  NCheck(napi_get_value_string_utf16(env, v, s.data(), s.size(), &len));
  s.resize(len); Check(ValidUnicode(s)); return s;
}
static Value CopyBytes(const void* data, size_t size) {
  Check(data || !size);
  Bytes b(size);
  if (size) std::memcpy(b.data(), data, size);
  return Value::Bin(std::move(b));
}
Value FromNode(napi_env env, napi_value v, unsigned depth) {
  Check(depth <= kDataDepth);
  napi_valuetype type; NCheck(napi_typeof(env, v, &type));
  if (type == napi_undefined || type == napi_null) return {};
  if (type == napi_boolean) { bool b; NCheck(napi_get_value_bool(env, v, &b)); return Value::Bool(b); }
  if (type == napi_number) {
    double n; NCheck(napi_get_value_double(env, v, &n));
    Check(std::isfinite(n) && std::abs(n) <= kSafeInteger); return Value::Num(n);
  }
  if (type == napi_string) { Value r; r.kind = Value::String; r.string = NodeString(env, v); return r; }
  Check(type == napi_object);
  bool yes;
  NCheck(napi_is_buffer(env, v, &yes));
  if (yes) { void* p; size_t n; NCheck(napi_get_buffer_info(env, v, &p, &n)); return CopyBytes(p, n); }
  NCheck(napi_is_typedarray(env, v, &yes));
  if (yes) {
    napi_typedarray_type kind; size_t count, offset; void* p; napi_value buffer;
    NCheck(napi_get_typedarray_info(env, v, &kind, &count, &p, &buffer, &offset));
    size_t width = 1;
    switch (kind) {
      case napi_int16_array: case napi_uint16_array: width = 2; break;
      case napi_int32_array: case napi_uint32_array: case napi_float32_array: width = 4; break;
      case napi_float64_array: case napi_bigint64_array: case napi_biguint64_array: width = 8; break;
      default: break;
    }
    Check(count <= std::numeric_limits<size_t>::max() / width);
    return CopyBytes(p, count * width);
  }
  NCheck(napi_is_dataview(env, v, &yes));
  if (yes) {
    size_t n, offset; void* p; napi_value buffer;
    NCheck(napi_get_dataview_info(env, v, &n, &p, &buffer, &offset)); return CopyBytes(p, n);
  }
  NCheck(napi_is_arraybuffer(env, v, &yes));
  if (yes) {
    void* p; size_t n; NCheck(napi_get_arraybuffer_info(env, v, &p, &n)); return CopyBytes(p, n);
  }
  NCheck(napi_is_array(env, v, &yes));
  Value out;
  if (yes) {
    out.kind = Value::Array;
    uint32_t length; NCheck(napi_get_array_length(env, v, &length)); out.array.reserve(length);
    for (uint32_t i = 0; i < length; ++i) {
      napi_value item; NCheck(napi_get_element(env, v, i, &item));
      out.array.push_back(FromNode(env, item, depth + 1));
    }
  } else {
    out.kind = Value::Object;
    napi_value keys;
    NCheck(napi_get_all_property_names(env, v, napi_key_own_only,
      static_cast<napi_key_filter>(napi_key_enumerable | napi_key_skip_symbols),
      napi_key_numbers_to_strings, &keys));
    uint32_t length; NCheck(napi_get_array_length(env, keys, &length)); out.object.reserve(length);
    for (uint32_t i = 0; i < length; ++i) {
      napi_value key, item; NCheck(napi_get_element(env, keys, i, &key));
      NCheck(napi_get_property(env, v, key, &item));
      out.object.emplace_back(NodeString(env, key), FromNode(env, item, depth + 1));
    }
  }
  return out;
}
Value ArgsFromNode(napi_env env, napi_value args) {
  bool array; NCheck(napi_is_array(env, args, &array)); Check(array);
  Value out; out.kind = Value::Array;
  uint32_t length; NCheck(napi_get_array_length(env, args, &length)); out.array.reserve(length);
  for (uint32_t i = 0; i < length; ++i) {
    napi_value item; NCheck(napi_get_element(env, args, i, &item));
    out.array.push_back(FromNode(env, item));
  }
  return out;
}
napi_value ToNode(napi_env env, const Value& v) {
  napi_value out;
  switch (v.kind) {
    case Value::Null: NCheck(napi_get_null(env, &out)); break;
    case Value::Boolean: NCheck(napi_get_boolean(env, v.boolean, &out)); break;
    case Value::Number: NCheck(napi_create_double(env, v.number, &out)); break;
    case Value::String:
      NCheck(napi_create_string_utf16(env, v.string.data(), v.string.size(), &out)); break;
    case Value::Binary: {
      if (v.bytes->empty()) { void* p; NCheck(napi_create_buffer(env, 0, &p, &out)); break; }
      auto* owner = new std::shared_ptr<Bytes>(v.bytes);
      napi_status status = napi_create_external_buffer(env, v.bytes->size(), v.bytes->data(),
        [](napi_env, void*, void* hint) { delete static_cast<std::shared_ptr<Bytes>*>(hint); }, owner, &out);
      if (status != napi_ok) { delete owner; NCheck(status); }
      break;
    }
    case Value::Array:
      NCheck(napi_create_array_with_length(env, v.array.size(), &out));
      for (size_t i = 0; i < v.array.size(); ++i)
        NCheck(napi_set_element(env, out, static_cast<uint32_t>(i), ToNode(env, v.array[i])));
      break;
    case Value::Object:
      NCheck(napi_create_object(env, &out));
      for (const auto& item : v.object) {
        napi_value key; NCheck(napi_create_string_utf16(env, item.first.data(), item.first.size(), &key));
        napi_property_descriptor desc = {};
        desc.name = key; desc.value = ToNode(env, item.second); desc.attributes = napi_default_jsproperty;
        NCheck(napi_define_properties(env, out, 1, &desc));
      }
      break;
  }
  return out;
}

Value FromQuick(JSContext* ctx, JSValueConst v, unsigned depth) {
  Check(depth <= kDataDepth);
  if (JS_IsUndefined(v) || JS_IsNull(v)) return {};
  if (JS_IsBool(v)) return Value::Bool(JS_ToBool(ctx, v));
  if (JS_IsNumber(v)) {
    double n; Check(JS_ToFloat64(ctx, &n, v) == 0);
    Check(std::isfinite(n) && std::abs(n) <= kSafeInteger); return Value::Num(n);
  }
  if (JS_IsString(v)) { Value out; out.kind = Value::String; out.string = QString(ctx, v); return out; }
  Check(JS_IsObject(v) && !JS_IsFunction(ctx, v) && !JS_IsPromise(v));
  if (JS_IsArrayBuffer(v)) {
    size_t n; auto* p = JS_GetArrayBuffer(ctx, &n, v);
    if (!p && JS_HasException(ctx)) throw Fault(ExceptionCode(ctx));
    return CopyBytes(p, n);
  }
  if (JS_GetTypedArrayType(v) >= 0) {
    size_t offset, length, width;
    QValue buffer(ctx, QCheck(ctx, JS_GetTypedArrayBuffer(ctx, v, &offset, &length, &width)));
    size_t n; auto* p = JS_GetArrayBuffer(ctx, &n, buffer.value);
    if (!p && JS_HasException(ctx)) throw Fault(ExceptionCode(ctx));
    Check(offset <= n && length <= n - offset);
    return CopyBytes(p ? p + offset : p, length);
  }
  Value out;
  if (JS_IsArray(v)) {
    out.kind = Value::Array;
    QValue len(ctx, QCheck(ctx, JS_GetPropertyStr(ctx, v, "length")));
    uint32_t n; Check(JS_ToUint32(ctx, &n, len.value) == 0);
    out.array.reserve(n);
    for (uint32_t i = 0; i < n; ++i) {
      QValue item(ctx, QCheck(ctx, JS_GetPropertyUint32(ctx, v, i)));
      out.array.push_back(FromQuick(ctx, item.value, depth + 1));
    }
    return out;
  }
  // Only ordinary objects are data. Reject proxies, dates, DOM wrappers, etc.
  QValue plain(ctx, QCheck(ctx, JS_NewObjectProto(ctx, JS_NULL)));
  Check(JS_GetClassID(v) == JS_GetClassID(plain.value));
  out.kind = Value::Object;
  JSPropertyEnum* keys = nullptr; uint32_t length = 0;
  if (JS_GetOwnPropertyNames(ctx, &keys, &length, v, JS_GPN_STRING_MASK | JS_GPN_ENUM_ONLY) < 0)
    throw Fault(ExceptionCode(ctx));
  try {
    out.object.reserve(length);
    for (uint32_t i = 0; i < length; ++i) {
      QValue key(ctx, QCheck(ctx, JS_AtomToString(ctx, keys[i].atom)));
      QValue item(ctx, QCheck(ctx, JS_GetProperty(ctx, v, keys[i].atom)));
      out.object.emplace_back(QString(ctx, key.value), FromQuick(ctx, item.value, depth + 1));
    }
  } catch (...) { JS_FreePropertyEnum(ctx, keys, length); throw; }
  JS_FreePropertyEnum(ctx, keys, length);
  return out;
}
Value ArgsFromQuick(JSContext* ctx, JSValueConst args) {
  Check(JS_IsArray(args));
  Value out; out.kind = Value::Array;
  QValue length(ctx, QCheck(ctx, JS_GetPropertyStr(ctx, args, "length")));
  uint32_t n; Check(JS_ToUint32(ctx, &n, length.value) == 0); out.array.reserve(n);
  for (uint32_t i = 0; i < n; ++i) {
    QValue item(ctx, QCheck(ctx, JS_GetPropertyUint32(ctx, args, i)));
    out.array.push_back(FromQuick(ctx, item.value));
  }
  return out;
}
JSValue ToQuick(JSContext* ctx, const Value& v) {
  switch (v.kind) {
    case Value::Null: return JS_NULL;
    case Value::Boolean: return JS_NewBool(ctx, v.boolean);
    case Value::Number: return JS_NewFloat64(ctx, v.number);
    case Value::String:
      return QCheck(ctx, JS_NewStringUTF16(ctx, reinterpret_cast<const uint16_t*>(v.string.data()), v.string.size()));
    case Value::Binary: {
      JSValue buffer;
      if (v.bytes->empty()) {
        // QuickJS replaces a null external buffer's finalizer with its own.
        buffer = QCheck(ctx, JS_NewArrayBufferCopy(ctx, nullptr, 0));
      } else {
        auto* owner = new std::shared_ptr<Bytes>(v.bytes);
        buffer = JS_NewArrayBuffer(ctx, v.bytes->data(), v.bytes->size(),
          [](JSRuntime*, void* opaque, void*) { delete static_cast<std::shared_ptr<Bytes>*>(opaque); }, owner, false);
        if (JS_IsException(buffer)) { delete owner; throw Fault(ExceptionCode(ctx)); }
      }
      QValue held(ctx, buffer);
      JSValue args[] = { held.value, JS_UNDEFINED, JS_UNDEFINED };
      return QCheck(ctx, JS_NewTypedArray(ctx, 3, args, JS_TYPED_ARRAY_UINT8));
    }
    case Value::Array: {
      QValue out(ctx, QCheck(ctx, JS_NewArray(ctx)));
      for (size_t i = 0; i < v.array.size(); ++i)
        if (JS_DefinePropertyValueUint32(ctx, out.value, static_cast<uint32_t>(i),
            ToQuick(ctx, v.array[i]), JS_PROP_C_W_E) < 0)
          throw Fault(ExceptionCode(ctx));
      return out.Release();
    }
    case Value::Object: {
      QValue out(ctx, QCheck(ctx, JS_NewObjectProto(ctx, JS_NULL)));
      for (const auto& entry : v.object) {
        auto key = Utf8(entry.first);
        JSAtom atom = JS_NewAtomLen(ctx, key.data(), key.size());
        Check(atom != JS_ATOM_NULL, "RESOURCE_LIMIT");
        int status;
        try { status = JS_DefinePropertyValue(ctx, out.value, atom, ToQuick(ctx, entry.second), JS_PROP_C_W_E); }
        catch (...) { JS_FreeAtom(ctx, atom); throw; }
        JS_FreeAtom(ctx, atom);
        if (status < 0) throw Fault(ExceptionCode(ctx));
      }
      return out.Release();
    }
  }
  throw Fault("INVALID_ARGUMENT");
}
}  // namespace meow
