#pragma once
#include "value.h"
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <deque>
#include <map>
#include <mutex>
#include <set>
#ifdef _WIN32
#include <windows.h>
#else
#include <pthread.h>
#endif

namespace meow {
int64_t Now();
struct Control {
  uint64_t scope = 0;
  std::shared_ptr<Control> parent;
  std::atomic<int> cancelled{0};
  std::atomic<int64_t> cancelledAt{0};
  std::atomic<int64_t> deadline{0};
  bool awaitingExecution = true;  // Engine mutex; only successfully admitted calls are watched.
  bool executionActive = true;  // Owner thread only; completion does not revoke authority.
  bool retired = false;  // Owner thread only; authority revoked, execution may still be draining.
  void Cancel(int status, int64_t at);
  int Status();
  int64_t CancellationTime() const;
  bool ExecutionActive() const { return executionActive && (!parent || parent->ExecutionActive()); }
};
struct Task {
  std::shared_ptr<Control> control;
};
struct Message {
  std::string type = "result", name, code;
  uint64_t id = 0, scope = 0;
  bool ok = true;
  Value value;
};
struct Command {
  enum Kind { Invoke, Complete, Refresh } kind = Invoke;
  uint64_t id = 0;
  std::string name;
  bool ok = true, gc = false;
  Value value;
  std::shared_ptr<Control> control;
};
struct Snapshot {
  std::atomic<int64_t> runtimeCount{0}, contextCount{0}, threadCount{0}, activeCalls{0};
  std::atomic<int64_t> hostCalls{0}, timers{0}, heapBytes{0}, heapObjects{0};
  std::atomic<int64_t> byteBuffers{0}, byteBufferBytes{0}, jobsPending{0};
  std::atomic<bool> stopped{false};
  Value Read() const;
};
struct Context;

class Engine : public std::enable_shared_from_this<Engine> {
 public:
  napi_env env = nullptr;
  // One independent TSFN usage right per participant; napi_closing consumes it.
  napi_threadsafe_function producerMessages = nullptr, completionMessages = nullptr;
  napi_async_cleanup_hook_handle cleanupHook = nullptr;
  napi_async_work joinWork = nullptr;
  std::shared_ptr<Engine> joinOwner;
  std::atomic<bool> stopping{false};
  bool joining = false, environmentClosing = false;  // Node owner only
  Snapshot snapshot;

  explicit Engine(Value spec);
  ~Engine();
  void Start();
  void Join();
  void Admit(Command command);
  void Cancel(uint64_t id, const std::string& reason);
  void Stop(const std::string& reason);
  void Send(std::unique_ptr<Message> message);
  static void SendOwned(napi_threadsafe_function& sender, std::unique_ptr<Message> message);
  static void ReleaseSender(napi_threadsafe_function& sender, napi_threadsafe_function_release_mode mode);
  void JoinAsync();
  void PrepareJoin();
  void CheckCancelled();
  static void CallNode(napi_env env, napi_value callback, void* context, void* data);
  static JSValue Bridge(JSContext*, JSValueConst, int, JSValueConst*, int);
  static JSValue CrossResult(JSContext*, JSValueConst, int, JSValueConst*, int, JSValueConst*);
  static int Interrupt(JSContext*, void*);

 private:
#ifdef _WIN32
  HANDLE thread = nullptr;
#else
  pthread_t thread{};
#endif
  bool threadStarted = false;  // Node owner until handed to the join work.
  Value spec;
  std::mutex mutex;
  std::condition_variable changed;
  std::deque<Command> commands;
  std::map<uint64_t, std::shared_ptr<Control>> controls;
  std::atomic<Control*> admissionWatch{nullptr};  // controls owns published pointers; only the owner retires them.
  std::map<uint64_t, std::weak_ptr<Control>> scopes;
  std::shared_ptr<Control> initialization;
  JSHostTaskContext* interruptedTask = nullptr;  // Retains a generator owner after its frame unwinds.
  Control* interrupted = nullptr;  // interruptedTask/controls retains the cause until EndExecution.
  bool completingHost = false;
  bool preprocessing = false, crossingGuest = false;
  size_t maxStackBytes;
  int64_t cleanupTimeoutMs;
  std::string stopReason = "STALE_SESSION";
  JSRuntime* runtime = nullptr;
  std::unique_ptr<Context> core, guest;
  std::set<std::string> guestHelpers;
  uint64_t nextHostId = 0, nextTimerId = 0;

  struct Call {
    uint64_t id;
    JSContext* ctx;
    JSValue promise = JS_UNDEFINED;
    JSHostTaskContext* task = nullptr;
    std::shared_ptr<Control> control;
  };
  struct Host {
    JSContext* ctx;
    JSValue resolve, reject;
    JSHostTaskContext* task;
  };
  struct Timer {
    JSContext* ctx;
    JSValue fn;
    std::vector<JSValue> args;
    JSHostTaskContext* task;
    int64_t due, interval;
  };
  std::map<uint64_t, Call> calls;
  std::map<uint64_t, Host> hosts;
  std::map<uint64_t, Timer> timers;

  void Run();
  void Initialize();
  void Cleanup();
  void WatchAdmission(Control* control);  // mutex held
  void DispatchAdmission(Control* control);  // mutex held
  bool ShouldInterrupt();  // Owner thread; no queue access or locking.
  void Execute(Command command);
  void Poll();
  void RunTimer();
  void Refresh();
  void Failure(Control* control, const std::string& code);
  JSHostTaskContext* PendingExecutionTask();
  bool HasPendingExecution(Control* control);
  void BeginExecution(JSHostTaskContext* task);
  void EndExecution();
  void Result(uint64_t id, Value value = {}, const std::string& code = "");
  void Revoke(Control* control);
  void FinishCall(std::map<uint64_t, Call>::iterator entry, Value value, const std::string& code);
  void ReleaseTimer(Timer& timer);
  JSValue CrossCall(Context& from, Context& to, int argc, JSValueConst* argv, bool synchronous);
  JSValue HostCall(Context&, int argc, JSValueConst* argv, bool positional);
  JSValue SetTimer(Context&, int argc, JSValueConst* argv, bool repeat);
  std::string CurrentError(JSContext* ctx);
};
struct Context {
  Engine* engine;
  JSContext* ctx;
  JSValue methods = JS_UNDEFINED;
  JSValue promiseThen = JS_UNDEFINED;
  bool guest;
};
}  // namespace meow
