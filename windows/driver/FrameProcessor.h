// Portions adapted from Microsoft's IndirectDisplay sample; see LICENSE and upstream/REVISION.
#pragma once
#define NOMINMAX
#define WIN32_NO_STATUS
#include <windows.h>
#undef WIN32_NO_STATUS
#include <ntstatus.h>
#pragma warning(push)
#pragma warning(disable: 4324)
#include <wudfwdm.h>
#include <wdf.h>
#include <iddcx.h>
#pragma warning(pop)
#include <d3d11.h>
#include <dxgi1_5.h>
#include <wrl.h>
#include <memory>

class FrameProcessor {
public:
    FrameProcessor(IDDCX_SWAPCHAIN swapChain, LUID adapter, HANDLE available);
    ~FrameProcessor();
    bool Start();
private:
    static DWORD WINAPI ThreadEntry(void* context);
    void Run();
    IDDCX_SWAPCHAIN swapChain_;
    LUID adapter_;
    HANDLE available_, stop_ = nullptr, thread_ = nullptr;
};
