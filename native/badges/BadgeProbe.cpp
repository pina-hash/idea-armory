// BadgeProbe.exe: checks ArmoryBadges.dll the way Explorer uses it (docs/agent/EXPLORER.md;
// BadgeProbeTests run it on Windows, and developers run it under Wine). Never shipped.
//
//   BadgeProbe.exe <table.bin> <queries.txt> (--dll <ArmoryBadges.dll> | --com) [--section <name>] [--rounds N] [--iterations N]
//       The full check. Plays Armory (publishes table.bin, as built by Armory.Core.BadgeTable,
//       under a private header name unless --section names one), then Explorer: asks the four
//       handlers about every query, times IsMemberOf inside and outside the vault (the median of
//       the rounds), unpublishes (generation 0) and checks that nothing is claimed after 1.1 s,
//       then has a child copy of itself publish the table again and checks that every badge is
//       gone within 1.5 s of that child's end.
//   BadgeProbe.exe --attach [<queries.txt>] (--dll <path> | --com) [--section <name>]
//       Asks the handlers about what is published now (IdeaArmory.exe or a test's
//       BadgePublisher; the real per-user header unless --section names one).
//   BadgeProbe.exe --hold <table.bin> --section <name> --generation <G>
//       Used by the full check: publishes as generation G and waits until it is ended.
//
// queries.txt: UTF-8 lines "full path<TAB>expected state" (0 none, 1 Synced, 2 Locked, 3 Mine,
// 4 Attention). --dll loads the DLL directly (DllGetClassObject); --com uses CoCreateInstance,
// which proves the installer's registration. Exit code 0 when every check passes.
// Started as a copy named explorer.exe, it also checks the four Seen heartbeat values.
#define NOMINMAX
#include <windows.h>
#include <shlobj.h>
#include <sddl.h>
#include <stdio.h>
#include <stdint.h>
#include <algorithm>
#include <string>
#include <vector>
#include "BadgeTable.h"

using namespace armory_badges;

namespace {

constexpr int BadgeCount = 4;
const CLSID Clsids[BadgeCount] = {
    {0xE26E19F2, 0x515F, 0x472F, {0xAD, 0x4F, 0x1B, 0x02, 0x93, 0x72, 0x8C, 0xE2}},  // Attention
    {0xDB040D16, 0xC118, 0x4CDA, {0xB6, 0x16, 0xDF, 0x93, 0x40, 0xA8, 0xBC, 0x9F}},  // Mine
    {0xDF50E3A9, 0x57B8, 0x44AE, {0xB6, 0x90, 0x25, 0x7A, 0xFF, 0x28, 0x3F, 0x97}},  // Locked
    {0x58F5F8D8, 0x1041, 0x43B9, {0xB8, 0xDE, 0x0E, 0xBE, 0xBC, 0xC2, 0x9C, 0xF0}},  // Synced
};
const wchar_t* const Names[BadgeCount] = {L"Attention", L"Mine", L"Locked", L"Synced"};
const int StateOf[BadgeCount] = {Attention, Mine, Locked, Synced};
const int ExpectedPriority[BadgeCount] = {0, 10, 20, 30};
const IID IidOverlay = {0x0C6C4200, 0xC589, 0x11D0, {0x99, 0x9A, 0x00, 0xC0, 0x4F, 0xD6, 0x55, 0xE1}};

struct Query {
    std::wstring path;
    int expected;
};

std::vector<char> ReadAll(const wchar_t* path) {
    std::vector<char> data;
    HANDLE f = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, 0, nullptr);
    if (f == INVALID_HANDLE_VALUE) return data;
    LARGE_INTEGER size{};
    if (GetFileSizeEx(f, &size) && size.QuadPart > 0 && size.QuadPart < (1ll << 30)) {
        data.resize(static_cast<size_t>(size.QuadPart));
        DWORD got = 0;
        if (!ReadFile(f, data.data(), static_cast<DWORD>(data.size()), &got, nullptr) || got != data.size()) data.clear();
    }
    CloseHandle(f);
    return data;
}

std::wstring UserHeaderName() {
    HANDLE token = nullptr;
    alignas(TOKEN_USER) BYTE buffer[256];
    DWORD size = 0;
    LPWSTR sid = nullptr;
    std::wstring name = L"Local\\IDEA-Armory-Badges-";
    if (OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) {
        if (GetTokenInformation(token, TokenUser, buffer, sizeof buffer, &size) &&
            ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(buffer)->User.Sid, &sid)) {
            name += sid;
            LocalFree(sid);
        }
        CloseHandle(token);
    }
    return name;
}

