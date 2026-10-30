// Громкость системного устройства вывода по умолчанию: «без звука» (горит красным, когда звук выключен),
// «тише» / «громче» с полоской уровня на клавише. Работают как медиаклавиши клавиатуры: нажатие — шаг и плашка Windows.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

static class Audio
{
    public const string Group = "audio.";

    const int WM_APPCOMMAND = 0x319;
    const int APPCOMMAND_VOLUME_MUTE = 8, APPCOMMAND_VOLUME_DOWN = 9, APPCOMMAND_VOLUME_UP = 10;
    // Полоска уровня — та же, что дорожка в make_icons.py (BAR).
    static readonly Rectangle Bar = new Rectangle(58, 198, 140, 16);
    static readonly Color BarFill = ColorTranslator.FromHtml("#67e8f9");
    static readonly Color BarMuted = ColorTranslator.FromHtml("#6b6b75");

    // context → имя действия (mute / volume_down / volume_up); под lock(keys).
    static readonly Dictionary<string, string> keys = new Dictionary<string, string>();
    static int shownLevel = -1;
    static bool shownMuted;

    public static void Handle(string ev, string name, string context)
    {
        if (name != "mute" && name != "volume_down" && name != "volume_up") return;
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
                Press(name == "mute" ? APPCOMMAND_VOLUME_MUTE : name == "volume_up" ? APPCOMMAND_VOLUME_UP : APPCOMMAND_VOLUME_DOWN);
                ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(100); Refresh(true); });
                break;
        }
    }

    // Та же команда, что у медиаклавиш клавиатуры: шаг считает сама Windows и показывает свою плашку.
    // Шлём сразу оболочке, а не клавишей: игру в фокусе это не задевает, и она не может проглотить нажатие.
    static void Press(int command)
    {
        IntPtr shell = FindWindow("Shell_TrayWnd", null);
        if (shell == IntPtr.Zero) { Program.Log("Audio: Shell_TrayWnd not found"); return; }
        PostMessage(shell, WM_APPCOMMAND, shell, (IntPtr)(command << 16));
    }

    public static void Refresh(bool force)
    {
        int level = -1;
        bool muted = false;
        WithVolume(v =>
        {
            float l;
            v.GetMasterVolumeLevelScalar(out l);
            v.GetMute(out muted);
            level = (int)Math.Round(l * 100);
        });
        lock (keys)
        {
            if (keys.Count == 0 || (!force && level == shownLevel && muted == shownMuted)) return;
            shownLevel = level;
            shownMuted = muted;
            if (Program.Debug) Program.Log("Audio: level " + level + ", muted " + muted);
            string[] images = { null, null };
            foreach (var key in keys)
            {
                if (key.Value == "mute") { Program.SetState(key.Key, muted ? 1 : 0); continue; }
                int i = key.Value == "volume_up" ? 1 : 0;
                if (images[i] == null) images[i] = LevelImage(key.Value, level, muted);
                if (images[i] != null) Program.SetImage(key.Key, images[i]);
            }
        }
    }

    // Картинка клавиши ± с заполненной полоской уровня, data URI для setImage.
    static string LevelImage(string name, int level, bool muted)
    {
        string path = Path.Combine(Program.Dir, "images", name + ".png");
        if (level < 0 || !File.Exists(path)) return null;
        using (var image = new Bitmap(path))
        using (var g = Graphics.FromImage(image))
        using (var brush = new SolidBrush(muted ? BarMuted : BarFill))
        using (var output = new MemoryStream())
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int width = Math.Max(Bar.Height, Bar.Width * level / 100);
            if (level > 0) g.FillPath(brush, Pill(new Rectangle(Bar.X, Bar.Y, width, Bar.Height)));
            image.Save(output, ImageFormat.Png);
            return "data:image/png;base64," + Convert.ToBase64String(output.ToArray());
        }
    }

    static GraphicsPath Pill(Rectangle r)
    {
        var path = new GraphicsPath();
        int d = r.Height;
        path.AddArc(r.X, r.Y, d, d, 90, 180);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 180);
        path.CloseFigure();
        return path;
    }

    // Устройство по умолчанию берём заново каждый раз: вывод переключается между ТВ и наушниками.
    static void WithVolume(Action<IAudioEndpointVolume> action)
    {
        IMMDeviceEnumerator enumerator = null;
        IMMDevice device = null;
        object volume = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out device);
            Guid iid = typeof(IAudioEndpointVolume).GUID;
            device.Activate(ref iid, 1 /* CLSCTX_INPROC_SERVER */, IntPtr.Zero, out volume);
            action((IAudioEndpointVolume)volume);
        }
        catch (Exception e) { Program.Log("Audio: " + e.Message); }
        finally
        {
            if (volume != null) Marshal.ReleaseComObject(volume);
            if (device != null) Marshal.ReleaseComObject(device);
            if (enumerator != null) Marshal.ReleaseComObject(enumerator);
        }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints();
        void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        void Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        void RegisterControlChangeNotify();
        void UnregisterControlChangeNotify();
        void GetChannelCount();
        void SetMasterVolumeLevel();
        void SetMasterVolumeLevelScalar(float level, [MarshalAs(UnmanagedType.LPStruct)] Guid context);
        void GetMasterVolumeLevel();
        void GetMasterVolumeLevelScalar(out float level);
        void SetChannelVolumeLevel();
        void SetChannelVolumeLevelScalar();
        void GetChannelVolumeLevel();
        void GetChannelVolumeLevelScalar();
        void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, [MarshalAs(UnmanagedType.LPStruct)] Guid context);
        void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, int message, IntPtr wparam, IntPtr lparam);
}
