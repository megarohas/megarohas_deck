// Кнопки режима проецирования, то же, что Win+P: «только монитор», «только ТВ», «расширить», «дублировать».
// Кнопка текущего режима горит (состояние 1), остальные тусклые (состояние 0).
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

static class Displays
{
    public const string Group = "display.";

    const uint SDC_APPLY = 0x80, QDC_DATABASE_CURRENT = 0x4;
    const int ERROR_INSUFFICIENT_BUFFER = 122;
    const int PathInfoSize = 72, ModeInfoSize = 64;  // DISPLAYCONFIG_PATH_INFO / DISPLAYCONFIG_MODE_INFO

    static readonly Dictionary<string, uint> Topologies = new Dictionary<string, uint>
    {
        { "internal", 0x1 },  // DISPLAYCONFIG_TOPOLOGY_INTERNAL — «только экран компьютера» (основной монитор)
        { "clone", 0x2 },
        { "extend", 0x4 },
        { "external", 0x8 },  // DISPLAYCONFIG_TOPOLOGY_EXTERNAL — ТВ
    };

    [DllImport("user32.dll")] static extern int SetDisplayConfig(uint numPaths, IntPtr paths, uint numModes, IntPtr modes, uint flags);
    [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);
    [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint numPaths, IntPtr paths, ref uint numModes, IntPtr modes, out uint topology);

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint size, time; }

    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    // context кнопки на деке → её режим; под lock(keys) — его трогают поток чтения и таймер.
    static readonly Dictionary<string, uint> keys = new Dictionary<string, uint>();
    static uint shown = uint.MaxValue;  // режим, который сейчас подсвечен

    public static void Handle(string ev, string name, string context)
    {
        uint topology;
        if (!Topologies.TryGetValue(name, out topology)) return;
        switch (ev)
        {
            case "willAppear":
                lock (keys) keys[context] = topology;
                Refresh(true);
                break;
            case "willDisappear":
                lock (keys) keys.Remove(context);
                break;
            case "keyDown":
                int error = SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, SDC_APPLY | topology);
                if (error != 0)
                {
                    Program.Log("SetDisplayConfig(" + topology + ") = " + error);
                    Program.ShowAlert(context);
                }
                Refresh(true);
                Audio.FollowScreen();
                break;
            case "keyUp":
                // Софт сам перещёлкивает состояние двухрежимной кнопки после нажатия — возвращаем правду.
                ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(250); Refresh(true); });
                break;
        }
    }

    public const uint Internal = 0x1, External = 0x8;

    // Перейти на режим, если он ещё не включён. -1 — не вышло (ТВ отключён и т. п.), 0 — уже был, 1 — переключили.
    public static int SwitchTo(uint topology)
    {
        if (CurrentTopology() == topology) return 0;
        int error = SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, SDC_APPLY | topology);
        if (error != 0) Program.Log("SetDisplayConfig(" + topology + ") = " + error);
        Refresh(true);
        return error == 0 ? 1 : -1;
    }

    // Смена экрана любым способом, в том числе через Win+P, уводит за собой и звук.
    public static void Refresh(bool force)
    {
        uint current = CurrentTopology();
        bool changed;
        lock (keys)
        {
            changed = current != shown && current != 0 && shown != uint.MaxValue;
            if (!force && current == shown) return;
            shown = current;
            foreach (var key in keys) Program.SetState(key.Key, key.Value == current ? 1 : 0);
        }
        if (!changed) return;
        Audio.FollowScreen();
        if (current == External) ParkCursor();
    }

    const int ParkMs = 15000;  // сколько держать курсор у края
    static int parkRequest;    // номер последнего запроса ParkCursor: новый отменяет прежний

    // Курсор к правому краю основного экрана, посередине по высоте, чтобы не висел посреди картинки. Координаты в настоящих
    // пикселях: масштаб у ТВ и монитора разный, а плагин сам по себе про него не знает.
    // Windows ставит курсор посреди экрана при каждой смене картинки, а ТВ, пока просыпается, может сменить её ещё раз,
    // поэтому 15 секунд держим курсор у края, пока мышью и клавиатурой никто не пользовался.
    public static void ParkCursor()
    {
        int request = Interlocked.Increment(ref parkRequest);
        uint since = (uint)Environment.TickCount;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            IntPtr dpi = SetThreadDpiAwarenessContext(new IntPtr(-4));  // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
            try
            {
                for (int waited = 0; waited <= ParkMs && request == Thread.VolatileRead(ref parkRequest); waited += 500)
                {
                    var last = new LASTINPUTINFO { size = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
                    if (GetLastInputInfo(ref last) && unchecked((int)(last.time - since)) > 0) return;  // мышь взяли в руки
                    int x = GetSystemMetrics(0 /* SM_CXSCREEN */) - 1, y = GetSystemMetrics(1 /* SM_CYSCREEN */) / 2;
                    POINT at;
                    if (!GetCursorPos(out at) || at.x != x || at.y != y) SetCursorPos(x, y);
                    Thread.Sleep(500);
                }
            }
            finally { SetThreadDpiAwarenessContext(dpi); }
        });
    }

    // Режим из базы конфигураций Windows: 1 / 2 / 4 / 8 как у Win+P, 0 — не удалось узнать.
    static uint CurrentTopology()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            uint paths, modes, topology;
            if (GetDisplayConfigBufferSizes(QDC_DATABASE_CURRENT, out paths, out modes) != 0) return 0;
            IntPtr pathBuf = Marshal.AllocHGlobal((int)Math.Max(paths, 1) * PathInfoSize);
            IntPtr modeBuf = Marshal.AllocHGlobal((int)Math.Max(modes, 1) * ModeInfoSize);
            try
            {
                int error = QueryDisplayConfig(QDC_DATABASE_CURRENT, ref paths, pathBuf, ref modes, modeBuf, out topology);
                if (error == 0) return topology;
                if (error != ERROR_INSUFFICIENT_BUFFER) return 0;
            }
            finally
            {
                Marshal.FreeHGlobal(pathBuf);
                Marshal.FreeHGlobal(modeBuf);
            }
        }
        return 0;
    }
}