std::wstring TableName(const std::wstring& header, int64_t generation) {
    wchar_t suffix[24];
    const uint64_t g = static_cast<uint64_t>(generation);
    swprintf(suffix, 24, L"-%08X%08X", static_cast<unsigned>(g >> 32), static_cast<unsigned>(g & 0xFFFFFFFFu));
    return header + suffix;
}

// Armory's side: a table section, then the header that publishes it.
struct Publication {
    HANDLE headerMap = nullptr;
    Header* header = nullptr;
    HANDLE tableMap = nullptr;
    void* tableView = nullptr;

    bool Publish(const std::wstring& headerName, std::vector<char> table, int64_t generation) {
        memcpy(table.data() + 8, &generation, 8);
        const std::wstring name = TableName(headerName, generation);
        tableMap = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, static_cast<DWORD>(table.size()), name.c_str());
        if (!tableMap) return false;
        tableView = MapViewOfFile(tableMap, FILE_MAP_WRITE, 0, 0, 0);
        if (!tableView) return false;
        memcpy(tableView, table.data(), table.size());
        if (!headerMap) {
            headerMap = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, HeaderSize, headerName.c_str());
            if (!headerMap) return false;
            header = static_cast<Header*>(MapViewOfFile(headerMap, FILE_MAP_WRITE, 0, 0, HeaderSize));
            if (!header) return false;
        }
        header->magic = HeaderMagic;
        header->version = FormatVersion;
        header->armoryPid = GetCurrentProcessId();
        FILETIME now;
        GetSystemTimeAsFileTime(&now);
        header->updatedAt = (static_cast<uint64_t>(now.dwHighDateTime) << 32) | now.dwLowDateTime;
        InterlockedExchange64(const_cast<int64_t*>(&header->generation), generation);
        return true;
    }

    void Unpublish() {
        if (header) InterlockedExchange64(const_cast<int64_t*>(&header->generation), 0);
    }

    void CloseTable() {
        if (tableView) UnmapViewOfFile(tableView);
        if (tableMap) CloseHandle(tableMap);
        tableView = nullptr;
        tableMap = nullptr;
    }

    void Close() {
        CloseTable();
        if (header) UnmapViewOfFile(header);
        if (headerMap) CloseHandle(headerMap);
        header = nullptr;
        headerMap = nullptr;
    }
};

std::vector<Query> ReadQueries(const wchar_t* file, bool& ok) {
    std::vector<Query> queries;
    ok = true;
    if (!file) return queries;
    auto raw = ReadAll(file);
    if (raw.empty()) { ok = false; return queries; }
    int wide = MultiByteToWideChar(CP_UTF8, 0, raw.data(), static_cast<int>(raw.size()), nullptr, 0);
    std::wstring text(static_cast<size_t>(wide), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, raw.data(), static_cast<int>(raw.size()), &text[0], wide);
    if (!text.empty() && text[0] == 0xFEFF) text.erase(0, 1);
    size_t start = 0;
    while (start < text.size()) {
        size_t end = text.find(L'\n', start);
        if (end == std::wstring::npos) end = text.size();
        std::wstring line = text.substr(start, end - start);
        if (!line.empty() && line.back() == L'\r') line.pop_back();
        const size_t tab = line.rfind(L'\t');
        if (tab != std::wstring::npos) queries.push_back({line.substr(0, tab), _wtoi(line.c_str() + tab + 1)});
        start = end + 1;
    }
    return queries;
}

typedef HRESULT(__stdcall* GetClassObjectFn)(REFCLSID, REFIID, void**);

