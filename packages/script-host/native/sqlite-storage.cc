#include "sqlite-storage.h"
#ifdef MEOW_APPLE_STORAGE
#include <sqlite3.h>
#include <algorithm>
#include <cctype>
#include <cstring>
#include <cstdlib>
#include <cerrno>
#include <cmath>
#include <chrono>
#include <CommonCrypto/CommonDigest.h>
#include <condition_variable>
#include <deque>
#include <list>
#include <memory>
#include <mutex>
#include <stdexcept>
#include <string>
#include <thread>
#include <unordered_map>
#include <variant>
#include <vector>
#include <fcntl.h>
#include <sys/stat.h>
#include <sys/file.h>
#include <unistd.h>

namespace meow {
namespace {
constexpr int64_t SafeInteger = 9007199254740991LL;
constexpr size_t MaximumBytes = 16 * 1024 * 1024;
constexpr size_t MaxPreparedStatements = 256;
constexpr size_t MaxCachedSQLLength = 4096;
using Blob = std::vector<unsigned char>;
using Cell = std::variant<std::monostate, int64_t, double, std::string, Blob>;
struct Fault : std::runtime_error { using std::runtime_error::runtime_error; };
void Check(bool value, const char* code = "INVALID_REQUEST") { if (!value) throw Fault(code); }
bool IsPragmaSQL(const std::string& sql) {
  size_t index = 0;
  while (index < sql.size() && std::isspace(static_cast<unsigned char>(sql[index]))) ++index;
  static constexpr const char prefix[] = "pragma";
  if (sql.size() - index < 6) return false;
  for (size_t i = 0; i < 6; ++i) {
    if (std::tolower(static_cast<unsigned char>(sql[index + i])) != prefix[i]) return false;
  }
  if (index + 6 == sql.size()) return true;
  unsigned char next = static_cast<unsigned char>(sql[index + 6]);
  return !(std::isalnum(next) || next == '_');
}
struct OwnedDescriptor {
  int value = -1;
  ~OwnedDescriptor() { if (value >= 0) close(value); }
};
struct AdvisoryLock {
  int descriptor;
  AdvisoryLock(int descriptor, int mode, int timeout = 5000) : descriptor(descriptor) {
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(timeout);
    while (flock(descriptor, mode | LOCK_NB) != 0) {
      Check(errno == EWOULDBLOCK || errno == EAGAIN || errno == EINTR, "STORAGE_IO");
      Check(std::chrono::steady_clock::now() < deadline, "STORAGE_BUSY");
      std::this_thread::sleep_for(std::chrono::milliseconds(20));
    }
  }
  ~AdvisoryLock() { flock(descriptor, LOCK_UN); }
};
void N(napi_status value) { Check(value == napi_ok, "STORAGE_ERROR"); }
const char* SQLiteError(int code) {
  switch (code & 255) {
    case SQLITE_BUSY: case SQLITE_LOCKED: return "STORAGE_BUSY";
    case SQLITE_CONSTRAINT: return "STORAGE_CONFLICT";
    case SQLITE_FULL: return "STORAGE_FULL";
    case SQLITE_READONLY: return "STORAGE_READONLY";
    case SQLITE_CORRUPT: case SQLITE_NOTADB: return "STORAGE_CORRUPT";
    default: return "STORAGE_IO";
  }
}
napi_value Property(napi_env env, napi_value object, const char* key) {
  napi_value value; N(napi_get_named_property(env, object, key, &value)); return value;
}
std::string Text(napi_env env, napi_value value) {
  size_t length = 0; N(napi_get_value_string_utf8(env, value, nullptr, 0, &length));
  std::string result(length + 1, 0);
  N(napi_get_value_string_utf8(env, value, result.data(), result.size(), &length));
  result.resize(length); return result;
}
Cell ReadCell(napi_env env, napi_value value) {
  napi_valuetype type; N(napi_typeof(env, value, &type));
  if (type == napi_null || type == napi_undefined) return {};
  if (type == napi_string) return Text(env, value);
  if (type == napi_boolean) { bool flag; N(napi_get_value_bool(env, value, &flag)); return int64_t(flag); }
  if (type == napi_bigint) {
    int64_t integer; bool lossless; N(napi_get_value_bigint_int64(env, value, &integer, &lossless));
    Check(lossless); return integer;
  }
  if (type == napi_number) {
    double number; N(napi_get_value_double(env, value, &number)); Check(std::isfinite(number));
    if (std::floor(number) != number) return number;
    Check(number >= -SafeInteger && number <= SafeInteger); return int64_t(number);
  }
  bool buffer = false; N(napi_is_buffer(env, value, &buffer));
  if (buffer) {
    void* bytes; size_t length; N(napi_get_buffer_info(env, value, &bytes, &length));
    if (!length) return Blob{};
    auto* begin = static_cast<unsigned char*>(bytes); return Blob(begin, begin + length);
  }
  bool typed = false; N(napi_is_typedarray(env, value, &typed)); Check(typed);
  napi_typedarray_type arrayType; size_t length, offset; void* bytes; napi_value arrayBuffer;
  N(napi_get_typedarray_info(env, value, &arrayType, &length, &bytes, &arrayBuffer, &offset));
  Check(arrayType == napi_uint8_array || arrayType == napi_uint8_clamped_array || arrayType == napi_int8_array);
  if (!length) return Blob{};
  auto* begin = static_cast<unsigned char*>(bytes); return Blob(begin, begin + length);
}
size_t Cost(const Cell& cell) {
  if (auto* text = std::get_if<std::string>(&cell)) return text->size() + 32;
  if (auto* bytes = std::get_if<Blob>(&cell)) return bytes->size() + 32;
  return 16;
}
napi_value ToNode(napi_env env, const Cell& cell) {
  napi_value value;
  if (auto* integer = std::get_if<int64_t>(&cell)) {
    if (*integer >= -SafeInteger && *integer <= SafeInteger) N(napi_create_double(env, double(*integer), &value));
    else N(napi_create_bigint_int64(env, *integer, &value));
  } else if (auto* real = std::get_if<double>(&cell)) N(napi_create_double(env, *real, &value));
  else if (auto* text = std::get_if<std::string>(&cell))
    N(napi_create_string_utf8(env, text->data(), text->size(), &value));
  else if (auto* bytes = std::get_if<Blob>(&cell))
    N(napi_create_buffer_copy(env, bytes->size(), bytes->empty() ? nullptr : bytes->data(), nullptr, &value));
  else N(napi_get_null(env, &value));
  return value;
}
struct Statement {
  std::string sql;
  std::vector<Cell> params;
  int64_t expected = -1;
  bool rows = false;
  size_t maximumRows = size_t(SafeInteger);
};
struct Result {
  bool rows = false;
  int64_t changes = 0, rowId = 0;
  std::vector<std::vector<std::pair<std::string, Cell>>> values;
};
struct Request { int64_t id; std::string kind; std::vector<Statement> statements; size_t bytes; std::string destination; };
struct Reply {
  int64_t id = 0; std::string code; std::vector<Result> results;
  bool batch = false, join = false;
  size_t bytes = 0;
};
Statement ReadStatement(napi_env env, napi_value value, bool rows, size_t& bytes) {
  Statement result;
  result.sql = Text(env, Property(env, value, "sql")); Check(!result.sql.empty());
  bytes += result.sql.size() + 64; result.rows = rows;
  napi_value parameters = Property(env, value, "params"); napi_valuetype type;
  N(napi_typeof(env, parameters, &type));
  if (type != napi_undefined) {
    bool array; N(napi_is_array(env, parameters, &array)); Check(array);
    uint32_t count; N(napi_get_array_length(env, parameters, &count));
    Check(count < 32768, "STORAGE_BUSY");
    for (uint32_t i = 0; i < count; ++i) {
      napi_value parameter; N(napi_get_element(env, parameters, i, &parameter));
      Cell cell = ReadCell(env, parameter); bytes += Cost(cell);
      result.params.push_back(std::move(cell));
    }
  }
  Cell expected = ReadCell(env, Property(env, value, "expectChanges"));
  if (!std::holds_alternative<std::monostate>(expected)) {
    Check(std::holds_alternative<int64_t>(expected)); result.expected = std::get<int64_t>(expected);
    Check(result.expected >= 0);
  }
  Cell maximum = ReadCell(env, Property(env, value, "maxRows"));
  if (!std::holds_alternative<std::monostate>(maximum)) {
    Check(std::holds_alternative<int64_t>(maximum));
    int64_t count = std::get<int64_t>(maximum); Check(count > 0);
    result.maximumRows = size_t(count);
  }
  Cell flag = ReadCell(env, Property(env, value, "rows"));
  if (auto* number = std::get_if<int64_t>(&flag)) result.rows = *number != 0;
  return result;
}

/// Owns one SQLite connection handle and its independent bounded LRU prepared-statement cache.
struct ConnectionSlot {
  sqlite3* db = nullptr;
  using StatementOrder = std::list<std::pair<std::string, sqlite3_stmt*>>;
  StatementOrder statementOrder;
  std::unordered_map<std::string, StatementOrder::iterator> statementCache;

