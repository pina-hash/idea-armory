/* ArmoryShell.exe <verb> "<path>": what an IDEA Armory item on File Explorer's right-click menu
 * runs (docs/agent/EXPLORER.md). Explorer starts one copy per selected item. Each copy hands its
 * one path to the running IdeaArmory.exe over a per-user named pipe and exits; Armory gathers
 * the copies that arrive close together into one action and answers with one sentence.
 *
 * Protocol: one UTF-8 line "1<TAB>verb<TAB>path<TAB>GetTickCount64()<LF>"; Armory answers one
 * byte, 0x06 when it took the line (0x15, or the pipe closing, when it did not).
 * Exit codes: 0 delivered, 1 not delivered, 2 bad arguments.
 * No window, no console, no network, no disk writes. */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellapi.h>
#include <sddl.h>
#include <bcrypt.h>
#include <strsafe.h>

static const wchar_t* const Verbs[] = {L"checkout", L"checkoutopen", L"checkin", L"undo", L"show", L"forcecheckin"};
#define PATH_LIMIT 32767
#define LINE_LIMIT (PATH_LIMIT + 64)
#define START_WAIT_MS 30000ull  /* Armory starting in the tray */
#define BUSY_WAIT_MS 10000ull   /* every pipe instance busy */
#define GIVE_UP_MS 60000      /* this copy never lingers longer than this */
#define ACK 0x06

static BOOL KnownVerb(const wchar_t* verb) {
    for (int i = 0; i < (int)(sizeof Verbs / sizeof Verbs[0]); ++i)
        if (lstrcmpW(verb, Verbs[i]) == 0) return TRUE;
    return FALSE;
}

/* The environment variable's value, or FALSE when it is not set or does not fit. */
static BOOL Variable(const wchar_t* name, wchar_t* out, DWORD cch) {
    DWORD got = GetEnvironmentVariableW(name, out, cch);
    return got > 0 && got < cch;
}

/* ARMORY_DATA_DIR as AgentPaths.Resolve reads it: set and fully qualified (a test instance),
 * then made a full path. FALSE for the real app, whose data folder is not overridden. */
static BOOL TestDataFolder(wchar_t* full, DWORD cch) {
    wchar_t data[1024];
    if (!Variable(L"ARMORY_DATA_DIR", data, 1024)) return FALSE;
    BOOL drive = ((data[0] >= L'A' && data[0] <= L'Z') || (data[0] >= L'a' && data[0] <= L'z')) && data[1] == L':' && (data[2] == L'\\' || data[2] == L'/');
    BOOL unc = (data[0] == L'\\' || data[0] == L'/') && (data[1] == L'\\' || data[1] == L'/');
    if (!drive && !unc) return FALSE;
    DWORD n = GetFullPathNameW(data, cch, full, NULL);
    return n > 0 && n < cch;
}

/* AgentPaths.InstanceSuffix: for a test instance, "-" and the first 16 lowercase hex digits of
 * SHA-256 over the UTF-8 of its data folder's full path in upper case; otherwise nothing. */
static BOOL InstanceSuffix(wchar_t* out, size_t cch) {
    wchar_t full[1024], upper[1024];
    char utf8[4096];
    UCHAR digest[32];
    BCRYPT_ALG_HANDLE alg = NULL;
    BCRYPT_HASH_HANDLE hash = NULL;
    out[0] = 0;
    if (!TestDataFolder(full, 1024)) return TRUE;
    int units = LCMapStringEx(LOCALE_NAME_INVARIANT, LCMAP_UPPERCASE, full, -1, upper, 1024, NULL, NULL, 0);
    if (units <= 1) return FALSE;
    int bytes = WideCharToMultiByte(CP_UTF8, 0, upper, units - 1, utf8, (int)sizeof utf8, NULL, NULL);
    if (bytes <= 0) return FALSE;
    BOOL ok = BCRYPT_SUCCESS(BCryptOpenAlgorithmProvider(&alg, BCRYPT_SHA256_ALGORITHM, NULL, 0))
        && BCRYPT_SUCCESS(BCryptCreateHash(alg, &hash, NULL, 0, NULL, 0, 0))
        && BCRYPT_SUCCESS(BCryptHashData(hash, (PUCHAR)utf8, (ULONG)bytes, 0))
        && BCRYPT_SUCCESS(BCryptFinishHash(hash, digest, (ULONG)sizeof digest, 0));
    if (hash) BCryptDestroyHash(hash);
    if (alg) BCryptCloseAlgorithmProvider(alg, 0);
    if (!ok || cch < 18) return FALSE;
    const wchar_t* hex = L"0123456789abcdef";
    out[0] = L'-';
    for (int i = 0; i < 8; ++i) {
        out[1 + i * 2] = hex[digest[i] >> 4];
        out[2 + i * 2] = hex[digest[i] & 15];
    }
    out[17] = 0;
    return TRUE;
}