bool CreateHandlers(const wchar_t* dllPath, bool com, IShellIconOverlayIdentifier* (&overlays)[BadgeCount]) {
    GetClassObjectFn getClassObject = nullptr;
    if (dllPath) {
        HMODULE dll = LoadLibraryW(dllPath);
        if (!dll) { fwprintf(stderr, L"LoadLibrary failed: %lu\n", GetLastError()); return false; }
        getClassObject = reinterpret_cast<GetClassObjectFn>(reinterpret_cast<void*>(GetProcAddress(dll, "DllGetClassObject")));
    }
    bool ok = true;
    for (int i = 0; i < BadgeCount; ++i) {
        HRESULT hr;
        if (com) {
            hr = CoCreateInstance(Clsids[i], nullptr, CLSCTX_INPROC_SERVER, IidOverlay, reinterpret_cast<void**>(&overlays[i]));
        } else {
            IClassFactory* factory = nullptr;
            hr = getClassObject ? getClassObject(Clsids[i], IID_IClassFactory, reinterpret_cast<void**>(&factory)) : E_FAIL;
            if (SUCCEEDED(hr)) {
                hr = factory->CreateInstance(nullptr, IidOverlay, reinterpret_cast<void**>(&overlays[i]));
                factory->Release();
            }
        }
        if (FAILED(hr)) {
            wprintf(L"handler %ls: cannot be created, 0x%08lX\n", Names[i], static_cast<unsigned long>(hr));
            overlays[i] = nullptr;
            ok = false;
            continue;
        }
        wchar_t icon[MAX_PATH] = {};
        int index = -1, priority = -1;
        DWORD flags = 0;
        hr = overlays[i]->GetOverlayInfo(icon, MAX_PATH, &index, &flags);
        overlays[i]->GetPriority(&priority);
        const bool good = SUCCEEDED(hr) && index == i && flags == (ISIOI_ICONFILE | ISIOI_ICONINDEX) && priority == ExpectedPriority[i];
        wprintf(L"handler %ls: GetOverlayInfo 0x%08lX index %d flags %lu priority %d icon %ls%ls\n", Names[i],
            static_cast<unsigned long>(hr), index, flags, priority, icon, good ? L"" : L" WRONG");
        if (!good) ok = false;
    }
    return ok;
}

// The state the handlers claim together: 0 none, 1 to 4 a state, -1 when two claim one item.
int Claimed(IShellIconOverlayIdentifier* (&overlays)[BadgeCount], const wchar_t* path) {
    int got = 0;
    for (int i = 0; i < BadgeCount; ++i)
        if (overlays[i] && overlays[i]->IsMemberOf(path, 0) == S_OK) got = got ? -1 : StateOf[i];
    return got;
}

int Check(IShellIconOverlayIdentifier* (&overlays)[BadgeCount], const std::vector<Query>& queries) {
    int failures = 0;
    for (auto& q : queries) {
        const int got = Claimed(overlays, q.path.c_str());
        if (got != q.expected) {
            ++failures;
            wprintf(L"MISMATCH %ls expected %d got %d\n", q.path.c_str(), q.expected, got);
        }
    }
    wprintf(L"%llu queries, %d mismatches\n", static_cast<unsigned long long>(queries.size()), failures);
    return failures;
}

bool Inside(const std::wstring& path, const std::wstring& root) {
    if (path.size() <= root.size() || path[root.size()] != L'\\') return false;
    for (size_t i = 0; i < root.size(); ++i)
        if (Fold(path[i]) != root[i]) return false;
    return true;
}

// Median nanoseconds per IsMemberOf over the rounds, every handler asked about every query.
double Time(IShellIconOverlayIdentifier* (&overlays)[BadgeCount], const std::vector<Query>& queries, bool inside,
            const std::wstring& root, int rounds, int iterations, long long& callsPerRound) {
    std::vector<const wchar_t*> paths;
    for (auto& q : queries)
        if (Inside(q.path, root) == inside) paths.push_back(q.path.c_str());
    callsPerRound = 0;
    if (paths.empty()) return 0;
    LARGE_INTEGER freq, t0, t1;
    QueryPerformanceFrequency(&freq);
    std::vector<double> perCall;
    for (int round = 0; round < rounds; ++round) {
        long long calls = 0;
        QueryPerformanceCounter(&t0);
        for (int it = 0; it < iterations; ++it)
            for (auto p : paths)
                for (int i = 0; i < BadgeCount; ++i) {
                    overlays[i]->IsMemberOf(p, 0);
                    ++calls;
                }
        QueryPerformanceCounter(&t1);
        perCall.push_back(static_cast<double>(t1.QuadPart - t0.QuadPart) * 1e9 / static_cast<double>(freq.QuadPart) / static_cast<double>(calls));
        callsPerRound = calls;
    }
    std::sort(perCall.begin(), perCall.end());
    return perCall[perCall.size() / 2];
}

