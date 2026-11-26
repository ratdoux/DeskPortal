#include <windows.h>
#include <appmodel.h>
#include <shlobj.h>
#include <shellapi.h>
#include <propkey.h>
#include <propvarutil.h>
#include <winrt/base.h>
#include <string>

namespace {
HICON windowIcon(HWND window) {
    for (WPARAM type : { ICON_BIG, ICON_SMALL2, ICON_SMALL }) {
        DWORD_PTR icon = 0;
        if (SendMessageTimeoutW(window, WM_GETICON, type, 96, SMTO_ABORTIFHUNG | SMTO_BLOCK, 50, &icon) && icon)
            if (auto copy = CopyIcon(reinterpret_cast<HICON>(icon))) return copy;
    }
    return nullptr;
}

HICON shellIcon(LPCWSTR path, UINT flags) {
    SHFILEINFOW info{};
    SHGetFileInfoW(path, 0, &info, sizeof(info), SHGFI_ICON | SHGFI_LARGEICON | flags);
    return info.hIcon; // The caller owns this icon.
}

HICON packagedIcon(HWND window, HANDLE process) {
    std::wstring id;
    winrt::com_ptr<IPropertyStore> props;
    PROPVARIANT value{};
    if (SUCCEEDED(SHGetPropertyStoreForWindow(window, IID_PPV_ARGS(props.put()))) &&
        SUCCEEDED(props->GetValue(PKEY_AppUserModel_ID, &value)) && value.vt == VT_LPWSTR && value.pwszVal)
        id = value.pwszVal;
    PropVariantClear(&value);
    if (id.empty() && process) {
        WCHAR appId[APPLICATION_USER_MODEL_ID_MAX_LENGTH + 1]{};
        UINT32 length = ARRAYSIZE(appId);
        if (GetApplicationUserModelId(process, &length, appId) == ERROR_SUCCESS) id = appId;
    }
    if (id.empty() || id.size() > APPLICATION_USER_MODEL_ID_MAX_LENGTH || id.find_first_of(L"/\\:") != std::wstring::npos) return nullptr;
    PIDLIST_ABSOLUTE item = nullptr;
    if (FAILED(SHParseDisplayName((L"shell:AppsFolder\\" + id).c_str(), nullptr, &item, 0, nullptr))) return nullptr;
    auto icon = shellIcon(reinterpret_cast<LPCWSTR>(item), SHGFI_PIDL);
    CoTaskMemFree(item);
    return icon;
}

HICON readIcon(HWND window) {
    if (!IsWindow(window)) return nullptr;
    if (auto icon = windowIcon(window)) return icon;
    auto owner = GetAncestor(window, GA_ROOTOWNER);
    if (owner && owner != window)
        if (auto icon = windowIcon(owner)) return icon;
    DWORD pid = 0;
    GetWindowThreadProcessId(window, &pid);
    winrt::handle process{ OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid) };
    if (auto icon = packagedIcon(window, process.get())) return icon;
    for (int index : { GCLP_HICON, GCLP_HICONSM })
        if (auto icon = reinterpret_cast<HICON>(GetClassLongPtrW(window, index)))
            if (auto copy = CopyIcon(icon)) return copy;
    WCHAR path[32768];
    DWORD length = ARRAYSIZE(path);
    if (process && QueryFullProcessImageNameW(process.get(), 0, path, &length)) return shellIcon(path, 0);
    return nullptr;
}
}

extern "C" __declspec(dllexport) HICON CopySourceWindowIcon(HWND window) noexcept {
    auto apartment = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    HICON result = nullptr;
    try { result = readIcon(window); } catch (...) { /* Use the viewer's fallback. */ }
    if (SUCCEEDED(apartment)) CoUninitialize();
    return result;
}

// The taskbar otherwise groups the views with DeskPortal and displays the host
// executable's icon. A remote window lives only for the duration of its stream.
extern "C" __declspec(dllexport) void SetStreamTaskbarIdentity(HWND window, LPCWSTR id) noexcept {
    winrt::com_ptr<IPropertyStore> props;
    if (FAILED(SHGetPropertyStoreForWindow(window, IID_PPV_ARGS(props.put())))) return;
    PROPVARIANT value{};
    if (id) InitPropVariantFromBoolean(TRUE, &value);
    props->SetValue(PKEY_AppUserModel_PreventPinning, value);
    PropVariantClear(&value);
    if (id && FAILED(InitPropVariantFromString(id, &value))) return;
    props->SetValue(PKEY_AppUserModel_ID, value);
    PropVariantClear(&value);
}
