// RGB-кольцо деки D6: один статичный цвет вместо заводского мигания.
// Кнопка «Подсветка» листает палитру (последний пункт — выключить); на клавише кольцо из 18 точек текущего цвета.
// Выбор хранится в %APPDATA%\megarohas_deck\lights.json и ставится заново при подключении деки и после сна.
// Команду подсмотрел в плагине магазина RGB Control (com.mirabox.streamdock.lampControlD6): sendToDevice
// с массивом [65, 100, 18, r,g,b × 18] — 18 светодиодов. Софт принимает её только с context живой кнопки
// плагина на этой деке (по нему ищет профиль), поэтому цвет ставится, когда появляется любая наша кнопка.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Web.Script.Serialization;

static class Lights
{
    public const string Group = "lights.";
    const int Leds = 18;

    static readonly int[][] Palette =
    {
        new[] { 255, 255, 255 }, // белый — по умолчанию
        new[] { 0, 170, 255 },   // голубой лёд
        new[] { 20, 60, 255 },   // синий
        new[] { 130, 30, 255 },  // фиолетовый
        new[] { 255, 30, 150 },  // розовый
        new[] { 255, 0, 0 },     // красный
        new[] { 255, 80, 0 },    // оранжевый
        new[] { 255, 160, 0 },   // янтарь
        new[] { 0, 255, 60 },    // зелёный
        new[] { 0, 0, 0 },       // выключено
    };

    static readonly string settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "megarohas_deck", "lights.json");
    static readonly object sync = new object();
    // context любой нашей кнопки → (дека, action): от её имени и шлём команду.
    static readonly Dictionary<string, string[]> anchors = new Dictionary<string, string[]>();
    static readonly HashSet<string> keys = new HashSet<string>();
    static int index = Load();

    public static void Handle(string ev, string name, string context)
    {
        if (name != "color") return;
        switch (ev)
        {
            case "willAppear":
                lock (sync) keys.Add(context);
                ShowKey(context);
                break;
            case "willDisappear":
                lock (sync) keys.Remove(context);
                break;
            case "keyDown":
                lock (sync) index = (index + 1) % Palette.Length;
                Save();
                Apply();
                lock (sync) foreach (var key in keys) ShowKey(key);
                break;
        }
    }

    // Появилась любая наша кнопка. Первая на деке (запуск софта, подключение деки) — ставим цвет сразу
    // и ещё раз чуть позже: при подключении софт может сам выставить заводской эффект.
    public static void KeyAppeared(string device, string action, string context)
    {
        bool first;
        lock (sync)
        {
            first = true;
            foreach (var a in anchors.Values) if (a[0] == device) first = false;
            anchors[context] = new[] { device, action };
        }
        if (!first) return;
        Apply();
        ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(3000); Apply(); });
    }

    public static void KeyDisappeared(string context)
    {
        lock (sync) anchors.Remove(context);
    }

    public static void Apply()
    {
        int[] color;
        var targets = new Dictionary<string, KeyValuePair<string, string[]>>();  // дека → (context, (дека, action))
        lock (sync)
        {
            color = Palette[index];
            foreach (var a in anchors)
                if (!targets.ContainsKey(a.Value[0])) targets[a.Value[0]] = a;
        }
        var leds = new List<int> { 65, 100, Leds };
        for (int i = 0; i < Leds; i++) leds.AddRange(color);
        foreach (var target in targets)
        {
            Program.Send(new
            {
                @event = "sendToDevice",
                action = target.Value.Value[1],
                context = target.Value.Key,
                payload = new
                {
                    device = new { vid = 12610, pid = 7, serialNumber = "" },  // AmpliGame D6
                    controlType = "lampControl",
                    lampType = "",
                    lampControlArray = leds,
                    deviceID = target.Key,
                    Dependence = "device",
                },
            });
        }
    }

    static void ShowKey(string context)
    {
        int[] color;
        lock (sync) color = Palette[index];
        Program.SetImage(context, KeyImage(color));
    }

    // Тёмная плитка с кольцом из 18 точек цвета подсветки и мягким свечением внутри.
    static string KeyImage(int[] rgb)
    {
        bool off = rgb[0] + rgb[1] + rgb[2] == 0;
        var color = off ? Color.FromArgb(70, 70, 80) : Color.FromArgb(rgb[0], rgb[1], rgb[2]);
        using (var image = new Bitmap(256, 256))
        using (var g = Graphics.FromImage(image))
        using (var output = new MemoryStream())
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Black);
            using (var tile = RoundedRect(new Rectangle(0, 0, 256, 256), 44))
            using (var brush = new SolidBrush(ColorTranslator.FromHtml("#1b1b20")))
                g.FillPath(brush, tile);
            if (!off)
                using (var glow = new GraphicsPath())
                {
                    glow.AddEllipse(48, 48, 160, 160);
                    using (var brush = new PathGradientBrush(glow))
                    {
                        brush.CenterColor = Color.FromArgb(110, color);
                        brush.SurroundColors = new[] { Color.FromArgb(0, color) };
                        g.FillPath(brush, glow);
                    }
                }
            using (var dot = new SolidBrush(color))
                for (int i = 0; i < Leds; i++)
                {
                    double a = 2 * Math.PI * i / Leds - Math.PI / 2;
                    float x = 128 + (float)(84 * Math.Cos(a)), y = 128 + (float)(84 * Math.Sin(a));
                    g.FillEllipse(dot, x - 10, y - 10, 20, 20);
                }
            image.Save(output, ImageFormat.Png);
            return "data:image/png;base64," + Convert.ToBase64String(output.ToArray());
        }
    }

    public static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    static int Load()
    {
        try
        {
            var settings = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(settingsPath));
            int i = Convert.ToInt32(settings["index"]);
            if (i >= 0 && i < Palette.Length) return i;
        }
        catch { }
        return 0;
    }

    static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath));
            int i;
            lock (sync) i = index;
            File.WriteAllText(settingsPath, new JavaScriptSerializer().Serialize(new { index = i }));
        }
        catch (Exception e) { Program.Log("Lights: " + e.Message); }
    }
}