/* \\.\pipe\IDEA-Armory-Agent-<user SID><instance suffix>-shell: the agent's single-instance
 * name plus "-shell". ARMORY_SHELL_PIPE replaces it for automated tests, and only for a test
 * instance (ARMORY_DATA_DIR), exactly as in the app (ShellInbox.PipeName). */
static BOOL PipeName(wchar_t* out, size_t cch, BOOL* overridden) {
    wchar_t chosen[200], full[1024], suffix[24];
    HANDLE token = NULL;
    DECLSPEC_ALIGN(8) BYTE buffer[256];
    DWORD size = 0;
    LPWSTR sid = NULL;
    *overridden = FALSE;
    if (Variable(L"ARMORY_SHELL_PIPE", chosen, 200) && TestDataFolder(full, 1024)) {
        *overridden = TRUE;
        return SUCCEEDED(StringCchPrintfW(out, cch, L"\\\\.\\pipe\\%s", chosen));
    }
    if (!InstanceSuffix(suffix, 24)) return FALSE;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) return FALSE;
    BOOL ok = GetTokenInformation(token, TokenUser, buffer, (DWORD)sizeof buffer, &size);
    CloseHandle(token);
    if (!ok || !ConvertSidToStringSidW(((TOKEN_USER*)buffer)->User.Sid, &sid)) return FALSE;
    HRESULT hr = StringCchPrintfW(out, cch, L"\\\\.\\pipe\\IDEA-Armory-Agent-%s%s-shell", sid, suffix);
    LocalFree(sid);
    return SUCCEEDED(hr);
}

/* IdeaArmory.exe next to this file. */
static BOOL AppPath(wchar_t* out, DWORD cch) {
    DWORD n = GetModuleFileNameW(NULL, out, cch);
    if (n == 0 || n >= cch) return FALSE;
    while (n > 0 && out[n - 1] != L'\\') --n;
    return SUCCEEDED(StringCchCopyW(out + n, cch - n, L"IdeaArmory.exe"));
}

static HANDLE Connect(const wchar_t* pipe) {
    /* SECURITY_IDENTIFICATION: the server may learn who connected, never act as this user. */
    return CreateFileW(pipe, GENERIC_READ | GENERIC_WRITE, 0, NULL, OPEN_EXISTING,
        SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, NULL);
}

/* The long form of a path, for comparing two names of one file. */
static void LongForm(wchar_t* path, DWORD cch) {
    wchar_t longer[MAX_PATH * 2];
    DWORD n = GetLongPathNameW(path, longer, MAX_PATH * 2);
    if (n > 0 && n < MAX_PATH * 2 && n < cch) StringCchCopyW(path, cch, longer);
}

/* The server must be IdeaArmory.exe from this folder, so a path never goes to another program. */
static BOOL ServerIsArmory(HANDLE pipe, const wchar_t* app, BOOL test, ULONG* pid) {
    wchar_t image[MAX_PATH * 2], expected[MAX_PATH * 2];
    DWORD cch = MAX_PATH * 2;
    if (!GetNamedPipeServerProcessId(pipe, pid)) return FALSE;
    if (test) return TRUE; /* the test's own process serves the pipe */
    HANDLE p = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, *pid);
    if (!p) return FALSE;
    BOOL ok = QueryFullProcessImageNameW(p, 0, image, &cch);
    CloseHandle(p);
    if (!ok || FAILED(StringCchCopyW(expected, MAX_PATH * 2, app))) return FALSE;
    LongForm(image, MAX_PATH * 2);
    LongForm(expected, MAX_PATH * 2);
    return CompareStringOrdinal(image, -1, expected, -1, TRUE) == CSTR_EQUAL;
}

