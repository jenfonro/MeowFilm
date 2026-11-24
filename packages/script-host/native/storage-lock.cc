#include "storage-lock.h"
#include <chrono>
#include <memory>
#include <string>
#include <thread>
#ifdef _WIN32
#include <windows.h>
#else
#include <cerrno>
#include <fcntl.h>
#include <sys/file.h>
#include <sys/stat.h>
#include <unistd.h>
#endif

namespace meow {
namespace {
struct StorageLock {
#ifdef _WIN32
  HANDLE handle = INVALID_HANDLE_VALUE;
  void Close() { if (handle != INVALID_HANDLE_VALUE) CloseHandle(handle); handle = INVALID_HANDLE_VALUE; }
#else
  int handle = -1;
  void Close() { if (handle >= 0) close(handle); handle = -1; }
#endif
  ~StorageLock() { Close(); }
};
napi_value Release(napi_env env, napi_callback_info info) {
  napi_value self; size_t count = 0; StorageLock* lock = nullptr;
  if (napi_get_cb_info(env, info, &count, nullptr, &self, nullptr) != napi_ok ||
      napi_unwrap(env, self, reinterpret_cast<void**>(&lock)) != napi_ok || !lock) {
    napi_throw_error(env, "INVALID_REQUEST", "INVALID_REQUEST"); return nullptr;
  }
  lock->Close();
  napi_value value; napi_get_undefined(env, &value); return value;
}
napi_value Acquire(napi_env env, napi_callback_info info) {
  napi_value args[3]; size_t count = 3, length = 0; bool exclusive; int32_t timeout;
  if (napi_get_cb_info(env, info, &count, args, nullptr, nullptr) != napi_ok || count != 3 ||
      napi_get_value_bool(env, args[1], &exclusive) != napi_ok ||
      napi_get_value_int32(env, args[2], &timeout) != napi_ok || timeout < 0 || timeout > 5000) {
    napi_throw_error(env, "INVALID_REQUEST", "INVALID_REQUEST"); return nullptr;
  }
#ifdef _WIN32
  if (napi_get_value_string_utf16(env, args[0], nullptr, 0, &length) != napi_ok) return nullptr;
  std::u16string path(length + 1, 0);
  napi_get_value_string_utf16(env, args[0], path.data(), path.size(), &length); path.resize(length);
  if (path.size() < 4 || path[1] != u':' || (path[2] != u'\\' && path[2] != u'/') ||
      path.find(u'\0') != std::u16string::npos) {
    napi_throw_error(env, "UNSAFE_PATH", "UNSAFE_PATH"); return nullptr;
  }
#else
  if (napi_get_value_string_utf8(env, args[0], nullptr, 0, &length) != napi_ok) return nullptr;
  std::string path(length + 1, 0);
  napi_get_value_string_utf8(env, args[0], path.data(), path.size(), &length); path.resize(length);
  if (path.empty() || path[0] != '/' || path.find('\0') != std::string::npos) {
    napi_throw_error(env, "UNSAFE_PATH", "UNSAFE_PATH"); return nullptr;
  }
#endif
  auto lock = std::make_unique<StorageLock>();
  const auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(timeout);
  const char* failure = "STORAGE_BUSY";
  for (;;) {
#ifdef _WIN32
    lock->handle = CreateFileW(reinterpret_cast<const wchar_t*>(path.c_str()),
      exclusive ? GENERIC_READ | GENERIC_WRITE : GENERIC_READ, exclusive ? 0 : FILE_SHARE_READ,
      nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (lock->handle != INVALID_HANDLE_VALUE) {
      BY_HANDLE_FILE_INFORMATION information{};
      if (!GetFileInformationByHandle(lock->handle, &information) ||
          (information.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
        failure = "UNSAFE_PATH";
      else break;
      lock->Close();
    } else if (GetLastError() != ERROR_SHARING_VIOLATION && GetLastError() != ERROR_LOCK_VIOLATION)
      failure = "STORAGE_IO";
#else
    lock->handle = open(path.c_str(), O_RDWR | O_CREAT | O_CLOEXEC, 0600);
    if (lock->handle < 0) failure = "STORAGE_IO";
    else {
      struct stat information{};
      if (fstat(lock->handle, &information) != 0 || !S_ISREG(information.st_mode))
        failure = "UNSAFE_PATH";
      else if (flock(lock->handle, (exclusive ? LOCK_EX : LOCK_SH) | LOCK_NB) == 0) break;
      else if (errno != EWOULDBLOCK && errno != EAGAIN) failure = "STORAGE_IO";
      lock->Close();
    }
#endif
    if (std::string(failure) != "STORAGE_BUSY" || std::chrono::steady_clock::now() >= deadline) {
      napi_throw_error(env, failure, failure); return nullptr;
    }
    std::this_thread::sleep_for(std::chrono::milliseconds(20));
  }
  napi_value object, release;
  if (napi_create_object(env, &object) != napi_ok ||
      napi_create_function(env, "release", NAPI_AUTO_LENGTH, Release, nullptr, &release) != napi_ok ||
      napi_set_named_property(env, object, "release", release) != napi_ok ||
      napi_wrap(env, object, lock.get(), [](napi_env, void* value, void*) {
        delete static_cast<StorageLock*>(value);
      }, nullptr, nullptr) != napi_ok) {
    napi_throw_error(env, "STORAGE_ERROR", "STORAGE_ERROR"); return nullptr;
  }
  lock.release(); return object;
}
}
void RegisterStorageLock(napi_env env, napi_value exports) {
  napi_value function;
  napi_create_function(env, "acquireStorageLock", NAPI_AUTO_LENGTH, Acquire, nullptr, &function);
  napi_set_named_property(env, exports, "acquireStorageLock", function);
}
}
