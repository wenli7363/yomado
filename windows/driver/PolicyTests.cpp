#include "DisplayPolicy.h"
#include <cstdio>

int main() {
    unsigned cases = 0;
    const unsigned modes[][2] = {{1920,1080},{2560,1440},{3840,2160},{5120,2880}};
    for (unsigned slot = 0; slot < yomado::kMaxMonitors; ++slot) for (const auto& mode : modes) {
        auto edid = yomado::MakeEdid(slot, mode[0], mode[1]); unsigned width = 0, height = 0;
        if (!yomado::ParseEdid(edid.data(), edid.size(), width, height) || width != mode[0] || height != mode[1] || edid[12] != slot + 1) return 1;
        edid[50] ^= 1;
        if (yomado::ParseEdid(edid.data(), edid.size(), width, height)) return 2;
        ++cases;
    }
    if (yomado::ValidMode(65535,65535) || yomado::ValidMode(1920,1440)) return 3;
    std::printf("%u EDID/mode cases passed; malformed EDIDs and unsupported modes rejected\n", cases);
    return 0;
}
