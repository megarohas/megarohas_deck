// Кнопка «VPN» для Hiddify в режиме системного прокси: VPN включён, когда прокси Windows указывает на ядро
// Hiddify (127.0.0.1:12334) и оно слушает порт. Снаружи Hiddify не управляется (ни API, ни CLI, deep-link только
// для импорта), поэтому кнопка переключает сам системный прокси: выкл — трафик идёт напрямую, вкл — снова через
// ядро Hiddify. Если ядро не запущено, нажатие открывает окно Hiddify, чтобы подключиться там.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32;

static class Vpn
{
    public const string Group = "vpn.";
    const int Port = 12334;  // mixed inbound Hiddify по умолчанию
    const string Proxy = "127.0.0.1:12334";
    const string SettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    const int INTERNET_OPTION_SETTINGS_CHANGED = 39, INTERNET_OPTION_REFRESH = 37;
    static readonly string HiddifyExe = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Hiddify", "Hiddify.exe");

    [DllImport("wininet.dll", SetLastError = true)]
    static extern bool InternetSetOption(IntPtr internet, int option, IntPtr buffer, int length);

    static readonly HashSet<string> keys = new HashSet<string>();
    static int shown = -1;

    public static void Handle(string ev, string name, string context)
    {
        if (name != "toggle") return;
        switch (ev)
        {
            case "willAppear":
                lock (keys) keys.Add(context);
                Refresh(true);
                break;
            case "willDisappear":
                lock (keys) keys.Remove(context);
                break;
            case "keyDown":
                Toggle(context);
                Refresh(true);
                break;
            case "keyUp":
                Refresh(true);
                break;
        }
    }

    static void Toggle(string context)
    {
        if (On()) { SetProxy(false); return; }
        if (CoreListening()) { SetProxy(true); return; }
        // Ядро не запущено: подключиться можно только в самом Hiddify — показываем его окно.
        if (File.Exists(HiddifyExe)) Process.Start(new ProcessStartInfo(HiddifyExe) { UseShellExecute = true });
        else Program.ShowAlert(context);
    }

    public static void Refresh(bool force)
    {
        int state = On() ? 1 : 0;
        lock (keys)
        {
            if (keys.Count == 0 || (!force && state == shown)) return;
            shown = state;
            foreach (var key in keys) Program.SetState(key, state);
        }
    }

    static bool On()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(SettingsKey))
        {
            if (key == null) return false;
            object enable = key.GetValue("ProxyEnable");
            string server = key.GetValue("ProxyServer") as string ?? "";
            return enable is int && (int)enable == 1 && server.Contains(Proxy) && CoreListening();
        }
    }

    // Таблица слушателей на этой сборке Windows иногда теряет порт Hiddify (состояние 0), поэтому, если его там нет,
    // пробуем подключиться — не чаще раза в 10 с, чтобы не сорить соединениями в лог Hiddify.
    static DateTime probed;
    static bool probeResult;

    static bool CoreListening()
    {
        if (IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(ep => ep.Port == Port)) return true;
        if ((DateTime.Now - probed).TotalSeconds < 10) return probeResult;
        probed = DateTime.Now;
        try
        {
            using (var client = new TcpClient())
                probeResult = client.ConnectAsync("127.0.0.1", Port).Wait(300) && client.Connected;
        }
        catch { probeResult = false; }
        return probeResult;
    }

    // Меняем только ProxyEnable (и адрес, если его кто-то стёр); список исключений Hiddify не трогаем.
    static void SetProxy(bool enable)
    {
        using (var key = Registry.CurrentUser.OpenSubKey(SettingsKey, true))
        {
            if (enable && !(key.GetValue("ProxyServer") as string ?? "").Contains(Proxy))
                key.SetValue("ProxyServer", "http://" + Proxy, RegistryValueKind.String);
            key.SetValue("ProxyEnable", enable ? 1 : 0, RegistryValueKind.DWord);
        }
        InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
    }
}
