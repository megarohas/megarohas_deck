// Мелочи для Windows: «Рабочий стол» — свернуть все окна по-настоящему (как Win+M, а не переключатель Win+D,
// который разворачивает всё обратно) и «Не спать» — пока включено, экран не гаснет и ПК не засыпает.
// «Не спать» запускает/закрывает утилиту NeSpat.exe (megarohas_windows_tweaks/stayawake), если она установлена
// в %LOCALAPPDATA%\Programs\NeSpat; иначе плагин сам держит запрет сна. Кнопка горит, пока запрет действует.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

static class Tools
{
    public const string Group = "tools.";
    const string AwakeProcess = "NeSpat";
    static readonly string AwakeExe = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "NeSpat", "NeSpat.exe");

    const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x1, ES_DISPLAY_REQUIRED = 0x2;
    [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);

    static readonly HashSet<string> awakeKeys = new HashSet<string>();
    static int shown = -1;
    // Встроенный запрет сна: SetThreadExecutionState действует, пока жив поток, который его выставил.
    static ManualResetEvent builtInStop;

    public static void Handle(string ev, string name, string context)
    {
        if (name == "desktop" && ev == "keyDown")
        {
            MinimizeAll();
        }
        else if (name == "awake")
        {
            switch (ev)
            {
                case "willAppear":
                    lock (awakeKeys) awakeKeys.Add(context);
                    Refresh(true);
                    break;
                case "willDisappear":
                    lock (awakeKeys) awakeKeys.Remove(context);
                    break;
                case "keyDown":
                    if (Awake()) StopAwake(); else StartAwake(context);
                    Refresh(true);
                    break;
                case "keyUp":
                    ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(500); Refresh(true); });
                    break;
            }
        }
    }

    public static void Refresh(bool force)
    {
        int state = Awake() ? 1 : 0;
        lock (awakeKeys)
        {
            if (awakeKeys.Count == 0 || (!force && state == shown)) return;
            shown = state;
            foreach (var key in awakeKeys) Program.SetState(key, state);
        }
    }

    static bool Awake()
    {
        var running = Process.GetProcessesByName(AwakeProcess);
        foreach (var p in running) p.Dispose();
        return running.Length > 0 || builtInStop != null;
    }

    // NeSpat запускаем свёрнутой, чтобы окно не выскакивало поверх игры.
    static void StartAwake(string context)
    {
        if (File.Exists(AwakeExe))
        {
            Process.Start(new ProcessStartInfo(AwakeExe)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(AwakeExe),
                WindowStyle = ProcessWindowStyle.Minimized,
            });
            return;
        }
        var stop = new ManualResetEvent(false);
        builtInStop = stop;
        var thread = new Thread(() =>
        {
            SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED);
            stop.WaitOne();
            SetThreadExecutionState(ES_CONTINUOUS);
        });
        thread.IsBackground = true;
        thread.Start();
    }

    // Закрываем окно штатно — NeSpat сама снимает запрет сна; если не закрылась, Windows снимет его вместе с процессом.
    static void StopAwake()
    {
        foreach (var p in Process.GetProcessesByName(AwakeProcess))
            using (p)
            {
                if (!p.CloseMainWindow() || !p.WaitForExit(2000)) p.Kill();
            }
        var stop = builtInStop;
        builtInStop = null;
        if (stop != null) stop.Set();
    }

    // Shell.Application.MinimizeAll — то же, что Win+M. Через COM, а не нажатием клавиш: Win+M перехватывает DisplayHotkeys.
    public static void MinimizeAll()
    {
        var thread = new Thread(() =>
        {
            try
            {
                var shellType = Type.GetTypeFromProgID("Shell.Application");
                object shell = Activator.CreateInstance(shellType);
                shellType.InvokeMember("MinimizeAll", System.Reflection.BindingFlags.InvokeMethod, null, shell, null);
                System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
            }
            catch (Exception e) { Program.Log("Tools: MinimizeAll: " + e.Message); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(2000);
    }
}
