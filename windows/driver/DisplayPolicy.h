#pragma once
#include "Protocol.h"
#include <array>
#include <cstdio>
#include <cstring>

namespace yomado {
inline bool ValidMode(std::uint32_t width, std::uint32_t height) {
    return (width == 1920 && height == 1080) || (width == 2560 && height == 1440) ||
           (width == 3840 && height == 2160) || (width == 5120 && height == 2880);
}

// A separate, stable EDID identity for every connector. Mode text is parsed by
// our IddCx callback, including 5120px widths beyond the base EDID DTD limit.
inline std::array<unsigned char, 128> MakeEdid(std::uint32_t slot, std::uint32_t width, std::uint32_t height) {
    std::array<unsigned char, 128> edid{};
    const unsigned char header[] = {0, 255, 255, 255, 255, 255, 255, 0};
    std::memcpy(edid.data(), header, sizeof(header));
    edid[8] = 0x65; edid[9] = 0xA4; // YMD
    edid[10] = 0x4D; edid[11] = 0x59; // product 0x594D
    edid[12] = static_cast<unsigned char>(slot + 1);
    edid[16] = 1; edid[17] = 36; edid[18] = 1; edid[19] = 4;
    edid[20] = 0x80;
    const bool hiDpi = width > 2560;
    edid[21] = static_cast<unsigned char>((width / (hiDpi ? 2 : 1)) * 254 / 9600);
    edid[22] = static_cast<unsigned char>((height / (hiDpi ? 2 : 1)) * 254 / 9600);
    edid[23] = 120; edid[24] = 0x0A;
    for (int index = 38; index < 54; index += 2) { edid[index] = 1; edid[index + 1] = 1; }
    auto text = [&edid](int offset, unsigned char kind, const char* value) {
        edid[offset + 3] = kind;
        std::memset(edid.data() + offset + 5, ' ', 13);
        auto length = std::strlen(value);
        if (length > 12) length = 12;
        std::memcpy(edid.data() + offset + 5, value, length);
        edid[offset + 5 + length] = '\n';
    };
    char name[24]{}, mode[24]{}, serial[24]{};
    std::snprintf(name, sizeof(name), "Yomado %02u", slot + 1);
    std::snprintf(mode, sizeof(mode), "%ux%u", width, height);
    std::snprintf(serial, sizeof(serial), "YM%08u", slot + 1);
    text(54, 0xFC, name); text(72, 0xFE, mode); text(90, 0xFF, serial);
    edid[111] = 0xFD; edid[113] = 59; edid[114] = 61; edid[115] = 30; edid[116] = 255;
    edid[117] = 100; edid[119] = 0x0A;
    unsigned sum = 0; for (auto value : edid) sum += value;
    edid[127] = static_cast<unsigned char>(0 - sum);
    return edid;
}

inline bool ParseEdid(const unsigned char* data, std::size_t size, std::uint32_t& width, std::uint32_t& height) {
    if (!data || size != 128 || data[8] != 0x65 || data[9] != 0xA4 || data[10] != 0x4D || data[11] != 0x59 ||
        data[75] != 0xFE || data[12] == 0 || data[12] > kMaxMonitors) return false;
    unsigned checksum = 0; for (std::size_t index = 0; index < size; ++index) checksum += data[index];
    if ((checksum & 255) != 0) return false;
    char text[14]{}; std::memcpy(text, data + 77, 13);
    unsigned w = 0, h = 0;
    if (std::sscanf(text, "%ux%u", &w, &h) != 2 || !ValidMode(w, h)) return false;
    width = w; height = h;
    return true;
}
}
