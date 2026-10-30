// RynthCore: ImGuiColorTextEdit (santaclose fork) includes <boost/regex.hpp>.
// Its regex path only runs for languages without a hand-written tokenizer, and
// the two we use (Meta, Lua) both have one, so std::regex stands in for Boost.
#pragma once
#include <regex>

namespace boost
{
    using std::regex;
    using std::cmatch;
    using std::regex_search;
    namespace regex_constants = std::regex_constants;
}
