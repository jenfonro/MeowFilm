#pragma once
#include "value.h"
#include <functional>

namespace meow {
Value NativeAlgorithm(const std::string& operation, const Value& args,
                      const std::function<void()>& checkCancelled);
}
