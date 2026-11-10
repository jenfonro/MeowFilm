#include "engine.h"
#include "algorithms.h"
#include <algorithm>
#include <exception>
#include <limits>
#ifdef _WIN32
#include <process.h>
#endif

namespace meow {
int64_t Now() {
  return std::chrono::duration_cast<std::chrono::milliseconds>(
    std::chrono::steady_clock::now().time_since_epoch()).count();
}
void Control::Cancel(int status, int64_t at) {
  // Publish the first cancellation's time before its authority fence. A deadline
  // uses its actual expiry, even when the owner first observes it later.
  if (cancelled.load(std::memory_order_acquire)) return;
  int64_t previous = cancelledAt.load(std::memory_order_relaxed);
  while ((!previous || at < previous) &&
         !cancelledAt.compare_exchange_weak(previous, at, std::memory_order_relaxed)) {}
  int expected = 0;
  cancelled.compare_exchange_strong(expected, status, std::memory_order_release);
}
int Control::Status() {
  int status = cancelled.load(std::memory_order_acquire);
  if (!status && parent) status = parent->Status();
  int64_t end = deadline.load(std::memory_order_relaxed);
  if (!status && end && Now() >= end) {
    Cancel(2, end);
    status = cancelled.load(std::memory_order_acquire);
  }
  return status;
}
int64_t Control::CancellationTime() const {
  if (cancelled.load(std::memory_order_acquire)) return cancelledAt.load(std::memory_order_relaxed);
  return parent ? parent->CancellationTime() : 0;
}
static std::string CancelCode(int code) {
  return code == 2 ? "TIMEOUT" : code == 3 ? "STALE_SESSION" : "CANCELLED";
}
static Control* TaskControl(JSHostTaskContext* task) {
  auto* opaque = static_cast<Task*>(JS_GetHostTaskOpaque(task));
  return opaque ? opaque->control.get() : nullptr;
}
static bool ControlledBy(Control* control, Control* ancestor) {
  for (; control; control = control->parent.get()) if (control == ancestor) return true;
  return false;
}
static JSHostTaskContext* JobExecutionTask(JSHostTaskContext* registered, JSHostTaskContext* trigger) {
  auto* control = TaskControl(registered);
  // A live registration retains execution through cancellation cleanup. Once
  // drained, a later resolver controls execution without replacing the identity.
  return control && control->ExecutionActive() ? registered : trigger;
}
static JSHostTaskContext* NewTask(JSRuntime* runtime, std::shared_ptr<Control> control) {
  auto* opaque = new Task{std::move(control)};
  auto* task = JS_NewHostTaskContext(runtime, opaque, [](void* p) { delete static_cast<Task*>(p); });
  if (!task) { delete opaque; throw Fault("RESOURCE_LIMIT"); }
  return task;
}
class TaskScope {
  JSRuntime* runtime;
  JSHostTaskContext* previous;
 public:
  TaskScope(JSRuntime* rt, JSHostTaskContext* task) : runtime(rt),
    previous(JS_DupHostTaskContext(JS_GetHostTaskContext(rt))) { JS_SetHostTaskContext(rt, task); }
  ~TaskScope() { JS_SetHostTaskContext(runtime, previous); JS_FreeHostTaskContext(runtime, previous); }
};
class FlagScope {
  bool& flag;
  bool previous;
 public:
  explicit FlagScope(bool& value) : flag(value), previous(value) { flag = true; }
  ~FlagScope() { flag = previous; }
};
class PreprocessStackScope {
  JSRuntime* runtime;
  size_t previous;
 public:
  PreprocessStackScope(JSRuntime* rt, size_t stack) : runtime(rt), previous(stack) {
    // The existing stack top remains the origin: this is a finite total budget,
    // not an extra allowance on each call. No guest code may enter this scope.
    JS_SetMaxStackSize(runtime, JS_DEFAULT_STACK_SIZE);
  }
  ~PreprocessStackScope() { JS_SetMaxStackSize(runtime, previous); }
};
Value Snapshot::Read() const {
  Value out = Value::Obj();
#define FIELD(name) out.Set(#name, Value::Num(static_cast<double>(name.load(std::memory_order_relaxed))))
  FIELD(runtimeCount); FIELD(contextCount); FIELD(threadCount); FIELD(activeCalls);
  FIELD(hostCalls); FIELD(timers); FIELD(heapBytes); FIELD(heapObjects);
  FIELD(byteBuffers); FIELD(byteBufferBytes); FIELD(jobsPending);
#undef FIELD
  out.Set("stopped", Value::Bool(stopped.load(std::memory_order_acquire)));
  return out;
}
Engine::Engine(Value options) : spec(std::move(options)), initialization(std::make_shared<Control>()) {
  Check(spec.Get("source").kind == Value::String);
  Check(spec.Get("guestSource").kind == Value::String || spec.Get("guestSource").kind == Value::Null);
  auto stack = spec.Get("maxStackBytes").Integer();
  Check(stack > 0 && stack <= 1024 * 1024);  // Independent execution-stack contract.
  maxStackBytes = static_cast<size_t>(stack);
  auto ms = spec.Get("timeoutMs").Integer();
  if (ms) initialization->deadline = Now() + static_cast<int64_t>(ms);
  cleanupTimeoutMs = static_cast<int64_t>(spec.Get("cleanupTimeoutMs").Integer());
  Check(cleanupTimeoutMs > 0);
  const auto& helpers = spec.Get("guestHelpers");
  Check(helpers.kind == Value::Array);
  for (const auto& helper : helpers.array) guestHelpers.insert(helper.Text());
}
Engine::~Engine() {
  // All public and environment-close paths join before dropping the TSFN owner.
  if (threadStarted) std::terminate();
}
void Engine::Start() {
  // Cover the largest supported 1 MiB JS budget plus native entry/unwind
  // headroom. Darwin's default secondary thread has only 512 KiB in total.
  constexpr size_t stackBytes = 2 * 1024 * 1024;
  auto owner = std::make_unique<std::shared_ptr<Engine>>(shared_from_this());
#ifdef _WIN32
  auto handle = _beginthreadex(nullptr, stackBytes, [](void* data) -> unsigned {
    std::unique_ptr<std::shared_ptr<Engine>> self(static_cast<std::shared_ptr<Engine>*>(data));
    (*self)->Run();
    return 0;
  }, owner.get(), STACK_SIZE_PARAM_IS_A_RESERVATION, nullptr);
  Check(handle != 0, "RESOURCE_LIMIT");
  thread = reinterpret_cast<HANDLE>(handle);
#else
  pthread_attr_t attributes;
  Check(pthread_attr_init(&attributes) == 0, "RESOURCE_LIMIT");
  int status = pthread_attr_setstacksize(&attributes, stackBytes);
  if (status == 0) status = pthread_create(&thread, &attributes, [](void* data) -> void* {
    std::unique_ptr<std::shared_ptr<Engine>> self(static_cast<std::shared_ptr<Engine>*>(data));
    (*self)->Run();
    return nullptr;
  }, owner.get());
  pthread_attr_destroy(&attributes);
  Check(status == 0, "RESOURCE_LIMIT");
#endif
  owner.release();  // The successfully started thread owns this reference.
  threadStarted = true;
}
void Engine::Join() {
  if (!threadStarted) return;
#ifdef _WIN32
  if (WaitForSingleObject(thread, INFINITE) != WAIT_OBJECT_0 || !CloseHandle(thread)) std::terminate();
  thread = nullptr;
#else
  if (pthread_join(thread, nullptr) != 0) std::terminate();
#endif
  threadStarted = false;
}
void Engine::Admit(Command command) {
  std::lock_guard<std::mutex> lock(mutex);
  Check(!stopping.load(), "STALE_SESSION");
  if (command.kind == Command::Invoke) {
    Check(!controls.count(command.id));
    auto control = command.control;
    uint64_t id = command.id;
    controls.emplace(id, control);
    try {
      if (control->scope) scopes[control->scope] = control;
      for (auto entry = scopes.begin(); entry != scopes.end();) {
        if (entry->second.expired()) entry = scopes.erase(entry); else ++entry;
      }
      commands.push_back(std::move(command));
    } catch (...) {
      controls.erase(id);
      throw;
    }
    // Publish only after admission succeeds; failed/unsent calls cannot interrupt an owner.
    WatchAdmission(control.get());
  } else commands.push_back(std::move(command));
  changed.notify_one();
}
void Engine::WatchAdmission(Control* control) {
  auto* watched = admissionWatch.load(std::memory_order_relaxed);
  if (watched && watched->Status()) return;
  int64_t deadline = control->deadline.load(std::memory_order_relaxed);
  if (!watched || control->Status() || (deadline &&
      (!watched->deadline.load(std::memory_order_relaxed) ||
       deadline < watched->deadline.load(std::memory_order_relaxed))))
    admissionWatch.store(control, std::memory_order_release);
}
void Engine::DispatchAdmission(Control* control) {
  control->awaitingExecution = false;
  if (admissionWatch.load(std::memory_order_relaxed) != control) return;
  admissionWatch.store(nullptr, std::memory_order_release);
  for (const auto& entry : controls)
    if (entry.second->awaitingExecution) WatchAdmission(entry.second.get());
}
void Engine::Cancel(uint64_t id, const std::string& reason) {
  std::lock_guard<std::mutex> lock(mutex);
  auto entry = controls.find(id);
  if (entry != controls.end()) {
    entry->second->Cancel(reason == "TIMEOUT" ? 2 : 1, Now());
    if (entry->second->awaitingExecution) WatchAdmission(entry->second.get());
  }
  changed.notify_one();
}
void Engine::Stop(const std::string& reason) {
  std::lock_guard<std::mutex> lock(mutex);
  if (!stopping.exchange(true)) stopReason = SafeCode(reason) ? reason : "STALE_SESSION";
  changed.notify_one();
}
void Engine::Send(std::unique_ptr<Message> message) {
  SendOwned(producerMessages, std::move(message));
}
void Engine::SendOwned(napi_threadsafe_function& sender, std::unique_ptr<Message> message) {
  if (!sender) return;
  auto* data = message.release();
  napi_status status = napi_call_threadsafe_function(sender, data, napi_tsfn_nonblocking);
  if (status != napi_ok) delete data;
  // Node consumes this participant's usage right when Push reports closing.
  // Neither another Push nor Release is legal afterwards, even during cleanup.
  if (status == napi_closing) sender = nullptr;
}
void Engine::ReleaseSender(napi_threadsafe_function& sender, napi_threadsafe_function_release_mode mode) {
  auto owned = sender; sender = nullptr;
  if (owned) napi_release_threadsafe_function(owned, mode);
}
void Engine::Result(uint64_t id, Value value, const std::string& code) {
  auto message = std::make_unique<Message>();
  message->id = id; message->ok = code.empty(); message->code = code; message->value = std::move(value);
  Send(std::move(message));
}
int Engine::Interrupt(JSContext* ctx, void* opaque) {
  auto* engine = static_cast<Engine*>(opaque);
  auto* runtime = JS_GetRuntime(ctx);
  if (engine->stopping.load(std::memory_order_relaxed)) return 1;
  // A host resolver polls even before entering a getter. Only actual script
  // frames make a completion/cancel race an interrupted user turn.
  if ((!engine->completingHost || JS_HasActiveScriptFrame(runtime)) && engine->ShouldInterrupt()) return 1;
  return 0;
}
bool Engine::ShouldInterrupt() {
  if (interrupted) return true;
  auto* owner = JS_GetHostExecutionOwner(runtime);
  auto* executing = TaskControl(owner);
  if (executing && executing->ExecutionActive() && executing->Status() &&
      Now() - executing->CancellationTime() >= cleanupTimeoutMs) {
    interruptedTask = JS_DupHostTaskContext(owner);
    interrupted = executing;
    return true;
  }
  // Background authority has no active request lifetime. A later admitted call
  // must still be able to interrupt it, without changing that authority's identity.
  if (!executing || !executing->ExecutionActive()) {
    // Node can replace the watch, but only this owner dispatches/frees its Control.
    auto* waiting = admissionWatch.load(std::memory_order_acquire);
    if (waiting && waiting->Status() &&
        Now() - waiting->CancellationTime() >= cleanupTimeoutMs && JS_HasActiveScriptFrame(runtime)) {
      interrupted = waiting;
      return true;
    }
  }
  return false;
}
void Engine::CheckCancelled() {
  if (stopping.load(std::memory_order_relaxed)) throw Fault("STALE_SESSION");
  if (ShouldInterrupt()) throw Fault(CancelCode(interrupted->Status()));
}
JSHostTaskContext* Engine::PendingExecutionTask() {
  return JobExecutionTask(JS_GetPendingJobTaskContext(runtime), JS_GetPendingJobExecutionContext(runtime));
}
bool Engine::HasPendingExecution(Control* control) {
  return JS_HasPendingJobMatching(runtime, [](JSHostTaskContext* registered,
      JSHostTaskContext* trigger, void* opaque) {
    return ControlledBy(TaskControl(JobExecutionTask(registered, trigger)), static_cast<Control*>(opaque));
  }, control);
}
void Engine::BeginExecution(JSHostTaskContext* task) {
  interrupted = nullptr;
  JS_SetHostExecutionOwner(runtime, task);
  JS_SetHostExecutionContext(runtime, task);
}
void Engine::EndExecution() {
  interrupted = nullptr; completingHost = false; JS_SetHostExecutionContext(runtime, nullptr);
  JS_SetHostExecutionOwner(runtime, nullptr);
  JS_FreeHostTaskContext(runtime, interruptedTask); interruptedTask = nullptr;
}
void Engine::Failure(Control* control, const std::string& code) {
  if (stopping.load()) return;
  auto message = std::make_unique<Message>();
  message->type = "event"; message->name = "failure"; message->scope = control ? control->scope : 0;
  message->value = Value::Obj(); message->value.Set("code", Value::Str(code));
  Send(std::move(message));
  Stop(code);
}
std::string Engine::CurrentError(JSContext* ctx) {
  std::string code = ExceptionCode(ctx);
  if (stopping.load()) return "STALE_SESSION";
  if (interrupted) return CancelCode(interrupted->Status());
  auto* control = TaskControl(JS_GetHostTaskContext(runtime));
  return control && control->Status() ? CancelCode(control->Status()) : code;
}
void Engine::Initialize() {
  runtime = JS_NewRuntime();
  Check(runtime != nullptr, "RESOURCE_LIMIT"); snapshot.runtimeCount = 1;
  const auto& heap = spec.Get("maxHeapBytes");
  if (heap.kind != Value::Null && heap.Integer())
    JS_SetMemoryLimit(runtime, static_cast<size_t>(heap.Integer()));
  JS_SetMaxStackSize(runtime, maxStackBytes);
  JS_SetInterruptHandler(runtime, Interrupt, this);
  JS_SetHostExecutionOwnerSelector(runtime, JobExecutionTask);
  auto* initTask = NewTask(runtime, initialization);
  TaskScope scope(runtime, initTask); JS_FreeHostTaskContext(runtime, initTask);
  BeginExecution(JS_GetHostTaskContext(runtime));
  const char* names[] = {"__meowHost", "__meowEmit", "__meowNative", "__meowTaskContext",
                        "__meowGuest", "__meowCore", "setTimeout", "setInterval",
                        "clearTimeout", "clearInterval", "__meowHostArgs"};
  auto build = [&](bool isGuest, const Value& source) {
    auto context = std::make_unique<Context>();
    context->engine = this; context->guest = isGuest; context->ctx = JS_NewContext(runtime);
    Check(context->ctx != nullptr, "RESOURCE_LIMIT"); ++snapshot.contextCount;
    // Publish ownership before evaluating code so every partial failure is reclaimable.
    Context* current = context.get();
    if (isGuest) guest = std::move(context); else core = std::move(context);
    JS_SetContextOpaque(current->ctx, current);
    QValue global(current->ctx, JS_GetGlobalObject(current->ctx));
    QValue promise(current->ctx, QCheck(current->ctx, JS_GetPropertyStr(current->ctx, global.value, "Promise")));
    QValue prototype(current->ctx, QCheck(current->ctx, JS_GetPropertyStr(current->ctx, promise.value, "prototype")));
    current->promiseThen = QCheck(current->ctx, JS_GetPropertyStr(current->ctx, prototype.value, "then"));
    for (int i = 0; i < 11; ++i) {
      if ((i == 4 && isGuest) || (i == 5 && !isGuest)) continue;
      JSValue fn = JS_NewCFunctionMagic(current->ctx, Bridge, names[i], 0, JS_CFUNC_generic_magic, i);
      if (JS_DefinePropertyValueStr(current->ctx, global.value, names[i], fn, JS_PROP_CONFIGURABLE) < 0)
        throw Fault(CurrentError(current->ctx));
    }
    // Only a two-realm owner's trusted core can capture this capability.
    // Single-realm LX/JSON programs and the guest never receive it.
    if (!isGuest && spec.Get("guestSource").kind == Value::String) {
      JSValue fn = JS_NewCFunctionMagic(current->ctx, Bridge, "__meowPreprocess", 1,
        JS_CFUNC_generic_magic, 11);
      if (JS_DefinePropertyValueStr(current->ctx, global.value, "__meowPreprocess", fn,
          JS_PROP_CONFIGURABLE) < 0) throw Fault(CurrentError(current->ctx));
    }
    std::string text = source.Text();
    current->methods = JS_Eval(current->ctx, text.data(), text.size(),
      isGuest ? "guest-bootstrap.js" : "core-bootstrap.js", JS_EVAL_TYPE_GLOBAL);
    if (JS_IsException(current->methods)) {
      current->methods = JS_UNDEFINED;
      throw Fault(CurrentError(current->ctx));
    }
    Check(JS_IsObject(current->methods) && !JS_IsPromise(current->methods), "INVALID_ARGUMENT");
    CheckCancelled();
    if (initialization->Status()) throw Fault(CancelCode(initialization->Status()));
  };
  build(false, spec.Get("source"));
  if (spec.Get("guestSource").kind == Value::String) build(true, spec.Get("guestSource"));
  initialization->deadline = 0;
  initialization->executionActive = false;
  EndExecution();
  spec = {};  // Do not retain a second copy of each bootstrap after compilation.
  Refresh(); Result(0);
}
static JSValue MethodCall(Context& context, const std::string& method, const Value& args) {
  Check(args.kind == Value::Array);
  JSAtom atom = JS_NewAtomLen(context.ctx, method.data(), method.size());
  Check(atom != JS_ATOM_NULL, "RESOURCE_LIMIT");
  JSPropertyDescriptor descriptor{};
  int own = JS_GetOwnProperty(context.ctx, &descriptor, context.methods, atom);
  JS_FreeAtom(context.ctx, atom);
  if (own < 0) throw Fault(ExceptionCode(context.ctx));
  Check(own == 1, "UNSUPPORTED_OPTION");
  QValue function(context.ctx, descriptor.value);
  QValue getter(context.ctx, descriptor.getter), setter(context.ctx, descriptor.setter);
  Check(JS_IsFunction(context.ctx, function.value), "UNSUPPORTED_OPTION");
  std::vector<QValue> handles; std::vector<JSValue> values;
  handles.reserve(args.array.size()); values.reserve(args.array.size());
  for (const auto& input : args.array) {
    handles.emplace_back(context.ctx, ToQuick(context.ctx, input)); values.push_back(handles.back().value);
  }
  return JS_Call(context.ctx, function.value, context.methods, static_cast<int>(values.size()), values.data());
}
void Engine::Execute(Command command) {
  if (command.kind == Command::Refresh) {
    if (command.gc) JS_RunGC(runtime);
    Refresh(); Result(command.id, snapshot.Read()); return;
  }
  if (command.kind == Command::Complete) {
    auto entry = hosts.find(command.id);
    if (entry == hosts.end()) return;
    auto* control = TaskControl(entry->second.task);
    if (control && control->Status()) return;  // Poll retires and rejects the logical operation.
    Host host = entry->second; hosts.erase(entry);
    {
      TaskScope scope(runtime, host.task);
      BeginExecution(host.task);
      completingHost = true;
      // Resolving cloned arrays can enter an inherited `then` getter. Do not
      // preflight CheckCancelled: a data-only completion is not a script turn.
      try {
        bool ok = command.ok;
        QValue value(host.ctx, ok ? ToQuick(host.ctx, command.value)
          : QError(host.ctx, SafeCode(command.value.Text()) ? command.value.Text() : "SCRIPT_EXCEPTION"));
        if (JS_IsException(value.value)) { value.value = JS_GetException(host.ctx); ok = false; }
        QValue result(host.ctx, JS_Call(host.ctx, ok ? host.resolve : host.reject,
          JS_UNDEFINED, 1, &value.value));
        if (JS_IsException(result.value)) { QValue ignored(host.ctx, JS_GetException(host.ctx)); }
      } catch (const std::exception& error) {
        QValue value(host.ctx, QError(host.ctx, dynamic_cast<const Fault*>(&error) ? error.what() : "RESOURCE_LIMIT"));
        if (JS_IsException(value.value)) value.value = JS_GetException(host.ctx);
        QValue result(host.ctx, JS_Call(host.ctx, host.reject, JS_UNDEFINED, 1, &value.value));
        if (JS_IsException(result.value)) { QValue ignored(host.ctx, JS_GetException(host.ctx)); }
      }
      if (interrupted) Failure(interrupted, CancelCode(interrupted->Status()));
      EndExecution();
    }
    JS_FreeValue(host.ctx, host.resolve); JS_FreeValue(host.ctx, host.reject);
    JS_FreeHostTaskContext(runtime, host.task);
    return;
  }
  Call call{command.id, core->ctx, JS_UNDEFINED, NewTask(runtime, command.control), command.control};
  std::map<uint64_t, Call>::iterator entry;
  try { entry = calls.emplace(command.id, std::move(call)).first; }
  catch (...) { JS_FreeHostTaskContext(runtime, call.task); throw; }
  snapshot.activeCalls = calls.size();
  TaskScope scope(runtime, entry->second.task);
  BeginExecution(entry->second.task);
  try {
    if (command.control->Status()) throw Fault(CancelCode(command.control->Status()));
    QValue result(core->ctx, MethodCall(*core, command.name, command.value));
    if (JS_IsException(result.value)) throw Fault(CurrentError(core->ctx));
    CheckCancelled();
    if (JS_IsPromise(result.value)) entry->second.promise = result.Release();
    else FinishCall(entry, FromQuick(core->ctx, result.value), "");
  } catch (const std::exception& error) {
    if (interrupted) Failure(interrupted, CancelCode(interrupted->Status()));
    auto found = calls.find(command.id);
    if (found != calls.end()) FinishCall(found, {}, dynamic_cast<const Fault*>(&error) ? error.what() : "RESOURCE_LIMIT");
  }
  EndExecution();
}
void Engine::Revoke(Control* control) {
  if (!control->retired) {
    auto message = std::make_unique<Message>(); message->type = "cancelScope";
    message->scope = control->scope; message->code = CancelCode(control->Status()); Send(std::move(message));
    control->retired = true;
  }
}
void Engine::FinishCall(std::map<uint64_t, Call>::iterator entry, Value value, const std::string& code) {
  auto& call = entry->second;
  int status = call.control->Status();
  if (status) {
    Revoke(call.control.get());
    if (!stopping.load()) {
      // Cancellation can arrive after Poll's host pass or during a synchronous
      // result. Keep control until the next pass rejects its remaining I/O.
      for (const auto& host : hosts)
        if (ControlledBy(TaskControl(host.second.task), call.control.get())) return;
      if (HasPendingExecution(call.control.get())) {
        if (Now() - call.control->CancellationTime() < cleanupTimeoutMs) return;
        Failure(call.control.get(), CancelCode(status));
      }
    }
  }
  call.control->executionActive = false;
  if (!status) call.control->deadline = 0;
  {
    std::lock_guard<std::mutex> lock(mutex); controls.erase(call.id);
  }
  Result(call.id, std::move(value), status ? CancelCode(status) : code);
  JS_FreeValue(call.ctx, call.promise); JS_FreeHostTaskContext(runtime, call.task);
  calls.erase(entry);
  snapshot.activeCalls = calls.size();
}
void Engine::Poll() {
  // Reject cancelled I/O before deciding whether a call has runnable cleanup.
  // These rejections can enqueue the very reactions that must retain its control.
  for (auto entry = hosts.begin(); entry != hosts.end();) {
    auto* control = TaskControl(entry->second.task);
    if (!control || !control->Status()) { ++entry; continue; }
    auto message = std::make_unique<Message>(); message->type = "cancelHost";
    message->id = entry->first; message->code = CancelCode(control->Status()); Send(std::move(message));
    Host host = entry->second; entry = hosts.erase(entry);
    {
      TaskScope scope(runtime, host.task);
      // JS_NewError can enter Error.prepareStackTrace even with own data fields.
      // Retain the cancelled request's existing cleanup control for that script.
      BeginExecution(host.task);
      completingHost = true;  // A data-only rejection is not a script turn.
      QValue error(host.ctx, QError(host.ctx, "STALE_SESSION"));
      if (JS_IsException(error.value)) error.value = JS_GetException(host.ctx);
      QValue result(host.ctx, JS_Call(host.ctx, host.reject, JS_UNDEFINED, 1, &error.value));
      if (JS_IsException(result.value)) { QValue ignored(host.ctx, JS_GetException(host.ctx)); }
      if (interrupted) Failure(interrupted, CancelCode(interrupted->Status()));
      EndExecution();
    }
    JS_FreeValue(host.ctx, host.resolve); JS_FreeValue(host.ctx, host.reject);
    JS_FreeHostTaskContext(runtime, host.task);
  }
  for (auto entry = timers.begin(); entry != timers.end();) {
    auto* control = TaskControl(entry->second.task);
    if (!control || !control->Status()) { ++entry; continue; }
    ReleaseTimer(entry->second); entry = timers.erase(entry);
  }
  for (auto entry = calls.begin(); entry != calls.end();) {
    auto current = entry++;
    auto& call = current->second;
    if (call.control->Status()) {
      FinishCall(current, {}, CancelCode(call.control->Status())); continue;
    }
    auto state = JS_PromiseState(call.ctx, call.promise);
    if (state == JS_PROMISE_PENDING) continue;
    TaskScope scope(runtime, call.task);
    BeginExecution(call.task);
    QValue result(call.ctx, JS_PromiseResult(call.ctx, call.promise));
    Value value;
    std::string code;
    try {
      if (state == JS_PROMISE_REJECTED) {
        JS_Throw(call.ctx, JS_DupValue(call.ctx, result.value));
        code = CurrentError(call.ctx);
      } else value = FromQuick(call.ctx, result.value);
    } catch (const std::exception& error) {
      code = dynamic_cast<const Fault*>(&error) ? error.what() : "RESOURCE_LIMIT";
    }
    // Reading a rejection's properties can enter script even when safe error
    // extraction absorbs its exception. Retain its actual owner through failure.
    if (interrupted) Failure(interrupted, CancelCode(interrupted->Status()));
    FinishCall(current, std::move(value), code);
    EndExecution();
  }
}
void Engine::ReleaseTimer(Timer& timer) {
  JS_FreeValue(timer.ctx, timer.fn);
  for (auto arg : timer.args) JS_FreeValue(timer.ctx, arg);
  JS_FreeHostTaskContext(runtime, timer.task);
}
void Engine::RunTimer() {
  auto due = timers.end();
  for (auto entry = timers.begin(); entry != timers.end(); ++entry)
    if (entry->second.due <= Now() && (due == timers.end() || entry->second.due < due->second.due)) due = entry;
  if (due == timers.end()) return;
  uint64_t id = due->first;
  Timer timer = std::move(due->second); timers.erase(due);
  auto* control = TaskControl(timer.task);
  if (control && control->Status()) { ReleaseTimer(timer); return; }
  // Reinsert repeating timers before invoking so clearInterval inside the callback works.
  if (timer.interval) {
    Timer repeat;
    try { repeat = timer; }
    catch (...) { ReleaseTimer(timer); throw; }
    repeat.fn = JS_DupValue(timer.ctx, timer.fn);
    for (auto& arg : repeat.args) arg = JS_DupValue(timer.ctx, arg);
    repeat.task = JS_DupHostTaskContext(timer.task); repeat.due = Now() + timer.interval;
    try { timers.emplace(id, std::move(repeat)); }
    catch (...) { ReleaseTimer(repeat); ReleaseTimer(timer); throw; }
  }
  {
    TaskScope scope(runtime, timer.task);
    BeginExecution(timer.task);
    try {
      CheckCancelled();
      QValue result(timer.ctx, JS_Call(timer.ctx, timer.fn, JS_UNDEFINED,
        static_cast<int>(timer.args.size()), timer.args.data()));
      if (JS_IsException(result.value)) {
        auto message = std::make_unique<Message>(); message->type = "event"; message->name = "diagnostic";
        message->code = CurrentError(timer.ctx); message->value = Value::Str(message->code);
        auto* control = TaskControl(timer.task); message->scope = control ? control->scope : 0;
        Send(std::move(message));
      }
    } catch (...) {}
    if (interrupted) Failure(interrupted, CancelCode(interrupted->Status()));
    EndExecution();
  }
  ReleaseTimer(timer);
}
void Engine::Refresh() {
  if (!runtime) return;
  JSMemoryUsage memory; JS_ComputeMemoryUsage(runtime, &memory);
  snapshot.activeCalls = calls.size(); snapshot.hostCalls = hosts.size(); snapshot.timers = timers.size();
  snapshot.heapBytes = memory.memory_used_size; snapshot.heapObjects = memory.obj_count;
  snapshot.byteBuffers = memory.array_buffer_count; snapshot.byteBufferBytes = memory.array_buffer_size;
  snapshot.jobsPending = JS_IsJobPending(runtime) ? 1 : 0;
}
void Engine::Cleanup() {
  if (!runtime) return;
  for (auto& entry : calls) {
    JS_FreeValue(entry.second.ctx, entry.second.promise);
    JS_FreeHostTaskContext(runtime, entry.second.task);
  }
  calls.clear();
  for (auto& entry : hosts) {
    JS_FreeValue(entry.second.ctx, entry.second.resolve); JS_FreeValue(entry.second.ctx, entry.second.reject);
    JS_FreeHostTaskContext(runtime, entry.second.task);
  }
  hosts.clear();
  for (auto& entry : timers) ReleaseTimer(entry.second);
  timers.clear();
  JS_SetHostTaskContext(runtime, nullptr);
  EndExecution();
  for (auto* context : {guest.get(), core.get()}) if (context) {
    JS_FreeValue(context->ctx, context->promiseThen);
    JS_FreeValue(context->ctx, context->methods); JS_FreeContext(context->ctx);
  }
  guest.reset(); core.reset(); JS_FreeRuntime(runtime); runtime = nullptr;
  snapshot.runtimeCount = 0; snapshot.contextCount = 0; snapshot.activeCalls = 0;
  snapshot.hostCalls = 0; snapshot.timers = 0; snapshot.heapBytes = 0; snapshot.heapObjects = 0;
  snapshot.byteBuffers = 0; snapshot.byteBufferBytes = 0; snapshot.jobsPending = 0;
}
void Engine::Run() {
  snapshot.threadCount = 1;
  try {
    Initialize();
    while (!stopping.load()) {
      Command command; bool have = false;
      {
        std::lock_guard<std::mutex> lock(mutex);
        if (!commands.empty()) {
          command = std::move(commands.front()); commands.pop_front(); have = true;
          if (command.kind == Command::Invoke) DispatchAdmission(command.control.get());
        }
      }
      if (have) Execute(std::move(command));
      Poll();
      if (stopping.load()) break;
      // One job per scheduler pass keeps commands/timers/cancel progressing through infinite chains.
      if (JS_IsJobPending(runtime)) {
        BeginExecution(PendingExecutionTask());
        if (!ShouldInterrupt()) {
          JSContext* ctx;
          if (JS_ExecutePendingJob(runtime, &ctx) < 0 && ctx) {
            QValue ignored(ctx, JS_GetException(ctx));
          }
        }
        if (interrupted) Failure(interrupted, CancelCode(interrupted->Status()));
        EndExecution();
      } else RunTimer();
      Poll();
      snapshot.activeCalls = calls.size(); snapshot.hostCalls = hosts.size();
      snapshot.timers = timers.size(); snapshot.jobsPending = JS_IsJobPending(runtime) ? 1 : 0;
      std::unique_lock<std::mutex> lock(mutex);
      // Cancel updates the persistent Control under this same mutex. A notify
      // between Poll and locking must still prevent sleep without a new command.
      auto runnable = [&] {
        if (!commands.empty() || JS_IsJobPending(runtime) || stopping.load()) return true;
        for (const auto& entry : calls) if (entry.second.control->Status()) return true;
        return false;
      };
      if (!runnable()) {
        int64_t wake = std::numeric_limits<int64_t>::max();
        for (const auto& entry : calls) {
          int64_t deadline = entry.second.control->deadline.load();
          if (deadline) wake = std::min(wake, deadline);
        }
        for (const auto& entry : timers) wake = std::min(wake, entry.second.due);
        if (wake == std::numeric_limits<int64_t>::max()) changed.wait(lock, runnable);
        else changed.wait_until(lock, std::chrono::steady_clock::time_point(std::chrono::milliseconds(wake)), runnable);
      }
    }
  } catch (const std::exception& error) {
    std::string code = dynamic_cast<const Fault*>(&error) ? error.what() : "RESOURCE_LIMIT";
    if (interrupted) Failure(interrupted, CancelCode(interrupted->Status()));
    Result(0, {}, code); Stop(code);
  } catch (...) { Result(0, {}, "SCRIPT_EXCEPTION"); Stop("SCRIPT_EXCEPTION"); }
  Cleanup();
  {
    std::lock_guard<std::mutex> lock(mutex);
    admissionWatch.store(nullptr, std::memory_order_release);
    commands.clear(); controls.clear(); scopes.clear();
  }
  auto message = std::make_unique<Message>(); message->type = "join"; Send(std::move(message));
  ReleaseSender(producerMessages, napi_tsfn_release);
}

JSValue Engine::HostCall(Context& context, int argc, JSValueConst* argv, bool positional) {
  Check(argc >= 2); CheckCancelled();
  auto name = Utf8(QString(context.ctx, argv[0]));
  auto input = positional ? ArgsFromQuick(context.ctx, argv[1]) : FromQuick(context.ctx, argv[1]);
  auto message = std::make_unique<Message>(); message->type = "host";
  message->name = std::move(name); message->value = std::move(input);
  auto* current = JS_GetHostTaskContext(runtime);
  if (argc >= 3 && !JS_IsUndefined(argv[2])) {
    auto scope = FromQuick(context.ctx, argv[2]).Integer();
    auto* control = TaskControl(current);
    Check(!control || scope == control->scope, "STALE_SESSION");
  }
  JSHostTaskContext* task = JS_DupHostTaskContext(current);
  JSValue resolve[2]; JSValue promise = JS_NewPromiseCapability(context.ctx, resolve);
  if (JS_IsException(promise)) { JS_FreeHostTaskContext(runtime, task); return promise; }
  QValue held(context.ctx, promise);
  uint64_t id = ++nextHostId;
  try { hosts.emplace(id, Host{context.ctx, resolve[0], resolve[1], task}); }
  catch (...) {
    JS_FreeValue(context.ctx, resolve[0]); JS_FreeValue(context.ctx, resolve[1]);
    JS_FreeHostTaskContext(runtime, task); throw;
  }
  message->id = id;
  auto* control = TaskControl(task); message->scope = control ? control->scope : 0;
  Send(std::move(message));
  return held.Release();
}
JSValue Engine::SetTimer(Context& context, int argc, JSValueConst* argv, bool repeat) {
  Check(argc >= 1 && JS_IsFunction(context.ctx, argv[0]));
  double delay = 0;
  if (argc > 1) Check(JS_ToFloat64(context.ctx, &delay, argv[1]) == 0);
  if (!std::isfinite(delay) || delay < 1 || delay > INT32_MAX) delay = 1;
  Timer timer{context.ctx, JS_DupValue(context.ctx, argv[0]), {},
    JS_DupHostTaskContext(JS_GetHostTaskContext(runtime)), Now() + static_cast<int64_t>(delay),
    repeat ? static_cast<int64_t>(delay) : 0};
  try {
    timer.args.reserve(argc > 2 ? static_cast<size_t>(argc - 2) : 0);
    for (int i = 2; i < argc; ++i) timer.args.push_back(JS_DupValue(context.ctx, argv[i]));
  }
  catch (...) { ReleaseTimer(timer); throw; }
  auto id = ++nextTimerId;
  try { timers.emplace(id, std::move(timer)); }
  catch (...) { ReleaseTimer(timer); throw; }
  return JS_NewFloat64(context.ctx, id);
}
JSValue Engine::CrossCall(Context& from, Context& to, int argc, JSValueConst* argv, bool synchronous) {
  // A guest-origin helper must not borrow the core's preprocessing budget.
  FlagScope crossing(crossingGuest);
  Check(argc >= 2 && !JS_IsUndefined(to.methods), "STALE_SESSION");
  auto method = Utf8(QString(from.ctx, argv[0]));
  if (synchronous) Check(guestHelpers.count(method) != 0, "UNSUPPORTED_OPTION");
  auto args = ArgsFromQuick(from.ctx, argv[1]);
  // Protect the caller's task context even when a trusted helper changes its own context.
  TaskScope scope(runtime, JS_GetHostTaskContext(runtime));
  QValue result(to.ctx, MethodCall(to, method, args));
  if (JS_IsException(result.value)) throw Fault(CurrentError(to.ctx));
  CheckCancelled();
  if (!JS_IsPromise(result.value)) return ToQuick(from.ctx, FromQuick(to.ctx, result.value));
  Check(!synchronous);
  JSValue resolve[2];
  QValue promise(from.ctx, QCheck(from.ctx, JS_NewPromiseCapability(from.ctx, resolve)));
  QValue fulfill(from.ctx, resolve[0]), reject(from.ctx, resolve[1]);
  JSValue data[] = { fulfill.value, reject.value };
  QValue onFulfilled(to.ctx, QCheck(to.ctx, JS_NewCFunctionData(to.ctx, CrossResult, 1, 0, 2, data)));
  QValue onRejected(to.ctx, QCheck(to.ctx, JS_NewCFunctionData(to.ctx, CrossResult, 1, 1, 2, data)));
  JSValue callbacks[] = { onFulfilled.value, onRejected.value };
  QValue reaction(to.ctx, QCheck(to.ctx, JS_Call(to.ctx, to.promiseThen, result.value, 2, callbacks)));
  return promise.Release();
}
JSValue Engine::CrossResult(JSContext* ctx, JSValueConst, int argc, JSValueConst* argv,
                           int rejected, JSValueConst* data) {
  auto& context = *static_cast<Context*>(JS_GetContextOpaque(ctx));
  auto& engine = *context.engine;
  JSContext* destination = engine.core->ctx;
  try {
    QValue value(destination, JS_UNDEFINED);
    if (rejected) {
      JS_Throw(ctx, JS_DupValue(ctx, argc ? argv[0] : JS_UNDEFINED));
      value.value = QError(destination, ExceptionCode(ctx));
    } else value.value = ToQuick(destination, FromQuick(ctx, argc ? argv[0] : JS_UNDEFINED));
    if (JS_IsException(value.value)) { value.value = JS_GetException(destination); rejected = 1; }
    QValue result(destination, JS_Call(destination, data[rejected ? 1 : 0], JS_UNDEFINED, 1, &value.value));
    if (JS_IsException(result.value)) { QValue ignored(destination, JS_GetException(destination)); }
  } catch (const std::exception& error) {
    QValue value(destination, QError(destination, dynamic_cast<const Fault*>(&error) ? error.what() : "RESOURCE_LIMIT"));
    if (JS_IsException(value.value)) value.value = JS_GetException(destination);
    QValue result(destination, JS_Call(destination, data[1], JS_UNDEFINED, 1, &value.value));
    if (JS_IsException(result.value)) { QValue ignored(destination, JS_GetException(destination)); }
  }
  return JS_UNDEFINED;
}
JSValue Engine::Bridge(JSContext* ctx, JSValueConst, int argc, JSValueConst* argv, int operation) {
  auto& context = *static_cast<Context*>(JS_GetContextOpaque(ctx)); auto& engine = *context.engine;
  try {
    engine.CheckCancelled();
    // Preprocessing is pure and synchronous: no host calls, timers, context
    // switches, nested scopes or guest re-entry while the larger budget is live.
    Check(!engine.preprocessing, "UNSUPPORTED_OPTION");
    auto* authority = TaskControl(JS_GetHostTaskContext(engine.runtime));
    if ((operation == 0 || operation == 2 || operation == 4 || operation == 5 || operation == 11 ||
         operation == 6 || operation == 7 || operation == 10) && authority && authority->Status())
      throw Fault("STALE_SESSION");
    switch (operation) {
      case 0: return engine.HostCall(context, argc, argv, false);
      case 10: return engine.HostCall(context, argc, argv, true);
      case 1: {
        Check(argc >= 1);
        auto message = std::make_unique<Message>(); message->type = "event";
        message->name = Utf8(QString(ctx, argv[0]));
        if (argc > 1) message->value = FromQuick(ctx, argv[1]);
        auto* control = TaskControl(JS_GetHostTaskContext(engine.runtime)); message->scope = control ? control->scope : 0;
        engine.Send(std::move(message)); return JS_UNDEFINED;
      }
      case 2: {
        Check(argc >= 2);
        return ToQuick(ctx, NativeAlgorithm(Utf8(QString(ctx, argv[0])), ArgsFromQuick(ctx, argv[1]),
          [&engine] { engine.CheckCancelled(); }));
      }
      case 3: {
        if (argc > 1) Check(JS_IsFunction(ctx, argv[1]));
        auto* task = JS_GetHostTaskContext(engine.runtime); auto* control = TaskControl(task);
        if (argc > 0) {
          uint64_t id = FromQuick(ctx, argv[0]).Integer();
          if (!control || control->scope != id) {
            std::shared_ptr<Control> next;
            auto* current = static_cast<Task*>(JS_GetHostTaskOpaque(task));
            {
              std::lock_guard<std::mutex> lock(engine.mutex);
              if (id == 0) next = engine.initialization;
              else {
                auto entry = engine.scopes.find(id);
                if (entry != engine.scopes.end()) next = entry->second.lock();
              }
              if (!next) {
                next = std::make_shared<Control>(); next->scope = id;
                if (current) next->parent = current->control;
                if (id) engine.scopes[id] = next;
              }
              for (auto entry = engine.scopes.begin(); entry != engine.scopes.end();) {
                if (entry->second.expired()) entry = engine.scopes.erase(entry);
                else ++entry;
              }
            }
            auto* replacement = NewTask(engine.runtime, std::move(next));
            JS_SetHostTaskContext(engine.runtime, replacement); JS_FreeHostTaskContext(engine.runtime, replacement);
            control = TaskControl(JS_GetHostTaskContext(engine.runtime));
          }
          auto* execution = id == 0
            ? JS_GetHostTaskContext(engine.runtime) : JS_GetHostExecutionOwner(engine.runtime);
          JS_SetHostExecutionContext(engine.runtime, execution);
          if (argc > 1) {
            // Only a trusted callback boundary selects a new execution owner.
            // Ordinary scope switches above must not replace that owner.
            TaskScope scope(engine.runtime, JS_GetHostTaskContext(engine.runtime));
            auto* previousOwner = JS_DupHostTaskContext(JS_GetHostExecutionOwner(engine.runtime));
            auto* previousExecution = JS_DupHostTaskContext(execution);
            auto* owner = JobExecutionTask(JS_GetHostTaskContext(engine.runtime), previousOwner);
            JS_SetHostExecutionOwner(engine.runtime, owner);
            JS_SetHostExecutionContext(engine.runtime, owner);
            JSValue result = JS_Call(ctx, argv[1], JS_UNDEFINED, 0, nullptr);
            JS_SetHostExecutionOwner(engine.runtime, previousOwner);
            JS_SetHostExecutionContext(engine.runtime, previousExecution);
            JS_FreeHostTaskContext(engine.runtime, previousOwner);
            JS_FreeHostTaskContext(engine.runtime, previousExecution);
            return result;
          }
        }
        return JS_NewFloat64(ctx, control ? control->scope : 0);
      }
      case 4: Check(engine.guest != nullptr, "UNSUPPORTED_OPTION");
        return engine.CrossCall(context, *engine.guest, argc, argv, false);
      case 5: Check(engine.core != nullptr, "UNSUPPORTED_OPTION");
        return engine.CrossCall(context, *engine.core, argc, argv, true);
      case 11: {
        Check(!context.guest && !engine.crossingGuest, "UNSUPPORTED_OPTION");
        Check(argc == 1 && JS_IsFunction(ctx, argv[0]) && !JS_IsAsyncFunction(argv[0]));
        QValue result(ctx, JS_UNDEFINED);
        {
          FlagScope active(engine.preprocessing);
          PreprocessStackScope stack(engine.runtime, engine.maxStackBytes);
          result.value = JS_Call(ctx, argv[0], JS_UNDEFINED, 0, nullptr);
        }
        // Restore before inspecting errors or returning to any caller. Never
        // assimilate a Promise/thenable or run pending jobs inside the scope.
        if (JS_IsException(result.value)) throw Fault(engine.CurrentError(ctx));
        engine.CheckCancelled();
        Check(JS_IsString(result.value));
        return result.Release();
      }
      case 6: case 7: return engine.SetTimer(context, argc, argv, operation == 7);
      case 8: case 9:
        if (argc && JS_IsNumber(argv[0])) {
          auto id = FromQuick(ctx, argv[0]).Integer(); auto entry = engine.timers.find(id);
          if (entry != engine.timers.end() && entry->second.ctx == ctx) {
            engine.ReleaseTimer(entry->second); engine.timers.erase(entry);
          }
        }
        return JS_UNDEFINED;
    }
  } catch (const Fault& error) {
    JSValue value = QError(ctx, error.what());
    return JS_IsException(value) ? value : JS_Throw(ctx, value);
  } catch (...) {
    JSValue value = QError(ctx, "RESOURCE_LIMIT");
    return JS_IsException(value) ? value : JS_Throw(ctx, value);
  }
  JSValue value = QError(ctx, "UNSUPPORTED_OPTION");
  return JS_IsException(value) ? value : JS_Throw(ctx, value);
}
}  // namespace meow
