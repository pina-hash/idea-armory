/* ShellPipeTest.exe <ArmoryShell.exe> <count> [<pipe name>]: a lab and developer check of the
 * right-click forwarder (docs/agent/EXPLORER.md, lab check L2). It plays Armory (a pipe server
 * that acknowledges every line, as ShellInbox does) and Explorer (it starts <count> forwarders
 * back to back, one per "selected file"), then prints the first line it received, how long the
 * whole selection took to arrive and the largest gap between two arrivals.
 * Without <pipe name> it serves a private test pipe (ARMORY_SHELL_PIPE with ARMORY_DATA_DIR);
 * with one, it serves exactly that name, so the forwarder's own naming is checked. Never shipped. */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>

#define MAX_ARRIVALS 1000
static wchar_t g_pipe[300] = L"\\\\.\\pipe\\armory-shell-test";
static volatile LONG g_received = 0;
static ULONGLONG g_arrivals[MAX_ARRIVALS];
static char g_first[1024];

static DWORD WINAPI Serve(LPVOID unused) {
    (void)unused;
    for (;;) {
        HANDLE p = CreateNamedPipeW(g_pipe, PIPE_ACCESS_DUPLEX,
            PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
            PIPE_UNLIMITED_INSTANCES, 4096, 65536, 0, NULL);
        if (p == INVALID_HANDLE_VALUE) return 1;
        if (!ConnectNamedPipe(p, NULL) && GetLastError() != ERROR_PIPE_CONNECTED) {
            CloseHandle(p);
            continue;
        }
        char buffer[4096];
        DWORD got = 0, total = 0;
        while (total < sizeof buffer - 1 && ReadFile(p, buffer + total, (DWORD)(sizeof buffer - 1 - total), &got, NULL) && got > 0) {
            total += got;
            if (buffer[total - 1] == '\n') break;
        }
        buffer[total] = 0;
        /* Only a whole line is a delivery; a forwarder that refused this server sends nothing. */
        if (total > 0 && buffer[total - 1] == '\n') {
            char ack = 6;
            DWORD wrote = 0;
            WriteFile(p, &ack, 1, &wrote, NULL);
            LONG n = InterlockedIncrement(&g_received);
            if (n == 1) memcpy(g_first, buffer, total < sizeof g_first ? total + 1 : sizeof g_first - 1);
            if (n <= MAX_ARRIVALS) g_arrivals[n - 1] = GetTickCount64();
        }
        FlushFileBuffers(p);
        DisconnectNamedPipe(p);
        CloseHandle(p);
    }
}

/* Copied beside ArmoryShell.exe as IdeaArmory.exe, it plays the app a forwarder starts when
 * no Armory is running: "--background" serves the pipe named by ARMORY_TEST_PIPE for 20 s, then
 * writes how many lines it took to the file named by ARMORY_TEST_LOG. */
static int Background(void) {
    wchar_t name[200], log[MAX_PATH];
    if (!GetEnvironmentVariableW(L"ARMORY_TEST_PIPE", name, 200) || !GetEnvironmentVariableW(L"ARMORY_TEST_LOG", log, MAX_PATH)) return 2;
    _snwprintf(g_pipe, 300, L"\\\\.\\pipe\\%s", name);
    g_pipe[299] = 0;
    for (int i = 0; i < 4; ++i) {
        HANDLE t = CreateThread(NULL, 0, Serve, NULL, 0, NULL);
        if (t) CloseHandle(t);
    }
    Sleep(20000);
    char text[64];
    int n = _snprintf(text, sizeof text, "%ld lines\r\n", (long)g_received);
    HANDLE f = CreateFileW(log, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, 0, NULL);
    DWORD wrote = 0;
    if (f != INVALID_HANDLE_VALUE) {
        WriteFile(f, text, (DWORD)n, &wrote, NULL);
        CloseHandle(f);
    }
    return 0;
}

int wmain(int argc, wchar_t** argv) {
    if (argc == 2 && lstrcmpiW(argv[1], L"--background") == 0) return Background();
    if (argc < 3) {
        fwprintf(stderr, L"usage: ShellPipeTest <ArmoryShell.exe> <count> [<pipe name>]\n");
        return 2;
    }
    int count = _wtoi(argv[2]);
    if (count < 1 || count > MAX_ARRIVALS) return 2;
    if (argc > 3) {
        _snwprintf(g_pipe, 300, L"\\\\.\\pipe\\%s", argv[3]);
        g_pipe[299] = 0;
    } else {
        wchar_t temp[MAX_PATH];
        GetTempPathW(MAX_PATH, temp);
        SetEnvironmentVariableW(L"ARMORY_DATA_DIR", temp);
        SetEnvironmentVariableW(L"ARMORY_SHELL_PIPE", L"armory-shell-test");
    }
    for (int i = 0; i < 4; ++i) {
        HANDLE t = CreateThread(NULL, 0, Serve, NULL, 0, NULL);
        if (t) CloseHandle(t);
    }
    Sleep(200);
    ULONGLONG start = GetTickCount64();
    for (int i = 0; i < count; ++i) {
        wchar_t command[1024];
        _snwprintf(command, 1024, L"\"%s\" checkout \"C:\\IDEA\\Armory\\Robot 2027\\Bulk\\Part %05d.SLDPRT\"", argv[1], i);
        command[1023] = 0;
        STARTUPINFOW si;
        PROCESS_INFORMATION pi;
        ZeroMemory(&si, sizeof si);
        si.cb = sizeof si;
        if (CreateProcessW(argv[1], command, NULL, NULL, FALSE, 0, NULL, NULL, &si, &pi)) {
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
        }
    }
    ULONGLONG launched = GetTickCount64();
    ULONGLONG limit = 5000 + 300ull * (ULONGLONG)count;
    while (g_received < count && GetTickCount64() - start < limit) Sleep(10);
    LONG received = g_received;
    LONG kept = received < MAX_ARRIVALS ? received : MAX_ARRIVALS;
    ULONGLONG maxGap = 0;
    for (LONG i = 1; i < kept; ++i) {
        ULONGLONG a = g_arrivals[i - 1], b = g_arrivals[i];
        ULONGLONG gap = b > a ? b - a : a - b;
        if (gap > maxGap) maxGap = gap;
    }
    printf("first line: %s", received ? g_first : "(none)\n");
    wprintf(L"%ld of %d delivered; launches took %llu ms; all arrived after %llu ms; largest gap %llu ms\n",
        received, count, launched - start, kept ? g_arrivals[kept - 1] - start : 0ull, maxGap);
    return received == count ? 0 : 1;
}
