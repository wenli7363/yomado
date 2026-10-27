// This utility registers only Yomado's root device. Run after installing a signed INF.
#define NOMINMAX
#include <windows.h>
#include <setupapi.h>
#include <devguid.h>
#include <cstdio>
#include <cwchar>
#include <string>

int wmain(int argc, wchar_t** argv) {
    if (argc != 2 || (wcscmp(argv[1], L"--install") != 0 && wcscmp(argv[1], L"--remove") != 0)) {
        fwprintf(stderr, L"Usage: YomadoDevice.exe --install | --remove\n"); return 2;
    }
    SID_IDENTIFIER_AUTHORITY authority = SECURITY_NT_AUTHORITY; PSID administrators = nullptr; BOOL admin = FALSE;
    if (AllocateAndInitializeSid(&authority, 2, SECURITY_BUILTIN_DOMAIN_RID, DOMAIN_ALIAS_RID_ADMINS,
        0, 0, 0, 0, 0, 0, &administrators)) { CheckTokenMembership(nullptr, administrators, &admin); FreeSid(administrators); }
    if (!admin) { fwprintf(stderr, L"Run this utility as administrator.\n"); return 5; }
    const bool remove = wcscmp(argv[1], L"--remove") == 0;
    HDEVINFO devices = SetupDiGetClassDevsW(&GUID_DEVCLASS_DISPLAY, nullptr, nullptr, DIGCF_PRESENT);
    if (devices == INVALID_HANDLE_VALUE) return static_cast<int>(GetLastError());
    SP_DEVINFO_DATA data{}; data.cbSize = sizeof(data);
    bool found = false;
    for (DWORD index = 0; SetupDiEnumDeviceInfo(devices, index, &data); ++index) {
        wchar_t hardware[512]{}; DWORD type = 0;
        if (!SetupDiGetDeviceRegistryPropertyW(devices, &data, SPDRP_HARDWAREID, &type,
            reinterpret_cast<PBYTE>(hardware), sizeof(hardware), nullptr) || type != REG_MULTI_SZ) continue;
        for (const wchar_t* id = hardware; *id; id += wcslen(id) + 1) {
            if (_wcsicmp(id, L"Root\\YomadoVirtualDisplay") != 0) continue;
            found = true;
            if (remove && !SetupDiCallClassInstaller(DIF_REMOVE, devices, &data)) {
                const auto error = GetLastError(); SetupDiDestroyDeviceInfoList(devices); return static_cast<int>(error);
            }
            break;
        }
        if (found) break;
    }
    SetupDiDestroyDeviceInfoList(devices);
    if (remove || found) return 0;
    devices = SetupDiCreateDeviceInfoList(&GUID_DEVCLASS_DISPLAY, nullptr);
    if (devices == INVALID_HANDLE_VALUE) return static_cast<int>(GetLastError());
    data = {}; data.cbSize = sizeof(data);
    const wchar_t ids[] = L"Root\\YomadoVirtualDisplay\0";
    BOOL success = SetupDiCreateDeviceInfoW(devices, L"YomadoVirtualDisplay", &GUID_DEVCLASS_DISPLAY,
        L"Yomado Virtual Display Adapter", nullptr, DICD_GENERATE_ID, &data);
    if (success) success = SetupDiSetDeviceRegistryPropertyW(devices, &data, SPDRP_HARDWAREID,
        reinterpret_cast<const BYTE*>(ids), sizeof(ids));
    if (success) success = SetupDiCallClassInstaller(DIF_REGISTERDEVICE, devices, &data);
    const auto error = success ? 0 : GetLastError(); SetupDiDestroyDeviceInfoList(devices);
    return static_cast<int>(error);
}
