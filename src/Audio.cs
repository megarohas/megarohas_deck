// Громкость системного устройства вывода по умолчанию: «без звука» (горит красным, когда звук выключен),
// «тише» / «громче» с полоской уровня на клавише. Работают как медиаклавиши клавиатуры: нажатие — шаг и плашка Windows.
// Здесь же выход звука, который идёт за экраном (FollowScreen).
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
    const int FollowChecks = 20;  // секунд проверять выход звука после смены экрана
    const int DigitalAudioDisplayDevice = 9;  // EndpointFormFactor: HDMI / DisplayPort
    static readonly PROPERTYKEY EnumeratorName = new PROPERTYKEY { fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), pid = 24 };
    static readonly PROPERTYKEY FriendlyName = new PROPERTYKEY { fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), pid = 14 };
    static readonly PROPERTYKEY FormFactor = new PROPERTYKEY { fmtid = new Guid("1DA5D803-D492-4EDD-8C23-E0C0FFEE7F0E"), pid = 0 };
    // Полоска уровня — та же, что дорожка в make_icons.py (BAR).
    static readonly Rectangle Bar = new Rectangle(58, 198, 140, 16);
    static readonly Color BarFill = ColorTranslator.FromHtml("#67e8f9");
    static readonly Color BarMuted = ColorTranslator.FromHtml("#6b6b75");

    // context → имя действия (mute / volume_down / volume_up); под lock(keys).
    static readonly Dictionary<string, string> keys = new Dictionary<string, string>();
    static int shownLevel = -1;
    static bool shownMuted;
    static int followRequest;  // номер последнего запроса FollowScreen: новый запрос отменяет прежний

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

    // Звук идёт за экраном. Свой выход (HDMI / DisplayPort) есть у экрана, только пока тот включён, поэтому имена сравнивать
    // не нужно, да и не выйдет: монитор DEXP DQ27H1 называет себя в звуке Q2713S.
    // Подключённые Bluetooth-наушники важнее экрана: звук остаётся в них. Виртуальный выход NVIDIA Broadcast (тот же тип, что
    // у HDMI, но шина ROOT) настоящим не считаем: если он перехватил вывод, звук ушёл бы на устройство, выбранное в Broadcast,
    // а оно могло пропасть вместе с прежним экраном.
    // После смены экрана старый выход исчезает и новый появляется не сразу, а Windows тем временем выбирает что попало,
    // поэтому проверяем несколько раз.
    public static void FollowScreen()
    {
        int request = Interlocked.Increment(ref followRequest);
        ThreadPool.QueueUserWorkItem(_ =>
        {
            for (int i = 0; i < FollowChecks && request == Thread.VolatileRead(ref followRequest); i++)
            {
                try { PickOutput(); }
                catch (Exception e) { Program.Log("Audio: pick output: " + e.Message); }
                Thread.Sleep(1000);
            }
        });
    }

    static void PickOutput()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        IMMDevice defaultDevice = null;
        IMMDeviceCollection active = null;
        try
        {
            string current = null;
            try
            {
                enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out defaultDevice);
                defaultDevice.GetId(out current);
            }
            catch (COMException) { }  // выходов сейчас нет вообще
            enumerator.EnumAudioEndpoints(0 /* eRender */, 1 /* DEVICE_STATE_ACTIVE */, out active);
            int count;
            active.GetCount(out count);
            string headphones = null, screen = null;
            var names = new Dictionary<string, string>();
            for (int i = 0; i < count; i++)
            {
                IMMDevice device;
                active.Item(i, out device);
                try
                {
                    string id, bus = Property(device, EnumeratorName) as string ?? "";
                    device.GetId(out id);
                    names[id] = Property(device, FriendlyName) as string;
                    if (bus.StartsWith("BTH"))
                    {
                        if (id == current) return;
                        if (headphones == null || bus == "BTHENUM") headphones = id;  // BTHHFENUM — режим гарнитуры, звук в нём хуже
                    }
                    else if (bus == "HDAUDIO" && Equals(Property(device, FormFactor), DigitalAudioDisplayDevice))
                    {
                        if (screen == null || id == current) screen = id;  // экранов может быть два («расширить»): текущий не трогаем
                    }
                }
                finally { Marshal.ReleaseComObject(device); }
            }
            string target = headphones ?? screen;
            if (target == null || target == current) return;
            var policy = (IPolicyConfig)new PolicyConfigClient();
            try
            {
                for (int role = 0; role < 3; role++) policy.SetDefaultEndpoint(target, role);  // eConsole, eMultimedia, eCommunications
            }
            finally { Marshal.ReleaseComObject(policy); }
            Program.Log("Audio: output -> " + names[target]);
        }
        finally
        {
            if (active != null) Marshal.ReleaseComObject(active);
            if (defaultDevice != null) Marshal.ReleaseComObject(defaultDevice);
            Marshal.ReleaseComObject(enumerator);
        }
    }

    // Строка или число из свойств устройства, null — если свойства нет или оно другого типа.
    static object Property(IMMDevice device, PROPERTYKEY key)
    {
        IPropertyStore store;
        device.OpenPropertyStore(0 /* STGM_READ */, out store);
        try
        {
            PROPVARIANT value;
            store.GetValue(ref key, out value);
            try
            {
                if (value.vt == 31 /* VT_LPWSTR */) return Marshal.PtrToStringUni(value.pointer);
                if (value.vt == 19 /* VT_UI4 */) return value.pointer.ToInt32();
                return null;
            }
            finally { PropVariantClear(ref value); }
        }
        finally { Marshal.ReleaseComObject(store); }
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
        void EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        void GetCount(out int count);
        void Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        void Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        void OpenPropertyStore(int access, out IPropertyStore store);
        void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        void GetCount(out int count);
        void GetAt(int index, out PROPERTYKEY key);
        void GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROPERTYKEY { public Guid fmtid; public int pid; }

    [StructLayout(LayoutKind.Sequential)]
    struct PROPVARIANT { public ushort vt, reserved1, reserved2, reserved3; public IntPtr pointer, pointer2; }

    // Открытого API для выхода по умолчанию у Windows нет: IPolicyConfig недокументирован, им пользуется сама панель звука.
    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    class PolicyConfigClient { }

    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        void GetMixFormat();
        void GetDeviceFormat();
        void ResetDeviceFormat();
        void SetDeviceFormat();
        void GetProcessingPeriod();
        void SetProcessingPeriod();
        void GetShareMode();
        void SetShareMode();
        void GetPropertyValue();
        void SetPropertyValue();
        void SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
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
    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PROPVARIANT value);
}
