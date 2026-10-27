// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Yomado contributors. MS-PL; see LICENSE.
// IddCx initialization follows Microsoft's IndirectDisplay sample.
#include "FrameProcessor.h"
#include "DisplayPolicy.h"
#include <array>
#include <mutex>
#include <new>

using namespace yomado;
static const GUID kInterface = {0xbcb78c7b, 0x40c0, 0x47cc, {0x8e, 0xa7, 0x31, 0xa7, 0xd5, 0x22, 0x47, 0x26}};

struct MonitorContext {
    std::uint32_t Width, Height;
    std::unique_ptr<FrameProcessor> Frames;
};
struct DeviceContext {
    WDFDEVICE Device{};
    IDDCX_ADAPTER Adapter{};
    bool Ready = false;
    WDFFILEOBJECT Owner{};
    std::array<IDDCX_MONITOR, kMaxMonitors> Monitors{};
    std::array<SlotState, kMaxMonitors> States{};
    std::mutex Lock;
    NTSTATUS Create(const Request& request);
    NTSTATUS Remove(std::uint32_t slot);
};
struct DeviceWrapper { DeviceContext* Context = nullptr; };
struct MonitorWrapper { MonitorContext* Context = nullptr; };
WDF_DECLARE_CONTEXT_TYPE(DeviceWrapper);
WDF_DECLARE_CONTEXT_TYPE(MonitorWrapper);

EVT_WDF_DRIVER_DEVICE_ADD DeviceAdd;
EVT_WDF_DEVICE_D0_ENTRY D0Entry;
EVT_WDF_FILE_CLEANUP FileCleanup;
EVT_IDD_CX_DEVICE_IO_CONTROL IoControl;
EVT_IDD_CX_ADAPTER_INIT_FINISHED AdapterReady;
EVT_IDD_CX_ADAPTER_COMMIT_MODES CommitModes;
EVT_IDD_CX_PARSE_MONITOR_DESCRIPTION ParseDescription;
EVT_IDD_CX_MONITOR_GET_DEFAULT_DESCRIPTION_MODES DefaultModes;
EVT_IDD_CX_MONITOR_QUERY_TARGET_MODES TargetModes;
EVT_IDD_CX_MONITOR_ASSIGN_SWAPCHAIN AssignSwapChain;
EVT_IDD_CX_MONITOR_UNASSIGN_SWAPCHAIN UnassignSwapChain;

extern "C" DRIVER_INITIALIZE DriverEntry;
extern "C" BOOL WINAPI DllMain(HINSTANCE, DWORD, LPVOID) { return TRUE; }
_Use_decl_annotations_
extern "C" NTSTATUS DriverEntry(PDRIVER_OBJECT object, PUNICODE_STRING path) {
    WDF_DRIVER_CONFIG config;
    WDF_DRIVER_CONFIG_INIT(&config, DeviceAdd);
    return WdfDriverCreate(object, path, WDF_NO_OBJECT_ATTRIBUTES, &config, WDF_NO_HANDLE);
}

_Use_decl_annotations_
NTSTATUS DeviceAdd(WDFDRIVER, PWDFDEVICE_INIT init) {
    WDF_PNPPOWER_EVENT_CALLBACKS power;
    WDF_PNPPOWER_EVENT_CALLBACKS_INIT(&power); power.EvtDeviceD0Entry = D0Entry;
    WdfDeviceInitSetPnpPowerEventCallbacks(init, &power);
    WDF_FILEOBJECT_CONFIG files;
    WDF_FILEOBJECT_CONFIG_INIT(&files, nullptr, nullptr, FileCleanup);
    WdfDeviceInitSetFileObjectConfig(init, &files, WDF_NO_OBJECT_ATTRIBUTES);
    IDD_CX_CLIENT_CONFIG config;
    IDD_CX_CLIENT_CONFIG_INIT(&config);
    config.EvtIddCxDeviceIoControl = IoControl;
    config.EvtIddCxAdapterInitFinished = AdapterReady;
    config.EvtIddCxAdapterCommitModes = CommitModes;
    config.EvtIddCxParseMonitorDescription = ParseDescription;
    config.EvtIddCxMonitorGetDefaultDescriptionModes = DefaultModes;
    config.EvtIddCxMonitorQueryTargetModes = TargetModes;
    config.EvtIddCxMonitorAssignSwapChain = AssignSwapChain;
    config.EvtIddCxMonitorUnassignSwapChain = UnassignSwapChain;
    NTSTATUS status = IddCxDeviceInitConfig(init, &config);
    if (!NT_SUCCESS(status)) return status;
    WDF_OBJECT_ATTRIBUTES attributes;
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, DeviceWrapper);
    attributes.EvtCleanupCallback = [](WDFOBJECT object) {
        auto wrapper = WdfObjectGet_DeviceWrapper(object);
        delete wrapper->Context; wrapper->Context = nullptr;
    };
    WDFDEVICE device;
    status = WdfDeviceCreate(&init, &attributes, &device);
    if (!NT_SUCCESS(status)) return status;
    auto context = new (std::nothrow) DeviceContext;
    if (!context) return STATUS_INSUFFICIENT_RESOURCES;
    context->Device = device; WdfObjectGet_DeviceWrapper(device)->Context = context;
    status = WdfDeviceCreateDeviceInterface(device, &kInterface, nullptr);
    if (!NT_SUCCESS(status)) return status;
    return IddCxDeviceInitialize(device);
}

