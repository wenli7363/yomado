#pragma once
#include <cstdint>

namespace yomado {
constexpr std::uint32_t kProtocolVersion = 1;
constexpr std::uint32_t kMaxMonitors = 16;
constexpr std::uint32_t kQuery = 0x0022E004;
constexpr std::uint32_t kCreate = 0x0022E008;
constexpr std::uint32_t kRemove = 0x0022E00C;
struct Request { std::uint32_t Version, Slot, Width, Height; };
struct SlotState { std::uint32_t Slot, Connected, Width, Height; };
struct State { std::uint32_t Version, Ready, Count; SlotState Slots[kMaxMonitors]; };
static_assert(sizeof(Request) == 16 && sizeof(State) == 268);
}
