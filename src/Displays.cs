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
        { "internal", 0x1 },  // DISPLAYCONFIG_TOPOLOGY_INTERNAL — монитор DEXP
        { "clone", 0x2 },
        { "extend", 0x4 },
        { "external", 0x8 },  // DISPLAYCONFIG_TOPOLOGY_EXTERNAL — ТВ
    };

    [DllImport("user32.dll")] static extern int SetDisplayConfig(uint numPaths, IntPtr paths, uint numModes, IntPtr modes, uint flags);
    [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);
    [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint numPaths, IntPtr paths, ref uint numModes, IntPtr modes, out uint topology);

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
                break;
            case "keyUp":
                // Софт сам перещёлкивает состояние двухрежимной кнопки после нажатия — возвращаем правду.
                ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(250); Refresh(true); });
                break;
        }
    }

    public const uint External = 0x8;

    // Перейти на режим, если он ещё не включён. -1 — не вышло (ТВ отключён и т. п.), 0 — уже был, 1 — переключили.
    public static int SwitchTo(uint topology)
    {
        if (CurrentTopology() == topology) return 0;
        int error = SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, SDC_APPLY | topology);
        if (error != 0) Program.Log("SetDisplayConfig(" + topology + ") = " + error);
        Refresh(true);
        return error == 0 ? 1 : -1;
    }

    public static void Refresh(bool force)
    {
        uint current = CurrentTopology();
        lock (keys)
        {
            if (!force && current == shown) return;
            shown = current;
            foreach (var key in keys) Program.SetState(key.Key, key.Value == current ? 1 : 0);
        }
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