_Use_decl_annotations_
NTSTATUS D0Entry(WDFDEVICE device, WDF_POWER_DEVICE_STATE) {
    auto context = WdfObjectGet_DeviceWrapper(device)->Context;
    if (context->Adapter) return STATUS_SUCCESS;
    IDDCX_ADAPTER_CAPS caps{}; caps.Size = sizeof(caps); caps.MaxMonitorsSupported = kMaxMonitors;
    caps.EndPointDiagnostics.Size = sizeof(caps.EndPointDiagnostics);
    caps.EndPointDiagnostics.GammaSupport = IDDCX_FEATURE_IMPLEMENTATION_NONE;
    caps.EndPointDiagnostics.TransmissionType = IDDCX_TRANSMISSION_TYPE_WIRED_OTHER;
    caps.EndPointDiagnostics.pEndPointFriendlyName = L"Yomado Virtual Display";
    caps.EndPointDiagnostics.pEndPointManufacturerName = L"Yomado contributors";
    caps.EndPointDiagnostics.pEndPointModelName = L"Yomado Windows";
    IDDCX_ENDPOINT_VERSION version{}; version.Size = sizeof(version); version.MajorVer = 1;
    caps.EndPointDiagnostics.pHardwareVersion = &version; caps.EndPointDiagnostics.pFirmwareVersion = &version;
    WDF_OBJECT_ATTRIBUTES attributes;
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, DeviceWrapper);
    IDARG_IN_ADAPTER_INIT input{}; input.WdfDevice = device; input.pCaps = &caps; input.ObjectAttributes = &attributes;
    IDARG_OUT_ADAPTER_INIT output{};
    const auto status = IddCxAdapterInitAsync(&input, &output);
    if (NT_SUCCESS(status)) { context->Adapter = output.AdapterObject; WdfObjectGet_DeviceWrapper(output.AdapterObject)->Context = context; }
    return status;
}

_Use_decl_annotations_
NTSTATUS AdapterReady(IDDCX_ADAPTER adapter, const IDARG_IN_ADAPTER_INIT_FINISHED* input) {
    auto context = WdfObjectGet_DeviceWrapper(adapter)->Context;
    std::lock_guard<std::mutex> guard(context->Lock);
    context->Ready = NT_SUCCESS(input->AdapterInitStatus);
    return STATUS_SUCCESS; // no monitor arrives until an explicit application request
}

