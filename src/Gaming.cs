// Игровые режимы: Steam Big Picture и режим Xbox в Windows 11 (бывший Xbox Full Screen Experience,
// переключается сочетанием Win+F11). Обе кнопки горят, пока режим открыт. Вместе режимы не работают:
// включение одного сначала выключает другой.
// «Диван»: сначала картинка только на ТВ, потом Big Picture или режим Xbox.
// «Офис»: обратно за стол — выйти из режима Xbox, закрыть Big Picture (сами Steam и Xbox остаются запущенными),
// картинка только на монитор, свернуть все окна.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

static class Gaming
{
    public const string Group = "gaming.";

    // context → "bigpicture" / "xbox_mode"; под lock(keys).
    static readonly Dictionary<string, string> keys = new Dictionary<string, string>();
    static int shownBigPicture = -1, shownXbox = -1;

    public static void Handle(string ev, string name, string context)
    {
        if (name == "bigpicture" || name == "xbox_mode")
        {
            switch (ev)
            {
                case "willAppear":
                    lock (keys) keys[context] = name;
                    Refresh(true);
                    break;
                case "willDisappear":
                    lock (keys) keys.Remove(context);
                    break;
                case "keyDown":
                    if (name == "xbox_mode") Run(() => { if (XboxModeActive()) ExitXboxMode(); else EnterXboxMode(); });
                    else Run(() => { if (BigPictureOpen()) CloseBigPicture(); else OpenBigPicture(); });
                    break;
                case "keyUp":
                    ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(250); Refresh(true); });
                    break;
            }
        }
        else if ((name == "couch_bigpicture" || name == "couch_xbox") && ev == "keyDown")
        {
            bool xbox = name == "couch_xbox";
            Run(() => Couch(context, xbox));
        }
        else if (name == "office" && ev == "keyDown")
        {
            Run(() => Office(context));
        }
    }

    // Действия ждут, пока режимы реально переключатся, поэтому идут в фоне и строго по очереди:
    // два быстрых нажатия не перемешаются.
    static readonly object sequence = new object();

    static void Run(Action action)
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            lock (sequence)
            {
                try { action(); }
                catch (Exception e) { Program.Log("Gaming: " + e); }
            }
            Refresh(true);
        });
    }

    // ТВ включается не мгновенно: после переключения ждём, пока картинка устаканится, и только потом режим.
    static void Couch(string context, bool xbox)
    {
        int switched = Displays.SwitchTo(Displays.External);
        if (switched < 0) { Program.ShowAlert(context); return; }
        if (switched > 0) Thread.Sleep(3000);
        if (xbox) EnterXboxMode(); else OpenBigPicture();
    }

    static void Office(string context)
    {
        ExitXboxMode();
        CloseBigPicture();
        int switched = Displays.SwitchTo(Displays.Internal);
        if (switched < 0) Program.ShowAlert(context);
        if (switched > 0) Thread.Sleep(2500);  // окна переезжают на монитор — сворачиваем уже после
        Tools.MinimizeAll();
    }

    static void OpenBigPicture()
    {
        ExitXboxMode();
        if (!BigPictureOpen()) Open("steam://open/bigpicture");
    }

    static void CloseBigPicture()
    {
        if (!BigPictureOpen()) return;
        Open("steam://close/bigpicture");
        WaitFor(() => !BigPictureOpen(), 5000);
        Thread.Sleep(500);
    }

    static void EnterXboxMode()
    {
        CloseBigPicture();
        if (XboxModeActive()) return;
        SendKeys(VK_LWIN, VK_F11);
        WaitFor(XboxModeActive, 5000);
    }

    // После выхода даём рабочему столу секунду вернуться, прежде чем открывать что-то следующее.
    static void ExitXboxMode()
    {
        if (!XboxModeActive()) return;
        SendKeys(VK_LWIN, VK_F11);
        WaitFor(() => !XboxModeActive(), 5000);
        Thread.Sleep(1000);
    }

    static void WaitFor(Func<bool> done, int timeoutMs)
    {
        for (int waited = 0; waited < timeoutMs && !done(); waited += 250) Thread.Sleep(250);
    }

    public static void Refresh(bool force)
    {
        int bigPicture = BigPictureOpen() ? 1 : 0, xbox = XboxModeActive() ? 1 : 0;
        lock (keys)
        {
            if (keys.Count == 0 || (!force && bigPicture == shownBigPicture && xbox == shownXbox)) return;
            shownBigPicture = bigPicture;
            shownXbox = xbox;
            foreach (var key in keys) Program.SetState(key.Key, key.Value == "xbox_mode" ? xbox : bigPicture);
        }
    }

    // Официальная проверка режима Xbox (Windows SDK 2025: IsGamingFullScreenExperienceActive, живёт в gamemode.dll).
    static bool xboxCheckBroken;

    static bool XboxModeActive()
    {
        if (xboxCheckBroken) return false;
        try { return IsGamingFullScreenExperienceActive(); }
        catch (Exception e)
        {
            xboxCheckBroken = true;
            Program.Log("Gaming: Xbox mode check unavailable: " + e.Message);
            return false;
        }
    }

    [DllImport("gamemode.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool IsGamingFullScreenExperienceActive();

    static void Open(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch (Exception e) { Program.Log("Gaming: " + uri + ": " + e.Message); }
    }

    // Окно Big Picture у Steam называется «Steam Big Picture Mode» (класс SDL_app у steamwebhelper).
    static bool BigPictureOpen()
    {
        bool found = false;
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            var title = new StringBuilder(256);
            var cls = new StringBuilder(64);
            GetWindowText(hwnd, title, title.Capacity);
            GetClassName(hwnd, cls, cls.Capacity);
            if (cls.ToString() == "SDL_app" && title.ToString().IndexOf("Big Picture", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                found = true;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    const ushort VK_LWIN = 0x5B, VK_F11 = 0x7A;
    const uint KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2;

    // Нажать сочетание: клавиши вниз по порядку, вверх в обратном.
    static void SendKeys(params ushort[] vks)
    {
        var inputs = new INPUT[vks.Length * 2];
        for (int i = 0; i < vks.Length; i++)
        {
            uint ext = vks[i] == VK_LWIN ? KEYEVENTF_EXTENDEDKEY : 0;
            inputs[i] = Key(vks[i], ext);
            inputs[inputs.Length - 1 - i] = Key(vks[i], ext | KEYEVENTF_KEYUP);
        }
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
    }

    static INPUT Key(ushort vk, uint flags)
    {
        var input = new INPUT { type = 1 };
        input.u.ki = new KEYBDINPUT { wVk = vk, dwFlags = flags };
        return input;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public InputUnion u; }

    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);

    [DllImport("user32.dll")] static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
}
