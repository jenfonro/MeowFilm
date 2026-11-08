#include "engine.h"
#include <array>
#ifndef _WIN32
#include <cerrno>
#include <climits>
#include <fcntl.h>
#include <unistd.h>
#endif
#ifdef MEOW_IOS_FRAMEWORK
#include "binding-identity.h"
#endif

namespace meow {
using Owner = std::shared_ptr<Engine>;
void Engine::PrepareJoin() {
  napi_value name;
  NCheck(napi_create_string_utf8(env, "script-host-native-exit", NAPI_AUTO_LENGTH, &name));
  napi_status status = napi_create_async_work(env, nullptr, name,
    [](napi_env, void* data) {
      auto& engine = *static_cast<Engine*>(data);
      if (engine.thread.joinable()) engine.thread.join();
      engine.snapshot.threadCount = 0;
      engine.snapshot.stopped.store(true, std::memory_order_release);
    },
    [](napi_env env, napi_status, void* data) {
      auto& engine = *static_cast<Engine*>(data);
      auto owner = std::move(engine.joinOwner);
      if (!engine.environmentClosing) {
        auto message = std::make_unique<Message>();
        message->type = "stopped"; message->code = engine.stopReason;
        message->value = engine.snapshot.Read();
        SendOwned(engine.completionMessages, std::move(message));
      }
      ReleaseSender(engine.completionMessages,
        engine.environmentClosing ? napi_tsfn_abort : napi_tsfn_release);
      if (engine.cleanupHook) {
        napi_remove_async_cleanup_hook(engine.cleanupHook); engine.cleanupHook = nullptr;
      }
      napi_delete_async_work(env, engine.joinWork); engine.joinWork = nullptr;
    }, this, &joinWork);
  NCheck(status);
}
void Engine::JoinAsync() {
  if (joining) return;
  // Work was created while the environment was live. Cleanup never creates V8 values.
  joinOwner = shared_from_this();
  napi_status status = napi_queue_async_work(env, joinWork);
  if (status != napi_ok) { joinOwner.reset(); NCheck(status); }
  joining = true;
}
void Engine::CallNode(napi_env env, napi_value callback, void* context, void* data) {
  std::unique_ptr<Message> message(static_cast<Message*>(data));
  auto& owner = *static_cast<Owner*>(context);
  if (!env || !callback) return;  // TSFN abort still destroys every owned message.
  try {
    if (message->type == "join") { owner->JoinAsync(); return; }
    Value input = Value::Obj();
    input.Set("type", Value::Str(message->type)); input.Set("id", Value::Num(message->id));
    input.Set("scope", Value::Num(message->scope)); input.Set("ok", Value::Bool(message->ok));
    input.Set("name", Value::Str(message->name)); input.Set("code", Value::Str(message->code));
    input.Set("value", std::move(message->value));
    napi_value value = ToNode(env, input), undefined, ignored;
    NCheck(napi_get_undefined(env, &undefined));
    NCheck(napi_call_function(env, undefined, callback, 1, &value, &ignored));
  } catch (...) {
    // An environment exception cannot leave the owner executing without a consumer.
    owner->Stop("SCRIPT_EXCEPTION");
    bool pending = false; napi_is_exception_pending(env, &pending);
    if (pending) { napi_value error; napi_get_and_clear_last_exception(env, &error); }
  }
}
struct Invocation {
  napi_env env;
  std::array<napi_value, 5> args{};
  size_t count = args.size();
  napi_value self;
  Engine* engine;
  Invocation(napi_env e, napi_callback_info info) : env(e) {
    NCheck(napi_get_cb_info(env, info, &count, args.data(), &self, nullptr));
    Owner* owner; NCheck(napi_unwrap(env, self, reinterpret_cast<void**>(&owner))); engine = owner->get();
  }
  Value Arg(size_t i) { Check(i < count); return FromNode(env, args[i]); }
  Value Args(size_t i) { Check(i < count); return ArgsFromNode(env, args[i]); }
};
template<typename F> napi_value Protect(napi_env env, F fn) {
  try { return fn(); }
  catch (const std::exception& error) {
    const char* code = dynamic_cast<const Fault*>(&error) ? error.what() : "RESOURCE_LIMIT";
    bool pending = false; napi_is_exception_pending(env, &pending);
    if (!pending) napi_throw_error(env, code, code);
    return nullptr;
  } catch (...) { napi_throw_error(env, "SCRIPT_EXCEPTION", "SCRIPT_EXCEPTION"); return nullptr; }
}
static napi_value Undefined(napi_env env) { napi_value out; NCheck(napi_get_undefined(env, &out)); return out; }
static napi_value Invoke(napi_env env, napi_callback_info info) {
  return Protect(env, [&] {
    Invocation call(env, info); Command command;
    command.id = call.Arg(0).Integer(); Check(command.id > 0);
    command.name = call.Arg(1).Text(); command.value = call.Args(2);
    command.control = std::make_shared<Control>(); command.control->scope = call.Arg(3).Integer();
    auto timeout = call.Arg(4).Integer();
    if (timeout) command.control->deadline = Now() + static_cast<int64_t>(timeout);
    call.engine->Admit(std::move(command)); return Undefined(env);
  });
}
static napi_value Complete(napi_env env, napi_callback_info info) {
  return Protect(env, [&] {
    Invocation call(env, info); Command command; command.kind = Command::Complete;
    command.id = call.Arg(0).Integer(); Value ok = call.Arg(1); Check(ok.kind == Value::Boolean);
    command.ok = ok.boolean; command.value = call.Arg(2);
    call.engine->Admit(std::move(command)); return Undefined(env);
  });
}
static napi_value Cancel(napi_env env, napi_callback_info info) {
  return Protect(env, [&] {
    Invocation call(env, info); call.engine->Cancel(call.Arg(0).Integer(), call.Arg(1).Text()); return Undefined(env);
  });
}
static napi_value Stop(napi_env env, napi_callback_info info) {
  return Protect(env, [&] {
    Invocation call(env, info); call.engine->Stop(call.Arg(0).Text()); return Undefined(env);
  });
}
static napi_value Stats(napi_env env, napi_callback_info info) {
  return Protect(env, [&] { Invocation call(env, info); return ToNode(env, call.engine->snapshot.Read()); });
}
static napi_value RefreshStats(napi_env env, napi_callback_info info) {
  return Protect(env, [&] {
    Invocation call(env, info); Command command; command.kind = Command::Refresh;
    command.id = call.Arg(0).Integer(); Value gc = call.Arg(1); Check(gc.kind == Value::Boolean); command.gc = gc.boolean;
    call.engine->Admit(std::move(command)); return Undefined(env);
  });
}
static napi_value Create(napi_env env, napi_callback_info info) {
  return Protect(env, [&] {
    size_t argc = 2; napi_value argv[2]; NCheck(napi_get_cb_info(env, info, &argc, argv, nullptr, nullptr));
    Check(argc == 2);
    napi_valuetype type; NCheck(napi_typeof(env, argv[1], &type)); Check(type == napi_function);
    auto owner = std::make_shared<Engine>(FromNode(env, argv[0])); owner->env = env;
    napi_value object; NCheck(napi_create_object(env, &object));
    napi_property_descriptor methods[] = {
      {"invoke", nullptr, Invoke, nullptr, nullptr, nullptr, napi_default, nullptr},
      {"complete", nullptr, Complete, nullptr, nullptr, nullptr, napi_default, nullptr},
      {"cancel", nullptr, Cancel, nullptr, nullptr, nullptr, napi_default, nullptr},
      {"stop", nullptr, Stop, nullptr, nullptr, nullptr, napi_default, nullptr},
      {"stats", nullptr, Stats, nullptr, nullptr, nullptr, napi_default, nullptr},
      {"refreshStats", nullptr, RefreshStats, nullptr, nullptr, nullptr, napi_default, nullptr},
    };
    NCheck(napi_define_properties(env, object, sizeof(methods) / sizeof(methods[0]), methods));
    auto* handle = new Owner(owner);
    napi_status status = napi_wrap(env, object, handle, [](napi_env, void* data, void*) {
      std::unique_ptr<Owner> owner(static_cast<Owner*>(data)); (*owner)->Stop("STALE_SESSION");
    }, nullptr, nullptr);
    if (status != napi_ok) { delete handle; NCheck(status); }
    napi_value name; NCheck(napi_create_string_utf8(env, "script-host-native", NAPI_AUTO_LENGTH, &name));
    auto* retained = new Owner(owner);
    status = napi_create_threadsafe_function(env, argv[1], nullptr, name, 0, 2, retained,
      [](napi_env, void* data, void*) { delete static_cast<Owner*>(data); },
      retained, Engine::CallNode, &owner->producerMessages);
    if (status != napi_ok) { delete retained; NCheck(status); }
    owner->completionMessages = owner->producerMessages;
    try {
      owner->PrepareJoin();
      NCheck(napi_add_async_cleanup_hook(env, [](napi_async_cleanup_hook_handle, void* data) {
        auto& engine = **static_cast<Owner*>(data);
        engine.environmentClosing = true; engine.Stop("STALE_SESSION");
        engine.JoinAsync();
      }, retained, &owner->cleanupHook));
      owner->Start();
    } catch (...) {
      if (owner->cleanupHook) { napi_remove_async_cleanup_hook(owner->cleanupHook); owner->cleanupHook = nullptr; }
      Engine::ReleaseSender(owner->producerMessages, napi_tsfn_abort);
      Engine::ReleaseSender(owner->completionMessages, napi_tsfn_release);
      if (owner->joinWork) { napi_delete_async_work(env, owner->joinWork); owner->joinWork = nullptr; }
      throw;
    }
    return object;
  });
}
#ifndef _WIN32
static napi_value DuplicateBootstrapDescriptor(napi_env env, napi_callback_info info) {
  return Protect(env, [&] {
    size_t argc = 2; napi_value argv[2];
    NCheck(napi_get_cb_info(env, info, &argc, argv, nullptr, nullptr));
    Check(argc == 1);
    double descriptor; NCheck(napi_get_value_double(env, argv[0], &descriptor));
    Check(std::isfinite(descriptor) && descriptor >= 3 && descriptor <= INT_MAX &&
          std::floor(descriptor) == descriptor);
    int copy;
    do { copy = fcntl(static_cast<int>(descriptor), F_DUPFD_CLOEXEC, 3); }
    while (copy < 0 && errno == EINTR);
    Check(copy >= 0, "DESCRIPTOR_UNAVAILABLE");
    napi_value result;
    napi_status status = napi_create_int32(env, copy, &result);
    if (status != napi_ok) { close(copy); NCheck(status); }
    // Only the returned copy changes owner. The launch thread retains the input.
    return result;
  });
}
#endif
static napi_value Initialize(napi_env env, napi_value exports) {
  return Protect(env, [&] {
    napi_value create, abi, napi;
    NCheck(napi_create_function(env, "create", NAPI_AUTO_LENGTH, Create, nullptr, &create));
    NCheck(napi_set_named_property(env, exports, "create", create));
    NCheck(napi_create_uint32(env, 1, &abi)); NCheck(napi_set_named_property(env, exports, "abi", abi));
    NCheck(napi_create_uint32(env, 8, &napi)); NCheck(napi_set_named_property(env, exports, "napi", napi));
#ifndef _WIN32
    napi_value duplicate;
    NCheck(napi_create_function(env, "duplicateBootstrapDescriptor", NAPI_AUTO_LENGTH,
      DuplicateBootstrapDescriptor, nullptr, &duplicate));
    NCheck(napi_set_named_property(env, exports, "duplicateBootstrapDescriptor", duplicate));
#endif
#ifdef MEOW_IOS_FRAMEWORK
    napi_value identity = ToNode(env, Value::Str(MEOW_BINDING_IDENTITY));
    NCheck(napi_set_named_property(env, exports, "bindingIdentity", identity));
    napi_value sdk = ToNode(env, Value::Str(MEOW_TARGET_SDK));
    NCheck(napi_set_named_property(env, exports, "sdk", sdk));
    napi_value arch = ToNode(env, Value::Str(MEOW_TARGET_ARCH));
    NCheck(napi_set_named_property(env, exports, "arch", arch));
    napi_value minimumOS = ToNode(env, Value::Str(MEOW_MINIMUM_OS));
    NCheck(napi_set_named_property(env, exports, "minimumOS", minimumOS));
#endif
    return exports;
  });
}
}  // namespace meow
NAPI_MODULE(NODE_GYP_MODULE_NAME, meow::Initialize)
