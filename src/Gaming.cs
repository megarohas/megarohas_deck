// Игровые режимы: Steam Big Picture (горит, пока открыт; нажатие открывает или закрывает его)
// и режим Xbox в Windows 11 (бывший Xbox Full Screen Experience; переключается сочетанием Win+F11).
// «Диван»: сначала картинка только на ТВ, потом Big Picture или режим Xbox.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

static class Gaming
{
    public const string Group = "gaming.";

    static readonly HashSet<string> bigPictureKeys = new HashSet<string>();
    static int shown = -1;

    public static void Handle(string ev, string name, string context)
    {
        if (name == "bigpicture")
        {
            switch (ev)
            {
                case "willAppear":
                    lock (bigPictureKeys) bigPictureKeys.Add(context);
                    Refresh(true);
                    break;
                case "willDisappear":
                    lock (bigPictureKeys) bigPictureKeys.Remove(context);
                    break;
                case "keyDown":
                    Open(BigPictureOpen() ? "steam://close/bigpicture" : "steam://open/bigpicture");
                    break;
                case "keyUp":
                    ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(250); Refresh(true); });
                    break;
            }
        }
        else if (name == "xbox_mode" && ev == "keyDown")
        {
            SendKeys(VK_LWIN, VK_F11);
        }
        else if ((name == "couch_bigpicture" || name == "couch_xbox") && ev == "keyDown")
        {
            bool xbox = name == "couch_xbox";
            ThreadPool.QueueUserWorkItem(_ => Couch(context, xbox));
        }
    }

    // ТВ включается не мгновенно: после переключения ждём, пока картинка устаканится, и только потом режим.
    static void Couch(string context, bool xbox)
    {
        int switched = Displays.SwitchTo(Displays.External);
        if (switched < 0) { Program.ShowAlert(context); return; }
        if (switched > 0) Thread.Sleep(3000);
        if (xbox) SendKeys(VK_LWIN, VK_F11);
        else if (!BigPictureOpen()) Open("steam://open/bigpicture");
        Thread.Sleep(1500);
        Refresh(true);
    }

    public static void Refresh(bool force)
    {
        int state = BigPictureOpen() ? 1 : 0;
        lock (bigPictureKeys)
        {
            if (bigPictureKeys.Count == 0 || (!force && state == shown)) return;
            shown = state;
            foreach (var key in bigPictureKeys) Program.SetState(key, state);
        }
    }

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