// Inside a copy named explorer.exe, GetOverlayInfo writes the four Seen values.
int CheckHeartbeat() {
    wchar_t image[MAX_PATH];
    const DWORD n = GetModuleFileNameW(nullptr, image, MAX_PATH);
    const wchar_t* base = image + n;
    while (base > image && base[-1] != L'\\') --base;
    if (CompareStringOrdinal(base, -1, L"explorer.exe", -1, TRUE) != CSTR_EQUAL) return 0;
    FILETIME ft;
    GetSystemTimeAsFileTime(&ft);
    const ULONGLONG now = (static_cast<ULONGLONG>(ft.dwHighDateTime) << 32) | ft.dwLowDateTime;
    int failures = 0;
    for (int i = 0; i < BadgeCount; ++i) {
        wchar_t value[32];
        swprintf(value, 32, L"Seen%ls", Names[i]);
        ULONGLONG when = 0;
        DWORD size = sizeof when;
        const LSTATUS status = RegGetValueW(HKEY_CURRENT_USER, L"Software\\IDEA Armory\\Badges", value, RRF_RT_REG_QWORD, nullptr, &when, &size);
        const bool fresh = status == ERROR_SUCCESS && when <= now + 10000000ull && now - when < 600000000ull;
        wprintf(L"heartbeat %ls: %ls\n", value, fresh ? L"written" : L"MISSING");
        if (!fresh) ++failures;
    }
    return failures;
}

int Hold(const wchar_t* tableFile, const std::wstring& section, int64_t generation) {
    auto table = ReadAll(tableFile);
    if (table.size() < TableHeaderSize || section.empty() || generation == 0) return 2;
    Publication publication;
    if (!publication.Publish(section, table, generation)) return 1;
    Sleep(INFINITE);
    return 0;
}

}  // namespace

