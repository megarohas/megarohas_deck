// Мелочи для Windows: «Рабочий стол» — свернуть все окна по-настоящему (как Win+M, а не переключатель Win+D,
// который разворачивает всё обратно) и «Не спать» — запуск/закрытие своей утилиты NeSpat.exe: пока она открыта,
// экран не гаснет и ПК не засыпает. Кнопка горит, пока NeSpat запущена.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

static class Tools
{
    public const string Group = "tools.";
    const string AwakeProcess = "NeSpat";
    static readonly string[] AwakeExe =
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "NeSpat", "NeSpat.exe"),
        @"S:\Claude\StayAwake\NeSpat.exe",
    };

    static readonly HashSet<string> awakeKeys = new HashSet<string>();
    static int shown = -1;

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
        return running.Length > 0;
    }

    // Запускаем свёрнутой, чтобы окно не выскакивало поверх игры.
    static void StartAwake(string context)
    {
        foreach (var exe in AwakeExe)
        {
            if (!File.Exists(exe)) continue;
            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exe),
                WindowStyle = ProcessWindowStyle.Minimized,
            });
            return;
        }
        Program.Log("Tools: NeSpat.exe not found");
        Program.ShowAlert(context);
    }

    // Закрываем окно штатно — NeSpat сама снимает запрет сна; если не закрылась, Windows снимет его вместе с процессом.
    static void StopAwake()
    {
        foreach (var p in Process.GetProcessesByName(AwakeProcess))
            using (p)
            {
                if (!p.CloseMainWindow() || !p.WaitForExit(2000)) p.Kill();
            }
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
    }
}