NTSTATUS DeviceContext::Create(const Request& request) {
    if (!Ready) return STATUS_DEVICE_NOT_READY;
    if (Monitors[request.Slot]) return STATUS_OBJECT_NAME_COLLISION;
    const auto edid = MakeEdid(request.Slot, request.Width, request.Height);
    WDF_OBJECT_ATTRIBUTES attributes;
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, MonitorWrapper);
    attributes.EvtCleanupCallback = [](WDFOBJECT object) {
        auto wrapper = WdfObjectGet_MonitorWrapper(object);
        delete wrapper->Context; wrapper->Context = nullptr;
    };
    IDDCX_MONITOR_INFO info{}; info.Size = sizeof(info);
    info.MonitorType = DISPLAYCONFIG_OUTPUT_TECHNOLOGY_HDMI; info.ConnectorIndex = request.Slot;
    info.MonitorDescription.Size = sizeof(info.MonitorDescription);
    info.MonitorDescription.Type = IDDCX_MONITOR_DESCRIPTION_TYPE_EDID;
    info.MonitorDescription.DataSize = static_cast<UINT>(edid.size());
    info.MonitorDescription.pData = const_cast<unsigned char*>(edid.data());
    info.MonitorContainerId = kInterface; info.MonitorContainerId.Data1 ^= request.Slot + 1;
    IDARG_IN_MONITORCREATE input{}; input.ObjectAttributes = &attributes; input.pMonitorInfo = &info;
    IDARG_OUT_MONITORCREATE output{};
    auto status = IddCxMonitorCreate(Adapter, &input, &output);
    if (!NT_SUCCESS(status)) return status;
    auto monitor = new (std::nothrow) MonitorContext{request.Width, request.Height, {}};
    if (!monitor) { WdfObjectDelete(output.MonitorObject); return STATUS_INSUFFICIENT_RESOURCES; }
    WdfObjectGet_MonitorWrapper(output.MonitorObject)->Context = monitor;
    IDARG_OUT_MONITORARRIVAL arrival{};
    status = IddCxMonitorArrival(output.MonitorObject, &arrival);
    if (!NT_SUCCESS(status)) { WdfObjectDelete(output.MonitorObject); return status; }
    Monitors[request.Slot] = output.MonitorObject;
    States[request.Slot] = { request.Slot, 1, request.Width, request.Height };
    return STATUS_SUCCESS;
}
NTSTATUS DeviceContext::Remove(std::uint32_t slot) {
    if (!Monitors[slot]) return STATUS_SUCCESS;
    auto status = IddCxMonitorDeparture(Monitors[slot]);
    if (NT_SUCCESS(status)) { Monitors[slot] = nullptr; States[slot] = {slot, 0, 0, 0}; }
    return status;
}

_Use_decl_annotations_
void IoControl(WDFDEVICE device, WDFREQUEST request, size_t outputLength, size_t inputLength, ULONG code) {
    if (inputLength != sizeof(Request) || (code != kQuery && code != kCreate && code != kRemove)) {
        WdfRequestComplete(request, STATUS_INVALID_PARAMETER); return;
    }
    Request* input = nullptr; void* output = nullptr;
    auto status = WdfRequestRetrieveInputBuffer(request, sizeof(Request), reinterpret_cast<void**>(&input), nullptr);
    const size_t required = code == kQuery ? sizeof(State) : sizeof(std::uint32_t);
    if (NT_SUCCESS(status) && (input->Version != kProtocolVersion || input->Slot >= kMaxMonitors ||
        (code == kCreate && !ValidMode(input->Width, input->Height)))) status = STATUS_INVALID_PARAMETER;
    if (NT_SUCCESS(status) && outputLength < required) status = STATUS_BUFFER_TOO_SMALL;
    if (NT_SUCCESS(status)) status = WdfRequestRetrieveOutputBuffer(request, required, &output, nullptr);
    if (!NT_SUCCESS(status)) { WdfRequestComplete(request, status); return; }
    // METHOD_BUFFERED input and output can share storage; copy before writing output.
    const Request command = *input;
    auto context = WdfObjectGet_DeviceWrapper(device)->Context;
    {
        std::lock_guard<std::mutex> guard(context->Lock);
        const auto file = WdfRequestGetFileObject(request);
        if (code == kQuery) {
            auto state = static_cast<State*>(output);
            *state = {}; state->Version = kProtocolVersion; state->Ready = context->Ready ? 1u : 0u; state->Count = kMaxMonitors;
            for (unsigned index = 0; index < kMaxMonitors; ++index) { state->Slots[index] = context->States[index]; state->Slots[index].Slot = index; }
        } else if (!file || (context->Owner && context->Owner != file)) status = STATUS_ACCESS_DENIED;
        else {
            if (code == kCreate) { context->Owner = file; status = context->Create(command); }
            else status = context->Remove(command.Slot);
            if (NT_SUCCESS(status)) *static_cast<std::uint32_t*>(output) = kProtocolVersion;
        }
    }
    WdfRequestCompleteWithInformation(request, status, NT_SUCCESS(status) ? required : 0);
}

_Use_decl_annotations_
void FileCleanup(WDFFILEOBJECT file) {
    auto context = WdfObjectGet_DeviceWrapper(WdfFileObjectGetDevice(file))->Context;
    if (!context) return;
    std::lock_guard<std::mutex> guard(context->Lock);
    if (context->Owner != file) return;
    for (unsigned index = 0; index < kMaxMonitors; ++index) context->Remove(index);
    context->Owner = nullptr;
}