int wmain(int argc, wchar_t** argv) {
    const wchar_t* dllPath = nullptr;
    const wchar_t* tableFile = nullptr;
    const wchar_t* queriesFile = nullptr;
    std::wstring section;
    bool com = false, attach = false, hold = false;
    int rounds = 9, iterations = 2000;
    int64_t holdGeneration = 0;
    for (int i = 1; i < argc; ++i) {
        const wchar_t* a = argv[i];
        if (!lstrcmpW(a, L"--dll") && i + 1 < argc) dllPath = argv[++i];
        else if (!lstrcmpW(a, L"--com")) com = true;
        else if (!lstrcmpW(a, L"--attach")) attach = true;
        else if (!lstrcmpW(a, L"--hold")) hold = true;
        else if (!lstrcmpW(a, L"--section") && i + 1 < argc) section = argv[++i];
        else if (!lstrcmpW(a, L"--rounds") && i + 1 < argc) rounds = std::max(1, _wtoi(argv[++i]));
        else if (!lstrcmpW(a, L"--iterations") && i + 1 < argc) iterations = std::max(1, _wtoi(argv[++i]));
        else if (!lstrcmpW(a, L"--generation") && i + 1 < argc) holdGeneration = _wcstoi64(argv[++i], nullptr, 10);
        else if (a[0] == L'-') { fwprintf(stderr, L"unknown option %ls\n", a); return 2; }
        else if (!attach && !tableFile) tableFile = a;
        else if (!queriesFile) queriesFile = a;
    }
    if (hold) return Hold(tableFile, section, holdGeneration);
    if (!dllPath && !com) { fwprintf(stderr, L"usage: see the top of native/badges/BadgeProbe.cpp\n"); return 2; }
    if (!attach && (!tableFile || !queriesFile)) { fwprintf(stderr, L"usage: see the top of native/badges/BadgeProbe.cpp\n"); return 2; }
    if (section.empty()) {
        if (attach) section = UserHeaderName();
        else {
            wchar_t own[80];
            swprintf(own, 80, L"Local\\IDEA-Armory-Badges-Probe-%lu", GetCurrentProcessId());
            section = own;
        }
    }
    // The DLL reads the same name (it is loaded into this process).
    SetEnvironmentVariableW(L"ARMORY_BADGES_SECTION", section.c_str());
    wprintf(L"section %ls\n", section.c_str());

    bool queriesOk = true;
    auto queries = ReadQueries(queriesFile, queriesOk);
    if (!queriesOk) { fwprintf(stderr, L"cannot read %ls\n", queriesFile); return 2; }

    CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    IShellIconOverlayIdentifier* overlays[BadgeCount] = {};
    int failures = CreateHandlers(dllPath, com, overlays) ? 0 : 1;
    failures += CheckHeartbeat();

    if (attach) {
        if (!failures) failures += Check(overlays, queries);
    } else if (!failures) {
        auto table = ReadAll(tableFile);
        TableView view{};
        int64_t generation = 0;
        if (table.size() >= TableHeaderSize) memcpy(&generation, table.data() + 8, 8);
        if (!Validate(reinterpret_cast<const uint8_t*>(table.data()), table.size(), generation, view)) {
            fwprintf(stderr, L"%ls is not a format 1 badge table\n", tableFile);
            return 2;
        }
        const std::wstring root(view.root, view.rootUnits);
        Publication publication;
        if (!publication.Publish(section, table, generation)) { fwprintf(stderr, L"cannot publish: %lu\n", GetLastError()); return 1; }
        failures += Check(overlays, queries);

        long long inCalls = 0, outCalls = 0;
        const double inNs = Time(overlays, queries, true, root, rounds, iterations, inCalls);
        const double outNs = Time(overlays, queries, false, root, rounds, iterations, outCalls);
        wprintf(L"inside the vault: median %.0f ns per IsMemberOf (%d rounds of %lld calls)\n", inNs, rounds, inCalls);
        wprintf(L"outside the vault: median %.0f ns per IsMemberOf (%d rounds of %lld calls)\n", outNs, rounds, outCalls);

        // Armory turns the badges off or quits: generation 0 drops every badge.
        publication.Unpublish();
        Sleep(1100);
        int after = 0;
        for (auto& q : queries)
            if (Claimed(overlays, q.path.c_str()) != 0) ++after;
        wprintf(L"after unpublish: %d queries still have a badge\n", after);
        if (after) ++failures;
        publication.CloseTable();

        // Armory ends without a word: a child publishes, then is ended; the handlers notice.
        const Query* badged = nullptr;
        for (auto& q : queries)
            if (q.expected > 0) { badged = &q; break; }
        if (badged) {
            wchar_t self[MAX_PATH];
            GetModuleFileNameW(nullptr, self, MAX_PATH);
            wchar_t command[MAX_PATH * 3];
            swprintf(command, MAX_PATH * 3, L"\"%ls\" --hold \"%ls\" --section \"%ls\" --generation %lld", self, tableFile, section.c_str(),
                static_cast<long long>(generation + 1));
            STARTUPINFOW si{};
            si.cb = sizeof si;
            PROCESS_INFORMATION pi{};
            if (!CreateProcessW(self, command, nullptr, nullptr, FALSE, 0, nullptr, nullptr, &si, &pi)) {
                wprintf(L"publisher exit: cannot start the child publisher (%lu)\n", GetLastError());
                ++failures;
            } else {
                CloseHandle(pi.hThread);
                const ULONGLONG start = GetTickCount64();
                bool seen = false;
                while (!seen && GetTickCount64() - start < 8000) {
                    seen = Claimed(overlays, badged->path.c_str()) == badged->expected;
                    if (!seen) Sleep(50);
                }
                TerminateProcess(pi.hProcess, 0);
                WaitForSingleObject(pi.hProcess, 10000);
                CloseHandle(pi.hProcess);
                const ULONGLONG ended = GetTickCount64();
                bool gone = false;
                while (!gone && GetTickCount64() - ended < 5000) {
                    gone = Claimed(overlays, badged->path.c_str()) == 0;
                    if (!gone) Sleep(50);
                }
                const ULONGLONG ms = GetTickCount64() - ended;
                if (!seen) wprintf(L"publisher exit: the child's badges never appeared\n");
                else wprintf(L"publisher exit: badges gone %llu ms after it ended%ls\n", ms, gone && ms <= 1500 ? L"" : L" (TOO LATE)");
                if (!seen || !gone || ms > 1500) ++failures;
            }
        }
        publication.Close();
    }
    for (auto o : overlays)
        if (o) o->Release();
    CoUninitialize();
    wprintf(L"%ls\n", failures ? L"FAIL" : L"PASS");
    return failures ? 1 : 0;
}