  void Exec(const std::string& sql) {
    int code = sqlite3_exec(db, sql.c_str(), nullptr, nullptr, nullptr);
    if (code != SQLITE_OK) throw Fault(SQLiteError(code));
  }
  void ClearStatementCache() {
    for (auto& entry : statementOrder) {
      if (entry.second) sqlite3_finalize(entry.second);
    }
    statementCache.clear();
    statementOrder.clear();
  }
  int Close() {
    if (!db) return SQLITE_OK;
    ClearStatementCache();
    int code = sqlite3_close(db);
    db = nullptr;
    return code;
  }
  sqlite3_stmt* AcquireStatement(const std::string& sql, bool& cached) {
    Check(sql.find('\0') == std::string::npos);
    const bool cacheable = sql.size() <= MaxCachedSQLLength && !IsPragmaSQL(sql);
    if (cacheable) {
      auto found = statementCache.find(sql);
      if (found != statementCache.end()) {
        statementOrder.splice(statementOrder.begin(), statementOrder, found->second);
        cached = true;
        return found->second->second;
      }
    }
    sqlite3_stmt* raw = nullptr;
    const char* tail = nullptr;
    int code = sqlite3_prepare_v2(db, sql.c_str(), int(sql.size()), &raw, &tail);
    if (code != SQLITE_OK) {
      if (raw) sqlite3_finalize(raw);
      throw Fault(SQLiteError(code));
    }
    if (!raw) throw Fault("INVALID_REQUEST");
    while (tail && std::isspace(static_cast<unsigned char>(*tail))) ++tail;
    if (tail && *tail) {
      sqlite3_finalize(raw);
      throw Fault("INVALID_REQUEST");
    }
    if (cacheable) {
      if (statementCache.size() >= MaxPreparedStatements) {
        auto& oldest = statementOrder.back();
        if (oldest.second) sqlite3_finalize(oldest.second);
        statementCache.erase(oldest.first);
        statementOrder.pop_back();
      }
      statementOrder.emplace_front(sql, raw);
      statementCache.emplace(statementOrder.front().first, statementOrder.begin());
      cached = true;
    } else {
      cached = false;
    }
    return raw;
  }
  template <class RetainFn>
  Result Execute(const Statement& input, size_t& bytes, RetainFn&& retainResult) {
    bool cached = false;
    sqlite3_stmt* raw = AcquireStatement(input.sql, cached);
    struct StatementGuard {
      sqlite3_stmt* stmt;
      bool cached;
      ~StatementGuard() {
        if (!stmt) return;
        if (cached) {
          sqlite3_reset(stmt);
          sqlite3_clear_bindings(stmt);
        } else {
          sqlite3_finalize(stmt);
        }
      }
    } guard{raw, cached};
    Check(size_t(sqlite3_bind_parameter_count(raw)) == input.params.size());
    for (size_t i = 0; i < input.params.size(); ++i) {
      const auto& cell = input.params[i]; int position = int(i + 1), bound;
      if (auto* integer = std::get_if<int64_t>(&cell)) bound = sqlite3_bind_int64(raw, position, *integer);
      else if (auto* real = std::get_if<double>(&cell)) bound = sqlite3_bind_double(raw, position, *real);
      else if (auto* text = std::get_if<std::string>(&cell))
        bound = sqlite3_bind_text64(raw, position, text->data(), sqlite3_uint64(text->size()), SQLITE_TRANSIENT, SQLITE_UTF8);
      else if (auto* blob = std::get_if<Blob>(&cell))
        bound = sqlite3_bind_blob64(raw, position, blob->empty() ? "" : static_cast<const void*>(blob->data()),
          sqlite3_uint64(blob->size()), SQLITE_TRANSIENT);
      else bound = sqlite3_bind_null(raw, position);
      if (bound != SQLITE_OK) throw Fault(SQLiteError(bound));
    }
    Result result; result.rows = input.rows;
    int code = SQLITE_OK;
    while ((code = sqlite3_step(raw)) == SQLITE_ROW) {
      if (!input.rows) continue;
      Check(result.values.size() < input.maximumRows, "STORAGE_RESULT_LIMIT");
      std::vector<std::pair<std::string, Cell>> row;
      int colCount = sqlite3_column_count(raw);
      row.reserve(size_t(colCount));
      for (int i = 0; i < colCount; ++i) {
        Cell value; int length = sqlite3_column_bytes(raw, i);
        const char* columnName = sqlite3_column_name(raw, i);
        int colType = sqlite3_column_type(raw, i);
        size_t size = std::strlen(columnName) + 64 +
          (colType == SQLITE_TEXT || colType == SQLITE_BLOB ? size_t(length) : 16);
        retainResult(size, bytes);
        switch (colType) {
          case SQLITE_INTEGER: value = sqlite3_column_int64(raw, i); break;
          case SQLITE_FLOAT: value = sqlite3_column_double(raw, i); break;
          case SQLITE_TEXT:
            value = std::string(reinterpret_cast<const char*>(sqlite3_column_text(raw, i)), size_t(length)); break;
          case SQLITE_BLOB: {
            const auto* data = static_cast<const unsigned char*>(sqlite3_column_blob(raw, i));
            value = length ? Blob(data, data + length) : Blob{}; break;
          }
        }
        row.emplace_back(columnName, std::move(value));
      }
      result.values.push_back(std::move(row));
    }
    if (code != SQLITE_DONE) throw Fault(SQLiteError(code));
    result.changes = sqlite3_changes(db); result.rowId = sqlite3_last_insert_rowid(db);
    if (input.expected >= 0) Check(result.changes == input.expected, "STORAGE_CONFLICT");
    return result;
  }
};

class Storage : public std::enable_shared_from_this<Storage> {
 public:
  napi_env env;
  ConnectionSlot writeSlot;
  ConnectionSlot readSlot;
  std::string path, schema, hash;
  int application = 0, version = 0;
  bool initialize = false, openComplete = false, stopping = false, environmentClosing = false, joining = false;
  std::mutex mutex;
  std::condition_variable writeChanged;
  std::condition_variable readChanged;
  std::deque<Request> writePending;
  std::deque<Request> readPending;
  size_t pendingBytes = 0, pendingCount = 0;
  std::string openFailure;
  std::unique_ptr<Reply> closeReply;
  dev_t device = 0;
  ino_t inode = 0;
  OwnedDescriptor maintenance;
  int commits = 0;
  std::thread writeThread;
  std::thread readThread;
  napi_threadsafe_function messages = nullptr;
  napi_async_work joinWork = nullptr;
  napi_async_cleanup_hook_handle cleanup = nullptr;
  std::shared_ptr<Storage> joinOwner;

