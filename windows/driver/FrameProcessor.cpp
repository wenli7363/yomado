// Copyright (c) Microsoft Corporation. Adapted under MS-PL; see LICENSE.
#include "FrameProcessor.h"
#include <avrt.h>

using Microsoft::WRL::ComPtr;

FrameProcessor::FrameProcessor(IDDCX_SWAPCHAIN swapChain, LUID adapter, HANDLE available)
    : swapChain_(swapChain), adapter_(adapter), available_(available) {}
bool FrameProcessor::Start() {
    stop_ = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (!stop_) return false;
    thread_ = CreateThread(nullptr, 0, ThreadEntry, this, 0, nullptr);
    return thread_ != nullptr;
}
FrameProcessor::~FrameProcessor() {
    if (stop_) SetEvent(stop_);
    if (thread_) { WaitForSingleObject(thread_, INFINITE); CloseHandle(thread_); }
    if (stop_) CloseHandle(stop_);
    if (swapChain_) WdfObjectDelete(swapChain_);
}
DWORD WINAPI FrameProcessor::ThreadEntry(void* context) {
    static_cast<FrameProcessor*>(context)->Run();
    return 0;
}
void FrameProcessor::Run() {
    DWORD task = 0;
    HANDLE scheduling = AvSetMmThreadCharacteristicsW(L"Distribution", &task);
    ComPtr<IDXGIFactory5> factory;
    ComPtr<IDXGIAdapter1> adapter;
    ComPtr<ID3D11Device> device;
    ComPtr<IDXGIDevice> dxgi;
    HRESULT result = CreateDXGIFactory2(0, IID_PPV_ARGS(&factory));
    if (SUCCEEDED(result)) result = factory->EnumAdapterByLuid(adapter_, IID_PPV_ARGS(&adapter));
    if (SUCCEEDED(result)) result = D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr,
        D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0, D3D11_SDK_VERSION, &device, nullptr, nullptr);
    if (SUCCEEDED(result)) result = device.As(&dxgi);
    IDARG_IN_SWAPCHAINSETDEVICE setDevice{}; setDevice.pDevice = dxgi.Get();
    if (SUCCEEDED(result)) result = IddCxSwapChainSetDevice(swapChain_, &setDevice);
    while (SUCCEEDED(result) && WaitForSingleObject(stop_, 0) != WAIT_OBJECT_0) {
        IDARG_OUT_RELEASEANDACQUIREBUFFER buffer{};
        result = IddCxSwapChainReleaseAndAcquireBuffer(swapChain_, &buffer);
        if (result == E_PENDING) {
            HANDLE events[] = { stop_, available_ };
            const DWORD wait = WaitForMultipleObjects(2, events, FALSE, 100);
            if (wait == WAIT_OBJECT_0 || wait == WAIT_FAILED) break;
            result = S_OK;
            continue;
        }
        if (FAILED(result)) break;
        // Yomado provides a display surface. The agent's existing capture tool
        // reads that surface; this driver doesn't stream or inspect its contents.
        ComPtr<IDXGIResource> surface;
        surface.Attach(buffer.MetaData.pSurface);
        surface.Reset();
        result = IddCxSwapChainFinishedProcessingFrame(swapChain_);
    }
    if (scheduling) AvRevertMmThreadCharacteristics(scheduling);
    // Deleting an abandoned swapchain allows IddCx to retry after GPU loss.
    WdfObjectDelete(swapChain_);
    swapChain_ = nullptr;
}