static void Signal(DISPLAYCONFIG_VIDEO_SIGNAL_INFO& signal, unsigned width, unsigned height, bool monitor) {
    signal = {}; signal.totalSize.cx = signal.activeSize.cx = width; signal.totalSize.cy = signal.activeSize.cy = height;
    signal.AdditionalSignalInfo.videoStandard = 255; signal.AdditionalSignalInfo.vSyncFreqDivider = monitor ? 0u : 1u;
    signal.vSyncFreq.Numerator = 60; signal.vSyncFreq.Denominator = 1;
    signal.hSyncFreq.Numerator = 60 * height; signal.hSyncFreq.Denominator = 1;
    signal.pixelRate = static_cast<UINT64>(width) * height * 60;
    signal.scanLineOrdering = DISPLAYCONFIG_SCANLINE_ORDERING_PROGRESSIVE;
}
static IDDCX_MONITOR_MODE MonitorMode(unsigned width, unsigned height, IDDCX_MONITOR_MODE_ORIGIN origin) {
    IDDCX_MONITOR_MODE mode{}; mode.Size = sizeof(mode); mode.Origin = origin;
    Signal(mode.MonitorVideoSignalInfo, width, height, true); return mode;
}
_Use_decl_annotations_
NTSTATUS ParseDescription(const IDARG_IN_PARSEMONITORDESCRIPTION* input, IDARG_OUT_PARSEMONITORDESCRIPTION* output) {
    unsigned width = 0, height = 0;
    if (!ParseEdid(static_cast<const unsigned char*>(input->MonitorDescription.pData), input->MonitorDescription.DataSize, width, height)) return STATUS_INVALID_PARAMETER;
    output->MonitorModeBufferOutputCount = 1; output->PreferredMonitorModeIdx = 0;
    if (input->MonitorModeBufferInputCount) input->pMonitorModes[0] = MonitorMode(width, height, IDDCX_MONITOR_MODE_ORIGIN_MONITORDESCRIPTOR);
    return STATUS_SUCCESS;
}
_Use_decl_annotations_
NTSTATUS DefaultModes(IDDCX_MONITOR monitor, const IDARG_IN_GETDEFAULTDESCRIPTIONMODES* input, IDARG_OUT_GETDEFAULTDESCRIPTIONMODES* output) {
    auto context = WdfObjectGet_MonitorWrapper(monitor)->Context;
    output->DefaultMonitorModeBufferOutputCount = 1; output->PreferredMonitorModeIdx = 0;
    if (input->DefaultMonitorModeBufferInputCount) input->pDefaultMonitorModes[0] = MonitorMode(context->Width, context->Height, IDDCX_MONITOR_MODE_ORIGIN_DRIVER);
    return STATUS_SUCCESS;
}
_Use_decl_annotations_
NTSTATUS TargetModes(IDDCX_MONITOR monitor, const IDARG_IN_QUERYTARGETMODES* input, IDARG_OUT_QUERYTARGETMODES* output) {
    auto context = WdfObjectGet_MonitorWrapper(monitor)->Context;
    output->TargetModeBufferOutputCount = 1;
    if (input->TargetModeBufferInputCount) {
        IDDCX_TARGET_MODE mode{}; mode.Size = sizeof(mode);
        Signal(mode.TargetVideoSignalInfo.targetVideoSignalInfo, context->Width, context->Height, false);
        input->pTargetModes[0] = mode;
    }
    return STATUS_SUCCESS;
}
_Use_decl_annotations_
NTSTATUS CommitModes(IDDCX_ADAPTER, const IDARG_IN_COMMITMODES*) { return STATUS_SUCCESS; }
_Use_decl_annotations_
NTSTATUS AssignSwapChain(IDDCX_MONITOR monitor, const IDARG_IN_SETSWAPCHAIN* input) {
    auto context = WdfObjectGet_MonitorWrapper(monitor)->Context;
    context->Frames.reset();
    context->Frames.reset(new (std::nothrow) FrameProcessor(input->hSwapChain, input->RenderAdapterLuid, input->hNextSurfaceAvailable));
    if (!context->Frames) { WdfObjectDelete(input->hSwapChain); return STATUS_SUCCESS; }
    if (!context->Frames->Start()) context->Frames.reset();
    return STATUS_SUCCESS;
}
_Use_decl_annotations_
NTSTATUS UnassignSwapChain(IDDCX_MONITOR monitor) { WdfObjectGet_MonitorWrapper(monitor)->Context->Frames.reset(); return STATUS_SUCCESS; }