  static off_t CheckPath(const std::string& file, bool required) {
    Check(!file.empty() && file[0] == '/' && file.find('\0') == std::string::npos, "UNSAFE_PATH");
    const size_t separator = file.find_last_of('/');
    const std::string parent = file.substr(0, separator), basename = file.substr(separator + 1);
    std::unique_ptr<char, decltype(&free)> canonical(realpath(parent.c_str(), nullptr), free);
    Check(canonical && parent == canonical.get(), "UNSAFE_PATH");
    int directory = open(parent.c_str(), O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC);
    Check(directory >= 0, "UNSAFE_PATH");
    struct stat state{};
    int result = fstatat(directory, basename.c_str(), &state, AT_SYMLINK_NOFOLLOW);
    int error = errno;
    close(directory);
    Check(!basename.empty() && basename != "." && basename != "..", "UNSAFE_PATH");
    if (result < 0 && error == ENOENT && !required) return -1;
    Check(result == 0 && S_ISREG(state.st_mode) && state.st_nlink == 1, "UNSAFE_PATH");
    return state.st_size;
  }
  void CheckFiles() {
    CheckPath(path, true);
    struct stat state{}; Check(lstat(path.c_str(), &state) == 0, "STORAGE_IO");
    Check(state.st_dev == device && state.st_ino == inode, "UNSAFE_PATH");
    for (const char* suffix : {"-wal", "-shm", "-journal"}) CheckPath(path + suffix, false);
    const std::string lockPath = path.substr(0, path.find_last_of('/')) + "/maintenance.lock";
    CheckPath(lockPath, true);
    struct stat descriptorState{}, pathState{};
    Check(fstat(maintenance.value, &descriptorState) == 0 && lstat(lockPath.c_str(), &pathState) == 0 &&
      descriptorState.st_dev == pathState.st_dev && descriptorState.st_ino == pathState.st_ino, "UNSAFE_PATH");
  }
  void RetainResult(size_t size, size_t& owned) {
    std::lock_guard<std::mutex> lock(mutex);
    Check(pendingCount <= 1 || (pendingBytes <= MaximumBytes && size <= MaximumBytes - pendingBytes), "STORAGE_RESULT_LIMIT");
    pendingBytes += size; owned += size;
  }
  void ReleaseReply(const Reply& reply) {
    if (reply.id <= 0) return;
    std::lock_guard<std::mutex> lock(mutex);
    --pendingCount; pendingBytes -= reply.bytes;
  }
  std::string CurrentFailure() {
    std::lock_guard<std::mutex> lock(mutex);
    return openFailure;
  }
  void LatchFatalFailure(const std::string& code) {
    if (code != "UNSAFE_PATH" && code != "STORAGE_CORRUPT") return;
    std::lock_guard<std::mutex> lock(mutex);
    if (openFailure.empty()) openFailure = code;
  }
  void Send(std::unique_ptr<Reply> reply, bool background = false) {
    Reply* raw = reply.release();
    if (napi_call_threadsafe_function(messages, raw, background ? napi_tsfn_blocking : napi_tsfn_nonblocking) != napi_ok) {
      ReleaseReply(*raw); delete raw;
    }
  }
  void Stop() {
    { std::lock_guard<std::mutex> lock(mutex); stopping = true; }
    writeChanged.notify_all();
    readChanged.notify_all();
  }
  void Join() {
    if (joining) return;
    joining = true; joinOwner = shared_from_this(); N(napi_queue_async_work(env, joinWork));
  }
  void Open() {
    const std::string parent = path.substr(0, path.find_last_of('/'));
    const std::string initializePath = parent + "/initialize.lock";
    CheckPath(initializePath, false);
    OwnedDescriptor initializeFile;
    initializeFile.value = open(initializePath.c_str(), O_RDWR | O_CREAT | O_NOFOLLOW | O_CLOEXEC, 0600);
    Check(initializeFile.value >= 0, "STORAGE_IO");
    struct stat lockState{};
    Check(fstat(initializeFile.value, &lockState) == 0 && S_ISREG(lockState.st_mode) && lockState.st_nlink == 1, "UNSAFE_PATH");
    AdvisoryLock initializeLock(initializeFile.value, LOCK_EX, 5000);
    const std::string maintenancePath = parent + "/maintenance.lock";
    CheckPath(maintenancePath, false);
    maintenance.value = open(maintenancePath.c_str(), O_RDWR | O_CREAT | O_NOFOLLOW | O_CLOEXEC, 0600);
    Check(maintenance.value >= 0, "STORAGE_IO");
    Check(fstat(maintenance.value, &lockState) == 0 && S_ISREG(lockState.st_mode) && lockState.st_nlink == 1, "UNSAFE_PATH");
    AdvisoryLock validationLock(maintenance.value, LOCK_SH, 5000);
    const off_t existing = CheckPath(path, false);
    for (const char* suffix : {"-wal", "-shm", "-journal"}) {
      const off_t sidecar = CheckPath(path + suffix, false);
      Check(existing >= 0 || sidecar < 0, "STORAGE_CORRUPT");
    }
    Check(existing != 0, "STORAGE_CORRUPT");
    bool createdFresh = false;
    bool committedSchema = false;
    try {
      if (existing < 0) {
        Check(initialize, "STORAGE_SCHEMA");
        const int fd = open(path.c_str(), O_RDWR | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, 0600);
        Check(fd >= 0, errno == ENOSPC ? "STORAGE_FULL" : "STORAGE_IO");
        createdFresh = true;
        Check(close(fd) == 0, "STORAGE_IO");
      }
      int code = sqlite3_open_v2(path.c_str(), &writeSlot.db,
        SQLITE_OPEN_READWRITE | SQLITE_OPEN_FULLMUTEX | SQLITE_OPEN_NOFOLLOW, nullptr);
      if (code != SQLITE_OK) throw Fault(SQLiteError(code));
      Check(std::string(sqlite3_libversion()) == "3.53.4", "STORAGE_ENGINE_UNSUPPORTED");
      Check(chmod(path.c_str(), 0600) == 0, "STORAGE_IO");
      struct stat state{}; Check(lstat(path.c_str(), &state) == 0, "STORAGE_IO");
      device = state.st_dev; inode = state.st_ino;
      sqlite3_extended_result_codes(writeSlot.db, 1); sqlite3_busy_timeout(writeSlot.db, 5000);
      Check(sqlite3_db_config(writeSlot.db, SQLITE_DBCONFIG_ENABLE_LOAD_EXTENSION, 0, nullptr) == SQLITE_OK, "STORAGE_CONFIGURATION");
      Check(sqlite3_db_config(writeSlot.db, SQLITE_DBCONFIG_NO_CKPT_ON_CLOSE, 1, nullptr) == SQLITE_OK, "STORAGE_CONFIGURATION");
      writeSlot.Exec("PRAGMA trusted_schema=OFF; PRAGMA foreign_keys=ON;");

      auto retain = [this](size_t s, size_t& o) { RetainResult(s, o); };
      auto readFrom = [&](ConnectionSlot& slot, const char* sql) {
        size_t retained = 0;
        try {
          auto result = slot.Execute({sql, {}, -1, true}, retained, retain);
          std::lock_guard<std::mutex> lock(mutex); pendingBytes -= retained;
          return result;
        } catch (...) {
          std::lock_guard<std::mutex> lock(mutex); pendingBytes -= retained;
          throw;
        }
      };
      auto intFrom = [&](ConnectionSlot& slot, const char* sql) {
        return std::get<int64_t>(readFrom(slot, sql).values[0][0].second);
      };
      auto verifyPragmas = [&](ConnectionSlot& slot, bool expectQueryOnly) {
        Check(std::get<std::string>(readFrom(slot, "PRAGMA journal_mode").values[0][0].second) == "wal" &&
          intFrom(slot, "PRAGMA synchronous") == 2 && intFrom(slot, "PRAGMA foreign_keys") == 1 &&
          intFrom(slot, "PRAGMA trusted_schema") == 0 && intFrom(slot, "PRAGMA wal_autocheckpoint") == 0 &&
          intFrom(slot, "PRAGMA auto_vacuum") == 2 && intFrom(slot, "PRAGMA busy_timeout") == 5000 &&
          intFrom(slot, "PRAGMA temp_store") == 2 && intFrom(slot, "PRAGMA cache_size") == -16384 &&
          (!expectQueryOnly || intFrom(slot, "PRAGMA query_only") == 1), "STORAGE_CONFIGURATION");
      };

      auto app = intFrom(writeSlot, "PRAGMA application_id"), current = intFrom(writeSlot, "PRAGMA user_version");
      bool empty = intFrom(writeSlot, "SELECT count(*) FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%'") == 0;
      if (empty) {
        Check(existing < 0 && !app && !current, "STORAGE_SCHEMA"); writeSlot.Exec("PRAGMA auto_vacuum=INCREMENTAL");
      } else Check(app == application && current == version, "STORAGE_SCHEMA");
      writeSlot.Exec("PRAGMA busy_timeout=5000; PRAGMA trusted_schema=OFF; PRAGMA foreign_keys=ON; "
                     "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA wal_autocheckpoint=0; "
                     "PRAGMA temp_store=MEMORY; PRAGMA cache_size=-16384;");
      verifyPragmas(writeSlot, false);
      if (empty) {
        size_t bytes = 0;
        writeSlot.Exec("BEGIN IMMEDIATE");
        try {
          if (!intFrom(writeSlot, "SELECT count(*) FROM sqlite_schema WHERE name='storage_identity'")) {
            writeSlot.Exec(schema);
            writeSlot.Execute({"INSERT INTO storage_identity(singleton,schema_hash) VALUES(1,?)", {hash}}, bytes, retain);
            writeSlot.Exec("PRAGMA application_id=" + std::to_string(application) + "; PRAGMA user_version=" + std::to_string(version));
          }
          writeSlot.Exec("COMMIT");
          committedSchema = true;
          std::lock_guard<std::mutex> lock(mutex); pendingBytes -= bytes;
        } catch (...) {
          { std::lock_guard<std::mutex> lock(mutex); pendingBytes -= bytes; }
          if (writeSlot.db && !sqlite3_get_autocommit(writeSlot.db))
            sqlite3_exec(writeSlot.db, "ROLLBACK", nullptr, nullptr, nullptr);
          throw;
        }
      }
      auto rows = readFrom(writeSlot, "SELECT schema_hash FROM storage_identity WHERE singleton=1");
      Check(rows.values.size() == 1 && std::get<std::string>(rows.values[0][0].second) == hash, "STORAGE_SCHEMA");
      sqlite3* rawExpected = nullptr;
      int expectedCode = sqlite3_open_v2(":memory:", &rawExpected, SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE, nullptr);
      std::unique_ptr<sqlite3, decltype(&sqlite3_close_v2)> expected(rawExpected, sqlite3_close_v2);
      Check(expectedCode == SQLITE_OK && rawExpected, "STORAGE_IO");
      Check(sqlite3_exec(rawExpected, schema.c_str(), nullptr, nullptr, nullptr) == SQLITE_OK, "STORAGE_SCHEMA");
      auto structure = [](sqlite3* connection) {
        sqlite3_stmt* raw = nullptr;
        int code = sqlite3_prepare_v2(connection,
          "SELECT type,name,tbl_name,sql FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%' ORDER BY type,name", -1, &raw, nullptr);
        std::unique_ptr<sqlite3_stmt, decltype(&sqlite3_finalize)> statement(raw, sqlite3_finalize);
        Check(code == SQLITE_OK && raw, "STORAGE_SCHEMA");
        std::vector<std::string> values;
        while ((code = sqlite3_step(raw)) == SQLITE_ROW)
          for (int column = 0; column < 4; ++column) {
            const char* text = reinterpret_cast<const char*>(sqlite3_column_text(raw, column));
            values.emplace_back(text ? text : "");
          }
        Check(code == SQLITE_DONE, "STORAGE_SCHEMA");
        return values;
      };
      Check(structure(writeSlot.db) == structure(rawExpected), "STORAGE_SCHEMA");

      // Dedicated WAL read-only connection on its own thread (`query_only=ON`).
      code = sqlite3_open_v2(path.c_str(), &readSlot.db,
        SQLITE_OPEN_READONLY | SQLITE_OPEN_FULLMUTEX | SQLITE_OPEN_NOFOLLOW, nullptr);
      if (code != SQLITE_OK) throw Fault(SQLiteError(code));
      sqlite3_extended_result_codes(readSlot.db, 1); sqlite3_busy_timeout(readSlot.db, 5000);
      Check(sqlite3_db_config(readSlot.db, SQLITE_DBCONFIG_ENABLE_LOAD_EXTENSION, 0, nullptr) == SQLITE_OK, "STORAGE_CONFIGURATION");
      Check(sqlite3_db_config(readSlot.db, SQLITE_DBCONFIG_NO_CKPT_ON_CLOSE, 1, nullptr) == SQLITE_OK, "STORAGE_CONFIGURATION");
      readSlot.Exec("PRAGMA busy_timeout=5000; PRAGMA trusted_schema=OFF; PRAGMA foreign_keys=ON; "
                    "PRAGMA synchronous=FULL; PRAGMA wal_autocheckpoint=0; "
                    "PRAGMA temp_store=MEMORY; PRAGMA cache_size=-16384; PRAGMA query_only=ON;");
      verifyPragmas(readSlot, true);

      writeSlot.ClearStatementCache();
      readSlot.ClearStatementCache();
    } catch (...) {
      readSlot.Close();
      writeSlot.Close();
      if (createdFresh && !committedSchema) {
        unlink(path.c_str());
        unlink((path + "-wal").c_str());
        unlink((path + "-shm").c_str());
        unlink((path + "-journal").c_str());
      }
      throw;
    }
  }
  void Backup(const std::string& destination) {
    CheckPath(destination, false);
    int file = open(destination.c_str(), O_RDWR | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, 0600);
    Check(file >= 0, errno == EEXIST ? "STORAGE_CONFLICT" : "STORAGE_IO");
    close(file);
    sqlite3* target = nullptr;
    sqlite3_backup* backup = nullptr;
    try {
      int code = sqlite3_open_v2(destination.c_str(), &target, SQLITE_OPEN_READWRITE | SQLITE_OPEN_NOFOLLOW, nullptr);
      if (code != SQLITE_OK) throw Fault(SQLiteError(code));
      backup = sqlite3_backup_init(target, "main", writeSlot.db, "main");
      Check(backup, "STORAGE_IO");
      code = sqlite3_backup_step(backup, -1);
      int finished = sqlite3_backup_finish(backup); backup = nullptr;
      if (code != SQLITE_DONE || finished != SQLITE_OK) throw Fault(SQLiteError(code != SQLITE_DONE ? code : finished));
      Check(sqlite3_close(target) == SQLITE_OK, "STORAGE_IO"); target = nullptr;
      file = open(destination.c_str(), O_RDONLY | O_NOFOLLOW | O_CLOEXEC);
      Check(file >= 0, "STORAGE_IO"); int synced = fsync(file); close(file); Check(synced == 0, "STORAGE_IO");
      std::string parent = destination.substr(0, destination.find_last_of('/'));
      file = open(parent.c_str(), O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC);
      Check(file >= 0, "STORAGE_IO"); synced = fsync(file); close(file); Check(synced == 0, "STORAGE_IO");
    } catch (...) {
      if (backup) sqlite3_backup_finish(backup);
      if (target) sqlite3_close_v2(target);
      unlink(destination.c_str());
      throw;
    }
  }
  void RunReader() {
    auto retain = [this](size_t s, size_t& o) { RetainResult(s, o); };
    for (;;) {
      Request request;
      {
        std::unique_lock<std::mutex> lock(mutex);
        readChanged.wait(lock, [&] { return (openComplete && !readPending.empty()) || stopping; });
        if (readPending.empty()) {
          if (stopping) break;
          continue;
        }
        request = std::move(readPending.front()); readPending.pop_front();
      }
      auto reply = std::make_unique<Reply>(); reply->id = request.id; reply->bytes = request.bytes;
      size_t bytes = 0;
      const bool snapshot = request.kind == "snapshot";
      try {
        std::string failure = CurrentFailure();
        if (!failure.empty()) throw Fault(failure);
        if (!readSlot.db) throw Fault("STORAGE_CLOSED");
        if (snapshot) readSlot.Exec("BEGIN DEFERRED");
        for (const auto& item : request.statements) reply->results.push_back(readSlot.Execute(item, bytes, retain));
        if (snapshot) readSlot.Exec("COMMIT");
        reply->batch = snapshot;
      } catch (const std::exception& error) {
        if (readSlot.db && !sqlite3_get_autocommit(readSlot.db))
          sqlite3_exec(readSlot.db, "ROLLBACK", nullptr, nullptr, nullptr);
        reply->code = error.what(); reply->results.clear();
      }
      reply->bytes += bytes;
      Send(std::move(reply), true);
    }
    readSlot.Close();
  }
  void RunWriter() {
    auto retain = [this](size_t s, size_t& o) { RetainResult(s, o); };
    auto initial = std::make_unique<Reply>();
    try {
      Open();
      {
        std::lock_guard<std::mutex> lock(mutex);
        openComplete = true;
      }
      readChanged.notify_all();
    } catch (const std::exception& error) {
      {
        std::lock_guard<std::mutex> lock(mutex);
        openFailure = error.what();
        openComplete = true;
        stopping = true;
      }
      initial->code = error.what();
      readChanged.notify_all();
    }
    Send(std::move(initial), true);
    for (;;) {
      Request request;
      {
        std::unique_lock<std::mutex> lock(mutex);
        writeChanged.wait(lock, [&] { return stopping || !writePending.empty(); });
        if (writePending.empty()) break;
        request = std::move(writePending.front()); writePending.pop_front();
      }
      auto reply = std::make_unique<Reply>(); reply->id = request.id; reply->bytes = request.bytes;
      size_t bytes = 0;
      bool transaction = request.kind == "transaction" || request.kind == "execute";
      try {
        if (request.kind == "close") {
          closeReply = std::move(reply);
          Stop();
          break;
        }
        std::string failure = CurrentFailure();
        if (!failure.empty()) throw Fault(failure);
        std::unique_ptr<AdvisoryLock> maintenanceLock;
        if (request.kind == "checkpoint" || request.kind == "backup") {
          maintenanceLock = std::make_unique<AdvisoryLock>(maintenance.value, LOCK_EX, 5000);
          CheckFiles();
        }
        if (request.kind == "backup") { Backup(request.destination); }
        const auto before = sqlite3_total_changes64(writeSlot.db);
        if (transaction) writeSlot.Exec("BEGIN IMMEDIATE");
        for (const auto& item : request.statements) reply->results.push_back(writeSlot.Execute(item, bytes, retain));
        if (transaction) writeSlot.Exec("COMMIT");
        if (transaction && sqlite3_total_changes64(writeSlot.db) != before) ++commits;
        reply->batch = request.kind == "transaction";
      } catch (const std::exception& error) {
        if (writeSlot.db && !sqlite3_get_autocommit(writeSlot.db))
          sqlite3_exec(writeSlot.db, "ROLLBACK", nullptr, nullptr, nullptr);
        reply->code = error.what(); reply->results.clear();
      }
      reply->bytes += bytes;
      Send(std::move(reply), true);
      if (commits >= 64) {
        commits = 0;
        try {
          AdvisoryLock maintenanceLock(maintenance.value, LOCK_EX, 0);
          CheckFiles();
          int ckptBusy = 0;
          Check(sqlite3_exec(writeSlot.db, "PRAGMA wal_checkpoint(PASSIVE)", [](void* out, int, char** values, char**) {
            if (values && values[0]) *static_cast<int*>(out) = std::atoi(values[0]);
            return 0;
          }, &ckptBusy, nullptr) == SQLITE_OK, "STORAGE_IO");
          if (ckptBusy == 0) {
            int freePages = 0;
            Check(sqlite3_exec(writeSlot.db, "PRAGMA freelist_count", [](void* result, int, char** values, char**) {
              *static_cast<int*>(result) = std::atoi(values[0]); return 0;
            }, &freePages, nullptr) == SQLITE_OK, "STORAGE_IO");
            if (freePages >= 256) writeSlot.Exec("PRAGMA incremental_vacuum(128)");
          }
        } catch (const std::exception& error) {
          LatchFatalFailure(error.what());
        }
      }
    }
    Stop();
    if (readThread.joinable()) readThread.join();
    int code = writeSlot.Close();
    if (code != SQLITE_OK && closeReply) closeReply->code = SQLiteError(code);
    auto join = std::make_unique<Reply>(); join->join = true; Send(std::move(join), true);
    napi_release_threadsafe_function(messages, napi_tsfn_release);
  }
};
napi_value ResultValue(napi_env env, const Result& result) {
  napi_value output;
  if (!result.rows) {
    N(napi_create_object(env, &output));
    N(napi_set_named_property(env, output, "changes", ToNode(env, result.changes)));
    N(napi_set_named_property(env, output, "lastInsertRowid", ToNode(env, result.rowId))); return output;
  }
  N(napi_create_array_with_length(env, result.values.size(), &output));
  for (size_t i = 0; i < result.values.size(); ++i) {
    napi_value row; N(napi_create_object(env, &row));
    for (const auto& cell : result.values[i]) N(napi_set_named_property(env, row, cell.first.c_str(), ToNode(env, cell.second)));
    N(napi_set_element(env, output, uint32_t(i), row));
  }
  return output;
}
void Deliver(napi_env env, napi_value callback, void* context, void* data) {
  std::unique_ptr<Reply> reply(static_cast<Reply*>(data));
  auto& storage = *static_cast<std::shared_ptr<Storage>*>(context);
  storage->ReleaseReply(*reply);
  if (!env || !callback) return;
  try {
  if (reply->join) { storage->Join(); return; }
  napi_value message, value, undefined, ignored;
  N(napi_create_object(env, &message)); N(napi_set_named_property(env, message, "id", ToNode(env, reply->id)));
  if (!reply->code.empty()) N(napi_set_named_property(env, message, "code", ToNode(env, reply->code)));
  else {
    if (reply->batch) {
      N(napi_create_array_with_length(env, reply->results.size(), &value));
      for (size_t i = 0; i < reply->results.size(); ++i)
        N(napi_set_element(env, value, uint32_t(i), ResultValue(env, reply->results[i])));
    } else if (!reply->results.empty()) value = ResultValue(env, reply->results[0]);
    else N(napi_get_boolean(env, true, &value));
    N(napi_set_named_property(env, message, "value", value));
  }
  N(napi_get_undefined(env, &undefined)); N(napi_call_function(env, undefined, callback, 1, &message, &ignored));
  } catch (...) {
    storage->Stop();
    bool pending = false; napi_is_exception_pending(env, &pending);
    if (pending) {
      napi_value error; napi_get_and_clear_last_exception(env, &error);
      napi_fatal_exception(env, error);
    }
  }
}
template<class F> napi_value Protect(napi_env env, F body) {
  try { return body(); }
  catch (const std::exception& error) {
    bool pending; napi_is_exception_pending(env, &pending);
    if (!pending) napi_throw_error(env, error.what(), error.what());
    return nullptr;
  }
}
napi_value RequestValue(napi_env env, napi_callback_info info) {
  return Protect(env, [&] {
    napi_value args[2], self; size_t count = 2;
    N(napi_get_cb_info(env, info, &count, args, &self, nullptr)); Check(count == 2);
    std::shared_ptr<Storage>* holder; N(napi_unwrap(env, self, reinterpret_cast<void**>(&holder)));
    auto& storage = **holder;
    Cell id = ReadCell(env, args[0]); Check(std::holds_alternative<int64_t>(id));
    Request request{std::get<int64_t>(id), Text(env, Property(env, args[1], "kind")), {}, 64};
    Check(request.id > 0 && request.id <= SafeInteger);
    if (request.kind == "query" || request.kind == "execute")
      request.statements.push_back(ReadStatement(env, args[1], request.kind == "query", request.bytes));
    else if (request.kind == "transaction" || request.kind == "snapshot") {
      napi_value statements = Property(env, args[1], "statements"); bool array; uint32_t length;
      N(napi_is_array(env, statements, &array)); Check(array);
      N(napi_get_array_length(env, statements, &length)); Check(length > 0);
      for (uint32_t i = 0; i < length; ++i) {
        napi_value item; N(napi_get_element(env, statements, i, &item));
        request.statements.push_back(ReadStatement(env, item, request.kind == "snapshot", request.bytes));
      }
    } else if (request.kind == "checkpoint") request.statements.push_back({"PRAGMA wal_checkpoint(PASSIVE)", {}, -1, true});
    else if (request.kind == "backup") {
      request.destination = Text(env, Property(env, args[1], "path"));
      request.bytes += request.destination.size();
    }
    else Check(request.kind == "close");
    const bool isRead = request.kind == "query" || request.kind == "snapshot";
    const bool isClose = request.kind == "close";
    {
      std::lock_guard<std::mutex> lock(storage.mutex);
      Check(!storage.stopping, "STORAGE_CLOSED");
      Check(storage.pendingCount < 64 && (storage.pendingCount == 0 ||
        (storage.pendingBytes <= MaximumBytes && request.bytes <= MaximumBytes - storage.pendingBytes)), "STORAGE_BUSY");
      if (isClose) storage.stopping = true;
      ++storage.pendingCount; storage.pendingBytes += request.bytes;
      if (isRead) storage.readPending.push_back(std::move(request));
      else storage.writePending.push_back(std::move(request));
    }
    if (isRead) storage.readChanged.notify_one();
    else {
      storage.writeChanged.notify_one();
      if (isClose) storage.readChanged.notify_all();
    }
    napi_value undefined; N(napi_get_undefined(env, &undefined)); return undefined;
  });
}
napi_value OpenValue(napi_env env, napi_callback_info info) {
  return Protect(env, [&] {
    napi_value args[2]; size_t count = 2; N(napi_get_cb_info(env, info, &count, args, nullptr, nullptr));
    Check(count == 2);
    auto storage = std::make_shared<Storage>(); storage->env = env;
    storage->path = Text(env, Property(env, args[0], "path"));
    Check(!storage->path.empty() && storage->path[0] == '/' && storage->path.find('\0') == std::string::npos);
    napi_value identity = Property(env, args[0], "identity");
    storage->schema = Text(env, Property(env, identity, "sql")); storage->hash = Text(env, Property(env, identity, "hash"));
    Check(storage->schema.size() <= MaximumBytes && storage->schema.find('\0') == std::string::npos);
    unsigned char digest[CC_SHA256_DIGEST_LENGTH];
    CC_SHA256(storage->schema.data(), CC_LONG(storage->schema.size()), digest);
    const char* hex = "0123456789abcdef"; std::string actual;
    for (unsigned char byte : digest) { actual += hex[byte >> 4]; actual += hex[byte & 15]; }
    Check(actual == storage->hash, "STORAGE_SCHEMA");
    storage->application = int(std::get<int64_t>(ReadCell(env, Property(env, identity, "applicationId"))));
    storage->version = int(std::get<int64_t>(ReadCell(env, Property(env, identity, "version"))));
    N(napi_get_value_bool(env, Property(env, args[0], "initialize"), &storage->initialize));
    auto* retained = new std::shared_ptr<Storage>(storage);
    napi_value name; N(napi_create_string_utf8(env, "meowfilm-storage", NAPI_AUTO_LENGTH, &name));
    napi_status status = napi_create_threadsafe_function(env, args[1], nullptr, name, 66, 2, retained,
      [](napi_env, void* data, void*) { delete static_cast<std::shared_ptr<Storage>*>(data); },
      retained, Deliver, &storage->messages);
    if (status != napi_ok) { delete retained; N(status); }
    try {
    N(napi_create_async_work(env, nullptr, name,
      [](napi_env, void* data) {
        auto& item = *static_cast<Storage*>(data);
        if (item.writeThread.joinable()) item.writeThread.join();
        if (item.readThread.joinable()) item.readThread.join();
      },
      [](napi_env env, napi_status, void* data) {
        auto& item = *static_cast<Storage*>(data); auto keep = std::move(item.joinOwner);
        if (!item.environmentClosing) {
          if (item.closeReply) item.Send(std::move(item.closeReply));
          auto done = std::make_unique<Reply>(); done->id = -1; item.Send(std::move(done));
        }
        napi_release_threadsafe_function(item.messages, item.environmentClosing ? napi_tsfn_abort : napi_tsfn_release);
        item.messages = nullptr;
        if (item.cleanup) { napi_remove_async_cleanup_hook(item.cleanup); item.cleanup = nullptr; }
        napi_delete_async_work(env, item.joinWork); item.joinWork = nullptr;
      }, storage.get(), &storage->joinWork));
    N(napi_add_async_cleanup_hook(env, [](napi_async_cleanup_hook_handle, void* data) {
      auto& item = **static_cast<std::shared_ptr<Storage>*>(data);
      item.environmentClosing = true; item.Stop();
      napi_acquire_threadsafe_function(item.messages);
      napi_release_threadsafe_function(item.messages, napi_tsfn_abort);
      try { item.Join(); } catch (...) {
        if (item.writeThread.joinable()) item.writeThread.join();
        if (item.readThread.joinable()) item.readThread.join();
        if (item.cleanup) { napi_remove_async_cleanup_hook(item.cleanup); item.cleanup = nullptr; }
      }
    }, retained, &storage->cleanup));
    napi_value object, function; N(napi_create_object(env, &object));
    auto* wrapped = new std::shared_ptr<Storage>(storage);
    N(napi_wrap(env, object, wrapped, [](napi_env, void* data, void*) {
      std::unique_ptr<std::shared_ptr<Storage>> item(static_cast<std::shared_ptr<Storage>*>(data)); (*item)->Stop();
    }, nullptr, nullptr));
    N(napi_create_function(env, "request", NAPI_AUTO_LENGTH, RequestValue, nullptr, &function));
    N(napi_set_named_property(env, object, "request", function));
    storage->readThread = std::thread([storage] { storage->RunReader(); });
    storage->writeThread = std::thread([storage] { storage->RunWriter(); });
    return object;
    } catch (...) {
      storage->Stop();
      if (storage->readThread.joinable()) storage->readThread.join();
      if (storage->cleanup) { napi_remove_async_cleanup_hook(storage->cleanup); storage->cleanup = nullptr; }
      if (storage->joinWork) { napi_delete_async_work(env, storage->joinWork); storage->joinWork = nullptr; }
      napi_release_threadsafe_function(storage->messages, napi_tsfn_abort);
      napi_release_threadsafe_function(storage->messages, napi_tsfn_abort);
      throw;
    }
  });
}
}
void RegisterSQLiteStorage(napi_env env, napi_value exports) {
  napi_value function; N(napi_create_function(env, "openStorage", NAPI_AUTO_LENGTH, OpenValue, nullptr, &function));
  N(napi_set_named_property(env, exports, "openStorage", function));
  napi_value version; N(napi_create_string_utf8(env, sqlite3_libversion(), NAPI_AUTO_LENGTH, &version));
  N(napi_set_named_property(env, exports, "storageSQLiteVersion", version));
}
}
#endif