static DWORD WINAPI GiveUp(LPVOID unused) {
    (void)unused;
    Sleep(GIVE_UP_MS);
    TerminateProcess(GetCurrentProcess(), 1);
    return 1;
}

static wchar_t g_line[LINE_LIMIT];
static char g_utf8[LINE_LIMIT * 3];

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE previous, PWSTR commandLine, int show) {
    (void)instance;
    (void)previous;
    (void)commandLine;
    (void)show;
    int argc = 0;
    LPWSTR* argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (!argv || argc != 3 || !KnownVerb(argv[1])) return 2;
    wchar_t* path = argv[2];
    size_t length = wcslen(path);
    if (length == 0 || length > PATH_LIMIT) return 2;
    /* "D:\" quoted as "%1" arrives as D:" (the backslash escaped the quote); a quote is never
     * part of a Windows path, so it stands for that backslash. */
    if (path[length - 1] == L'"') path[length - 1] = L'\\';
    for (size_t i = 0; i < length; ++i)
        if (path[i] < 32 || path[i] == L'"') return 2; /* no tab, line break or quote */

    HANDLE watchdog = CreateThread(NULL, 0, GiveUp, NULL, 0, NULL);
    if (watchdog) CloseHandle(watchdog);

    wchar_t pipeName[300], app[MAX_PATH * 2];
    BOOL test = FALSE;
    if (!PipeName(pipeName, 300, &test) || !AppPath(app, MAX_PATH * 2)) return 1;

    ULONGLONG started = GetTickCount64();
    BOOL launched = FALSE;
    HANDLE pipe = INVALID_HANDLE_VALUE;
    for (;;) {
        pipe = Connect(pipeName);
        if (pipe != INVALID_HANDLE_VALUE) break;
        DWORD error = GetLastError();
        ULONGLONG waited = GetTickCount64() - started;
        if (error == ERROR_PIPE_BUSY) {
            if (waited > BUSY_WAIT_MS + (launched ? START_WAIT_MS : 0)) return 1;
            WaitNamedPipeW(pipeName, 1000);
            continue;
        }
        if (!launched && !test) {
            /* Armory is not running: start it in the tray, as the sign-in entry does. Several
             * copies may do this at once; its single-instance guard keeps one. */
            STARTUPINFOW si;
            PROCESS_INFORMATION pi;
            wchar_t command[MAX_PATH * 2 + 32];
            ZeroMemory(&si, sizeof si);
            si.cb = sizeof si;
            if (FAILED(StringCchPrintfW(command, MAX_PATH * 2 + 32, L"\"%s\" --background", app))) return 1;
            if (!CreateProcessW(app, command, NULL, NULL, FALSE, 0, NULL, NULL, &si, &pi)) return 1;
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
            launched = TRUE;
        }
        if (waited > START_WAIT_MS) return 1;
        Sleep(100);
    }

    ULONG server = 0;
    if (!ServerIsArmory(pipe, app, test, &server)) {
        CloseHandle(pipe);
        return 1;
    }
    /* Explorer started this copy in answer to a click, so it may pass the foreground on:
     * "Show in Armory" and Armory's questions can then come to the front. */
    AllowSetForegroundWindow(server);

    size_t n = 0;
    if (FAILED(StringCchPrintfW(g_line, LINE_LIMIT, L"1\t%s\t%s\t%llu\n", argv[1], path, (unsigned long long)GetTickCount64()))
        || FAILED(StringCchLengthW(g_line, LINE_LIMIT, &n))) {
        CloseHandle(pipe);
        return 1;
    }
    int bytes = WideCharToMultiByte(CP_UTF8, 0, g_line, (int)n, g_utf8, (int)sizeof g_utf8, NULL, NULL);
    DWORD wrote = 0, read = 0;
    char ack = 0;
    BOOL ok = bytes > 0 && WriteFile(pipe, g_utf8, (DWORD)bytes, &wrote, NULL) && wrote == (DWORD)bytes
        && ReadFile(pipe, &ack, 1, &read, NULL) && read == 1 && ack == ACK;
    CloseHandle(pipe);
    LocalFree(argv);
    return ok ? 0 : 1;
}
