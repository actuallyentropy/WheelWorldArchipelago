using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using WheelWorldArchipelago.Archipelago;

namespace WheelWorldArchipelago.Utils;

public class ArchipelagoWindow
{
    #region Win32 P/Invoke

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX, ptY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowEx(uint exStyle, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] static extern bool UpdateWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern void PostQuitMessage(int code);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr w, string l);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hWnd, out RECT r);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string mod);
    [DllImport("user32.dll")] static extern IntPtr LoadCursor(IntPtr inst, int name);
    [DllImport("user32.dll")] static extern uint SetTimer(IntPtr hWnd, uint id, uint ms, IntPtr fn);
    [DllImport("user32.dll")] static extern bool KillTimer(IntPtr hWnd, uint id);

    // Window / control styles
    private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const uint WS_CHILD     = 0x40000000;
    private const uint WS_VISIBLE   = 0x10000000;
    private const uint WS_VSCROLL   = 0x00200000;
    private const uint WS_TABSTOP   = 0x00010000;
    private const uint ES_MULTILINE   = 0x0004;
    private const uint ES_AUTOVSCROLL = 0x0040;
    private const uint ES_AUTOHSCROLL = 0x0080;
    private const uint ES_READONLY    = 0x0800;
    private const uint BS_PUSHBUTTON  = 0x00000000;
    private const uint WS_EX_CLIENTEDGE = 0x00000200;

    // Messages
    private const uint WM_CREATE   = 0x0001;
    private const uint WM_DESTROY  = 0x0002;
    private const uint WM_SIZE     = 0x0005;
    private const uint WM_COMMAND  = 0x0111;
    private const uint WM_TIMER    = 0x0113;
    private const uint WM_SETTEXT  = 0x000C;
    private const uint WM_GETTEXTLENGTH = 0x000E;
    private const uint WM_VSCROLL  = 0x0115;
    private const uint EM_SETSEL      = 0x00B1;
    private const uint EM_REPLACESEL  = 0x00C2;
    private const uint EM_SETLIMITTEXT = 0x00C5;
    private const uint SB_BOTTOM = 7;

    private const uint WM_APP_LOG = 0x8001; // custom: drain log queue

    private const int SW_SHOW = 5, SW_HIDE = 0;
    private const int IDC_ARROW = 32512, COLOR_BTNFACE = 15;

    // Control IDs
    private const int ID_HOST_INPUT  = 1;
    private const int ID_SLOT_INPUT  = 2;
    private const int ID_PASS_INPUT  = 3;
    private const int ID_CONNECT_BTN = 4;
    private const int ID_LOG         = 5;
    private const int ID_CMD_INPUT   = 6;
    private const int ID_SEND_BTN    = 7;
    private const uint TIMER_POLL    = 1;

    #endregion

    // Window handles
    private IntPtr _hwnd;
    private IntPtr _hStatus;
    private IntPtr _hHostLbl, _hHostIn;
    private IntPtr _hSlotLbl, _hSlotIn;
    private IntPtr _hPassLbl, _hPassIn;
    private IntPtr _hConnBtn;
    private IntPtr _hLog;
    private IntPtr _hCmdLbl, _hCmdIn, _hSendBtn;

    private WndProcDelegate _wndProc; // keep delegate alive to prevent GC
    private bool _lastAuth;
    private readonly ConcurrentQueue<string> _logQueue = new();

    public static ArchipelagoWindow Instance { get; private set; }

    public void Show()
    {
        Instance = this;
        var t = new Thread(WindowThread) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    /// <summary>Thread-safe: enqueues a message and signals the window to display it.</summary>
    public void LogMessage(string message)
    {
        _logQueue.Enqueue(message);
        if (_hwnd != IntPtr.Zero)
            PostMessage(_hwnd, WM_APP_LOG, IntPtr.Zero, IntPtr.Zero);
    }

    private void WindowThread()
    {
        var hInst = GetModuleHandle(null);
        const string cls = "WWArchipelagoWnd";

        _wndProc = WndProc;
        var wc = new WNDCLASSEX
        {
            cbSize       = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc  = _wndProc,
            hInstance    = hInst,
            hCursor      = LoadCursor(IntPtr.Zero, IDC_ARROW),
            hbrBackground = new IntPtr(COLOR_BTNFACE + 1),
            lpszClassName = cls,
        };
        RegisterClassEx(ref wc);

        _hwnd = CreateWindowEx(0, cls, "Wheel World Archipelago",
            WS_OVERLAPPEDWINDOW | WS_VISIBLE,
            100, 100, 480, 540,
            IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero) return;

        UpdateWindow(_hwnd);

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0))
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_CREATE:
                CreateControls(hWnd);
                SetTimer(hWnd, TIMER_POLL, 500, IntPtr.Zero);
                return IntPtr.Zero;

            case WM_DESTROY:
                KillTimer(hWnd, TIMER_POLL);
                PostQuitMessage(0);
                return IntPtr.Zero;

            case WM_SIZE:
                LayoutControls(hWnd);
                return IntPtr.Zero;

            case WM_COMMAND:
                HandleCommand(wParam);
                return IntPtr.Zero;

            case WM_TIMER:
                if ((uint)wParam.ToInt64() == TIMER_POLL)
                    SyncAuthState();
                return IntPtr.Zero;

            case WM_APP_LOG:
                DrainLogQueue();
                return IntPtr.Zero;
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void CreateControls(IntPtr hWnd)
    {
        var h = GetModuleHandle(null);

        IntPtr MakeCtrl(uint exStyle, string cls, string text, uint style, int id) =>
            CreateWindowEx(exStyle, cls, text, WS_CHILD | WS_VISIBLE | style,
                0, 0, 10, 10, hWnd, new IntPtr(id), h, IntPtr.Zero);

        _hStatus  = MakeCtrl(0, "STATIC", $"Archipelago v{ArchipelagoClient.APVersion} — Status: Disconnected", 0, 0);
        _hHostLbl = MakeCtrl(0, "STATIC", "Host:", 0, 0);
        _hHostIn  = MakeCtrl(WS_EX_CLIENTEDGE, "EDIT", ArchipelagoClient.ServerData.Uri, ES_AUTOHSCROLL | WS_TABSTOP, ID_HOST_INPUT);
        _hSlotLbl = MakeCtrl(0, "STATIC", "Player Name:", 0, 0);
        _hSlotIn  = MakeCtrl(WS_EX_CLIENTEDGE, "EDIT", ArchipelagoClient.ServerData.SlotName, ES_AUTOHSCROLL | WS_TABSTOP, ID_SLOT_INPUT);
        _hPassLbl = MakeCtrl(0, "STATIC", "Password:", 0, 0);
        _hPassIn  = MakeCtrl(WS_EX_CLIENTEDGE, "EDIT", ArchipelagoClient.ServerData.Password ?? "", ES_AUTOHSCROLL | WS_TABSTOP, ID_PASS_INPUT);
        _hConnBtn = MakeCtrl(0, "BUTTON", "Connect", BS_PUSHBUTTON | WS_TABSTOP, ID_CONNECT_BTN);

        _hLog = MakeCtrl(WS_EX_CLIENTEDGE, "EDIT", "",
            ES_MULTILINE | ES_READONLY | ES_AUTOVSCROLL | WS_VSCROLL, ID_LOG);
        SendMessage(_hLog, EM_SETLIMITTEXT, new IntPtr(0x7FFFFFFE), IntPtr.Zero);

        _hCmdLbl  = MakeCtrl(0, "STATIC", "Command:", 0, 0);
        _hCmdIn   = MakeCtrl(WS_EX_CLIENTEDGE, "EDIT", "!help", ES_AUTOHSCROLL | WS_TABSTOP, ID_CMD_INPUT);
        _hSendBtn = MakeCtrl(0, "BUTTON", "Send", BS_PUSHBUTTON | WS_TABSTOP, ID_SEND_BTN);

        LayoutControls(hWnd);
        SyncAuthState();
        DrainLogQueue();
    }

    private void LayoutControls(IntPtr hWnd)
    {
        GetClientRect(hWnd, out var r);
        int w = r.right, h = r.bottom;
        const int P = 8, LH = 20, IH = 24, BH = 28, LW = 95, SEND_W = 60;

        int y = P;
        Mv(_hStatus,  P, y, w - P * 2, LH);          y += LH + P;
        Mv(_hHostLbl, P, y + 3, LW, LH);
        Mv(_hHostIn,  P + LW, y, w - P * 2 - LW, IH); y += IH + 4;
        Mv(_hSlotLbl, P, y + 3, LW, LH);
        Mv(_hSlotIn,  P + LW, y, w - P * 2 - LW, IH); y += IH + 4;
        Mv(_hPassLbl, P, y + 3, LW, LH);
        Mv(_hPassIn,  P + LW, y, w - P * 2 - LW, IH); y += IH + 4;
        Mv(_hConnBtn, P, y, 90, BH);                   y += BH + P;

        int cmdRowH = IH + P * 2;
        int logH = Math.Max(h - y - cmdRowH, 50);
        Mv(_hLog, P, y, w - P * 2, logH);             y += logH + P;

        Mv(_hCmdLbl,  P, y + 2, LW, LH);
        Mv(_hCmdIn,   P + LW, y, w - P * 2 - LW - SEND_W - 4, IH);
        Mv(_hSendBtn, w - P - SEND_W, y, SEND_W, IH);
    }

    private void Mv(IntPtr hWnd, int x, int y, int w, int h) =>
        MoveWindow(hWnd, x, y, w, h, true);

    private void SyncAuthState()
    {
        bool auth = ArchipelagoClient.Authenticated;
        if (auth == _lastAuth) return;
        _lastAuth = auth;

        int conn = auth ? SW_HIDE : SW_SHOW;
        int cmd  = auth ? SW_SHOW : SW_HIDE;

        ShowWindow(_hHostLbl, conn); ShowWindow(_hHostIn, conn);
        ShowWindow(_hSlotLbl, conn); ShowWindow(_hSlotIn, conn);
        ShowWindow(_hPassLbl, conn); ShowWindow(_hPassIn, conn);
        ShowWindow(_hConnBtn, conn);
        ShowWindow(_hCmdLbl, cmd); ShowWindow(_hCmdIn, cmd); ShowWindow(_hSendBtn, cmd);

        string status = auth
            ? $"Archipelago v{ArchipelagoClient.APVersion} — Status: Connected"
            : $"Archipelago v{ArchipelagoClient.APVersion} — Status: Disconnected";
        SendMessage(_hStatus, WM_SETTEXT, IntPtr.Zero, status);
    }

    private void DrainLogQueue()
    {
        while (_logQueue.TryDequeue(out var line))
            AppendToLog(line);
    }

    private void AppendToLog(string line)
    {
        var len = SendMessage(_hLog, WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero).ToInt64();
        SendMessage(_hLog, EM_SETSEL, new IntPtr(len), new IntPtr(len));
        SendMessage(_hLog, EM_REPLACESEL, IntPtr.Zero, line + "\r\n");
        SendMessage(_hLog, WM_VSCROLL, new IntPtr(SB_BOTTOM), IntPtr.Zero);
    }

    private void HandleCommand(IntPtr wParam)
    {
        uint wp   = (uint)wParam.ToInt64();
        int id    = (int)(wp & 0xFFFF);
        int notif = (int)((wp >> 16) & 0xFFFF);
        if (notif != 0) return; // only BN_CLICKED (0)

        if (id == ID_CONNECT_BTN)
        {
            string uri  = GetText(_hHostIn);
            string slot = GetText(_hSlotIn);
            string pass = GetText(_hPassIn);
            if (!string.IsNullOrWhiteSpace(slot))
            {
                ArchipelagoClient.ServerData.Uri      = uri;
                ArchipelagoClient.ServerData.SlotName = slot;
                ArchipelagoClient.ServerData.Password = pass;
                try
                {
                    Plugin.ArchipelagoClient.Connect();
                }
                catch (Exception ex)
                {
                    AppendToLog($"[Error] Connection failed: {ex.Message}");
                    Plugin.BepinLogger.LogError(ex);
                }
            }
        }
        else if (id == ID_SEND_BTN)
        {
            string cmd = GetText(_hCmdIn);
            if (!string.IsNullOrWhiteSpace(cmd) && ArchipelagoClient.Authenticated)
            {
                try
                {
                    Plugin.ArchipelagoClient.SendMessage(cmd);
                    SendMessage(_hCmdIn, WM_SETTEXT, IntPtr.Zero, "");
                }
                catch (Exception ex)
                {
                    AppendToLog($"[Error] Send failed: {ex.Message}");
                    Plugin.BepinLogger.LogError(ex);
                }
            }
        }
    }

    private string GetText(IntPtr hWnd)
    {
        var sb = new StringBuilder(512);
        GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }
}
