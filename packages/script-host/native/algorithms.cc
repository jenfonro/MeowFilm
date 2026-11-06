#include "algorithms.h"
#include <openssl/bio.h>
#include <openssl/evp.h>
#include <openssl/core_names.h>
#include <openssl/params.h>
#include <openssl/pem.h>
#include <openssl/rand.h>
#include <openssl/rsa.h>
#include <zlib.h>
#include <algorithm>
#include <array>
#include <cctype>
#include <climits>
#include <cstring>
#include <limits>

namespace meow {
namespace {
constexpr size_t kBlock = 64 * 1024;
template<class T, auto Free> using Owned = std::unique_ptr<T, decltype(Free)>;
OSSL_LIB_CTX* CryptoContext() {
  static Owned<OSSL_LIB_CTX, OSSL_LIB_CTX_free> context(OSSL_LIB_CTX_new(), OSSL_LIB_CTX_free);
  Check(context != nullptr, "UNSUPPORTED_CRYPTO");
  return context.get();
}
std::string Encoding(const Value& value, const char* fallback = "utf8") {
  auto s = value.kind == Value::Null ? std::string(fallback) : value.Text();
  std::transform(s.begin(), s.end(), s.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
  if (s == "utf-8") s = "utf8";
  if (s == "utf-16le" || s == "ucs2" || s == "ucs-2") s = "utf16le";
  if (s == "binary") s = "latin1";
  Check(s == "utf8" || s == "utf16le" || s == "latin1" || s == "ascii" ||
        s == "hex" || s == "base64" || s == "base64url", "UNSUPPORTED_OPTION");
  return s;
}
int Digit(char16_t c) {
  if (c >= '0' && c <= '9') return c - '0';
  if (c >= 'a' && c <= 'f') return c - 'a' + 10;
  if (c >= 'A' && c <= 'F') return c - 'A' + 10;
  return -1;
}
int BaseDigit(char16_t c) {
  if (c >= 'A' && c <= 'Z') return c - 'A';
  if (c >= 'a' && c <= 'z') return c - 'a' + 26;
  if (c >= '0' && c <= '9') return c - '0' + 52;
  if (c == '+' || c == '-') return 62;
  if (c == '/' || c == '_') return 63;
  return -1;
}
Bytes Encode(const Value& value, const std::string& encoding, const std::function<void()>& cancel) {
  if (value.kind == Value::Binary) return *value.bytes;
  if (value.kind == Value::Object && value.Get("type").kind == Value::String &&
      value.Get("type").Text() == "Buffer") return Encode(value.Get("data"), encoding, cancel);
  Bytes out;
  if (value.kind == Value::Array) {
    out.reserve(value.array.size());
    for (size_t i = 0; i < value.array.size(); ++i) {
      if (!(i % kBlock)) cancel();
      const auto& item = value.array[i]; Check(item.kind == Value::Number);
      double b = std::fmod(std::trunc(item.number), 256); if (b < 0) b += 256;
      out.push_back(static_cast<uint8_t>(b));
    }
    return out;
  }
  Check(value.kind == Value::String);
  const auto& s = value.string;
  if (encoding == "utf8") {
    // Chunk at scalar boundaries to keep large explicit conversions cancellable.
    for (size_t i = 0; i < s.size();) {
      cancel(); size_t n = std::min(kBlock, s.size() - i);
      if (i + n < s.size() && s[i + n - 1] >= 0xd800 && s[i + n - 1] <= 0xdbff) --n;
      auto part = Utf8(s.substr(i, n)); out.insert(out.end(), part.begin(), part.end()); i += n;
    }
  } else if (encoding == "utf16le") {
    Check(s.size() <= out.max_size() / 2);
    out.reserve(s.size() * 2);
    for (size_t i = 0; i < s.size(); ++i) {
      if (!(i % kBlock)) cancel();
      out.push_back(static_cast<uint8_t>(s[i])); out.push_back(static_cast<uint8_t>(s[i] >> 8));
    }
  } else if (encoding == "latin1" || encoding == "ascii") {
    out.reserve(s.size());
    for (size_t i = 0; i < s.size(); ++i) {
      if (!(i % kBlock)) cancel(); out.push_back(static_cast<uint8_t>(s[i]));
    }
  } else if (encoding == "hex") {
    out.reserve(s.size() / 2);
    for (size_t i = 0; i + 1 < s.size(); i += 2) {
      if (!(i % kBlock)) cancel(); int hi = Digit(s[i]), lo = Digit(s[i + 1]);
      if (hi < 0 || lo < 0) break; out.push_back(static_cast<uint8_t>((hi << 4) | lo));
    }
  } else {
    uint32_t bits = 0; unsigned count = 0;
    for (size_t i = 0; i < s.size(); ++i) {
      if (!(i % kBlock)) cancel();
      if (s[i] == '=') break;
      int digit = BaseDigit(s[i]); if (digit < 0) continue;
      bits = (bits << 6) | static_cast<unsigned>(digit); count += 6;
      if (count >= 8) { count -= 8; out.push_back(static_cast<uint8_t>(bits >> count)); }
    }
  }
  return out;
}
Value Decode(const Bytes& bytes, const std::string& encoding, size_t start, size_t end,
             const std::function<void()>& cancel) {
  Value out; out.kind = Value::String;
  auto& s = out.string;
  start = std::min(start, bytes.size()); end = std::min(end, bytes.size());
  if (end < start) end = start;
  size_t lastCheck = start;
  auto checkpoint = [&](size_t position) {
    if (position - lastCheck >= kBlock) { cancel(); lastCheck = position; }
  };
  cancel();
  if (encoding == "hex") {
    static constexpr char digits[] = "0123456789abcdef";
    for (size_t i = start; i < end; ++i) {
      checkpoint(i); s.push_back(digits[bytes[i] >> 4]); s.push_back(digits[bytes[i] & 15]);
    }
  } else if (encoding == "base64" || encoding == "base64url") {
    const char* digits = encoding == "base64"
      ? "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/"
      : "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
    for (size_t i = start; i < end; i += 3) {
      checkpoint(i);
      uint32_t n = bytes[i] << 16;
      if (i + 1 < end) n |= bytes[i + 1] << 8;
      if (i + 2 < end) n |= bytes[i + 2];
      s.push_back(digits[n >> 18]); s.push_back(digits[(n >> 12) & 63]);
      if (i + 1 < end) s.push_back(digits[(n >> 6) & 63]); else if (encoding == "base64") s.push_back('=');
      if (i + 2 < end) s.push_back(digits[n & 63]); else if (encoding == "base64") s.push_back('=');
    }
  } else if (encoding == "utf16le") {
    for (size_t i = start; i + 1 < end; i += 2) {
      checkpoint(i);
      s.push_back(static_cast<char16_t>(bytes[i] | (bytes[i + 1] << 8)));
    }
  } else if (encoding == "latin1" || encoding == "ascii") {
    for (size_t i = start; i < end; ++i) {
      checkpoint(i); s.push_back(bytes[i] & (encoding == "ascii" ? 127 : 255));
    }
  } else {
    // WHATWG/Node replacement decoding: retain an invalid next byte for the next scalar.
    for (size_t i = start; i < end;) {
      checkpoint(i);
      uint32_t c = bytes[i++], min = 0; unsigned need = 0;
      if (c < 0x80) { s.push_back(static_cast<char16_t>(c)); continue; }
      if (c >= 0xc2 && c <= 0xdf) { c &= 31; need = 1; min = 0x80; }
      else if (c >= 0xe0 && c <= 0xef) { c &= 15; need = 2; min = 0x800; }
      else if (c >= 0xf0 && c <= 0xf4) { c &= 7; need = 3; min = 0x10000; }
      else { s.push_back(0xfffd); continue; }
      unsigned consumed = 0;
      for (; consumed < need && i < end; ++consumed) {
        uint8_t b = bytes[i];
        if ((b & 0xc0) != 0x80 ||
            (consumed == 0 && ((need == 2 && c == 0 && b < 0xa0) ||
             (need == 2 && c == 13 && b >= 0xa0) ||
             (need == 3 && c == 0 && b < 0x90) ||
             (need == 3 && c == 4 && b >= 0x90)))) break;
        ++i; c = (c << 6) | (b & 63);
      }
      if (consumed != need || c < min || c > 0x10ffff || (c >= 0xd800 && c <= 0xdfff)) s.push_back(0xfffd);
      else if (c >= 0x10000) {
        c -= 0x10000; s.push_back(static_cast<char16_t>(0xd800 | (c >> 10)));
        s.push_back(static_cast<char16_t>(0xdc00 | (c & 1023)));
      } else s.push_back(static_cast<char16_t>(c));
    }
  }
  return out;
}
void CryptoCheck(bool success) { Check(success, "UNSUPPORTED_CRYPTO"); }
std::string DigestName(const Value& value) {
  auto name = value.Text();
  CryptoCheck(name == "md5" || name == "sha1" || name == "sha256" || name == "sha512");
  return name;
}
template<class Update> void UpdateBlocks(const Bytes& data, const std::function<void()>& cancel, Update update) {
  for (size_t i = 0; i < data.size();) {
    cancel(); size_t n = std::min(kBlock, data.size() - i);
    CryptoCheck(update(data.data() + i, n) == 1); i += n;
  }
}
}  // namespace

Value NativeAlgorithm(const std::string& op, const Value& args, const std::function<void()>& cancel) {
  Check(args.kind == Value::Array);
  auto bytes = [&](const Value& v, const std::string& enc = "utf8") { return Encode(v, enc, cancel); };
  cancel();
  if (op == "encode") return Value::Bin(bytes(args.At(0), Encoding(args.At(1))));
  if (op == "decode") {
    auto data = bytes(args.At(0));
    auto index = [&](size_t i, size_t fallback) -> size_t {
      const auto& v = args.At(i); if (v.kind == Value::Null) return fallback;
      Check(v.kind == Value::Number);
      return static_cast<size_t>(std::clamp(std::trunc(v.number), 0.0, static_cast<double>(data.size())));
    };
    return Decode(data, Encoding(args.At(1)), index(2, 0), index(3, data.size()), cancel);
  }
  if (op == "random") {
    uint64_t size = args.At(0).Integer(); Check(size <= Bytes().max_size());
    Bytes out(static_cast<size_t>(size));
    for (size_t i = 0; i < out.size();) {
      cancel(); size_t n = std::min(kBlock, out.size() - i);
      CryptoCheck(RAND_bytes_ex(CryptoContext(), out.data() + i, n, 0) == 1); i += n;
    }
    return Value::Bin(std::move(out));
  }
  if (op == "md5" || op == "digest") {
    auto name = op == "md5" ? "md5" : DigestName(args.At(0));
    auto data = bytes(args.At(op == "md5" ? 0 : 1));
    Owned<EVP_MD, EVP_MD_free> md(EVP_MD_fetch(CryptoContext(), name.c_str(), nullptr), EVP_MD_free);
    Owned<EVP_MD_CTX, EVP_MD_CTX_free> ctx(EVP_MD_CTX_new(), EVP_MD_CTX_free);
    CryptoCheck(md && ctx && EVP_DigestInit_ex(ctx.get(), md.get(), nullptr) == 1);
    UpdateBlocks(data, cancel, [&](const void* p, size_t n) { return EVP_DigestUpdate(ctx.get(), p, n); });
    Bytes out(EVP_MAX_MD_SIZE); unsigned n;
    CryptoCheck(EVP_DigestFinal_ex(ctx.get(), out.data(), &n) == 1); out.resize(n);
    return Decode(out, "hex", 0, out.size(), cancel);
  }
  if (op == "hmac") {
    auto name = DigestName(args.At(0));
    auto key = bytes(args.At(1)), data = bytes(args.At(2));
    Owned<EVP_MAC, EVP_MAC_free> mac(EVP_MAC_fetch(CryptoContext(), "HMAC", nullptr), EVP_MAC_free);
    CryptoCheck(mac != nullptr);
    Owned<EVP_MAC_CTX, EVP_MAC_CTX_free> ctx(EVP_MAC_CTX_new(mac.get()), EVP_MAC_CTX_free);
    OSSL_PARAM params[] = {
      OSSL_PARAM_construct_utf8_string(OSSL_MAC_PARAM_DIGEST, name.data(), 0), OSSL_PARAM_construct_end(),
    };
    static const uint8_t empty = 0;
    CryptoCheck(ctx && EVP_MAC_init(ctx.get(), key.empty() ? &empty : key.data(), key.size(), params) == 1);
    UpdateBlocks(data, cancel, [&](const uint8_t* p, size_t n) { return EVP_MAC_update(ctx.get(), p, n); });
    Bytes out(EVP_MAX_MD_SIZE); size_t n;
    CryptoCheck(EVP_MAC_final(ctx.get(), out.data(), &n, out.size()) == 1); out.resize(n);
    return Decode(out, Encoding(args.At(3), "hex"), 0, out.size(), cancel);
  }
  if (op == "aes" || op == "aesDecrypt" || op == "des" || op == "desDecrypt") {
    auto data = bytes(args.At(0)); auto mode = args.At(1).Text();
    auto key = bytes(args.At(2)); auto iv = args.At(3).kind == Value::Null ? Bytes() : bytes(args.At(3));
    bool des = op == "des" || op == "desDecrypt";
    if (des) {
      CryptoCheck((mode == "des-cbc" || mode == "des-ecb") && key.size() == 8);
      key.resize(24); std::memcpy(key.data() + 8, key.data(), 8); std::memcpy(key.data() + 16, key.data(), 8);
      mode = mode == "des-ecb" ? "des-ede3" : "des-ede3-cbc";
    } else {
      CryptoCheck(mode == "aes-128-cbc" || mode == "aes-192-cbc" || mode == "aes-256-cbc" ||
                  mode == "aes-128-ecb" || mode == "aes-192-ecb" || mode == "aes-256-ecb");
    }
    Owned<EVP_CIPHER, EVP_CIPHER_free> cipher(EVP_CIPHER_fetch(CryptoContext(), mode.c_str(), nullptr), EVP_CIPHER_free);
    Owned<EVP_CIPHER_CTX, EVP_CIPHER_CTX_free> ctx(EVP_CIPHER_CTX_new(), EVP_CIPHER_CTX_free);
    CryptoCheck(cipher && ctx);
    CryptoCheck(key.size() == static_cast<size_t>(EVP_CIPHER_get_key_length(cipher.get())));
    size_t ivLength = EVP_CIPHER_get_iv_length(cipher.get());
    CryptoCheck(ivLength == 0 || iv.size() == ivLength);
    CryptoCheck(EVP_CipherInit_ex(ctx.get(), cipher.get(), nullptr, key.data(),
      ivLength ? iv.data() : nullptr, op == "aes" || op == "des") == 1);
    Bytes out; std::array<uint8_t, kBlock + EVP_MAX_BLOCK_LENGTH> block;
    for (size_t i = 0; i < data.size();) {
      cancel(); size_t n = std::min(kBlock, data.size() - i); int written;
      CryptoCheck(EVP_CipherUpdate(ctx.get(), block.data(), &written, data.data() + i, static_cast<int>(n)) == 1);
      out.insert(out.end(), block.data(), block.data() + written); i += n;
    }
    int n; cancel(); CryptoCheck(EVP_CipherFinal_ex(ctx.get(), block.data(), &n) == 1);
    out.insert(out.end(), block.data(), block.data() + n);
    return Value::Bin(std::move(out));
  }
  if (op == "rsa") {
    auto data = bytes(args.At(0)), pem = bytes(args.At(1));
    CryptoCheck(data.size() <= 128 && pem.size() <= INT_MAX);
    Owned<BIO, BIO_free> bio(BIO_new_mem_buf(pem.data(), static_cast<int>(pem.size())), BIO_free);
    CryptoCheck(bio != nullptr);
    Owned<EVP_PKEY, EVP_PKEY_free> key(PEM_read_bio_PUBKEY_ex(bio.get(), nullptr, nullptr, nullptr,
      CryptoContext(), nullptr), EVP_PKEY_free);
    CryptoCheck(key && EVP_PKEY_is_a(key.get(), "RSA") == 1 && EVP_PKEY_get_bits(key.get()) == 1024);
    Owned<EVP_PKEY_CTX, EVP_PKEY_CTX_free> ctx(EVP_PKEY_CTX_new_from_pkey(CryptoContext(), key.get(), nullptr),
      EVP_PKEY_CTX_free);
    CryptoCheck(ctx && EVP_PKEY_encrypt_init(ctx.get()) == 1 &&
      EVP_PKEY_CTX_set_rsa_padding(ctx.get(), RSA_NO_PADDING) == 1);
    std::array<uint8_t, 128> padded{}; std::copy(data.begin(), data.end(), padded.end() - data.size());
    Bytes out(128); size_t n = out.size(); cancel();
    CryptoCheck(EVP_PKEY_encrypt(ctx.get(), out.data(), &n, padded.data(), padded.size()) == 1);
    out.resize(n); cancel(); return Value::Bin(std::move(out));
  }
  if (op == "inflate" || op == "deflate") {
    auto data = bytes(args.At(0)); z_stream stream{};
    bool inflate = op == "inflate";
    int initialized = inflate ? inflateInit(&stream) : deflateInit(&stream, Z_DEFAULT_COMPRESSION);
    Check(initialized == Z_OK, "RESOURCE_LIMIT");
    Bytes out; std::array<uint8_t, kBlock> block; size_t offset = 0;
    try {
      for (;;) {
        cancel();
        if (!stream.avail_in && offset < data.size()) {
          size_t n = std::min(kBlock, data.size() - offset);
          stream.next_in = data.data() + offset; stream.avail_in = static_cast<uInt>(n); offset += n;
        }
        stream.next_out = block.data(); stream.avail_out = static_cast<uInt>(block.size());
        int result = inflate ? ::inflate(&stream, Z_NO_FLUSH)
          : ::deflate(&stream, offset == data.size() ? Z_FINISH : Z_NO_FLUSH);
        out.insert(out.end(), block.data(), block.data() + block.size() - stream.avail_out);
        if (result == Z_STREAM_END) break;
        Check(result == Z_OK && !(inflate && !stream.avail_in && offset == data.size() && stream.avail_out),
          "RESOURCE_LIMIT");
      }
    } catch (...) { if (inflate) inflateEnd(&stream); else deflateEnd(&stream); throw; }
    if (inflate) inflateEnd(&stream); else deflateEnd(&stream);
    return Value::Bin(std::move(out));
  }
  throw Fault("UNSUPPORTED_OPTION");
}
}  // namespace meow
