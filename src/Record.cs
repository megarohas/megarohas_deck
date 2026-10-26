// Кнопка «Запись»: запись экрана через OBS, не открывая его окно.
// Нажатие: если OBS не запущен — запускаем его свёрнутым в трей; ставим сцену deck_record с захватом
// основного экрана (того, что сейчас на 0,0 — монитор или ТВ) и начинаем запись. Повторное нажатие — стоп,
// и OBS возвращается на прежнюю сцену. Пока идёт запись, клавиша красная и на ней тикает время.
// Связь — obs-websocket v5 (встроен в OBS 28+), пароль берётся из его config.json.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

static class Record
{
    public const string Group = "record.";
    const string Scene = "deck_record", Input = "deck_screen";
    static readonly string ObsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "obs-studio", "bin", "64bit");

    enum Status { Idle, Busy, Recording }

    static readonly object sync = new object();
    static readonly HashSet<string> keys = new HashSet<string>();
    static Status status = Status.Idle;
    static DateTime started;
    static string previousScene;  // сцена, на которую вернуть OBS после записи
    static Obs obs;
    static int connecting;

    public static void Handle(string ev, string name, string context)
    {
        if (name != "toggle") return;
        switch (ev)
        {
            case "willAppear":
                lock (sync) keys.Add(context);
                Show();
                break;
            case "willDisappear":
                lock (sync) keys.Remove(context);
                break;
            case "keyDown":
                Status now;
                lock (sync)
                {
                    now = status;
                    if (now == Status.Busy) return;
                    status = Status.Busy;
                }
                Show();
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        if (now == Status.Recording) obs.Call("StopRecord"); else Start();
                        Settle();
                    }
                    catch (Exception e)
                    {
                        Program.Log("Record: " + e.Message);
                        lock (sync) status = now;
                        Show();
                        lock (sync) foreach (var key in keys) Program.ShowAlert(key);
                    }
                });
                break;
        }
    }

    // Раз в секунду: время на клавише и подхват OBS, запущенного вручную (если запись идёт из самого OBS).
    public static void Tick()
    {
        lock (sync) if (status == Status.Recording && keys.Count > 0) Show();
        if (Connected() || Process.GetProcessesByName("obs64").Length == 0) return;
        if (Interlocked.Exchange(ref connecting, 1) == 1) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { Connect(); }
            catch { }
            finally { connecting = 0; }
        });
    }

    static void Start()
    {
        if (!Connected())
        {
            if (Process.GetProcessesByName("obs64").Length == 0)
                Process.Start(new ProcessStartInfo(Path.Combine(ObsDir, "obs64.exe"), "--minimize-to-tray --disable-shutdown-check")
                {
                    WorkingDirectory = ObsDir,
                    UseShellExecute = false,
                });
            var deadline = DateTime.Now.AddSeconds(30);
            while (true)
            {
                try { Connect(); break; }
                catch { if (DateTime.Now > deadline) throw; Thread.Sleep(500); }
            }
        }
        if (Bool(obs.Call("GetRecordStatus"), "outputActive")) return;  // уже пишет — просто подхватили

        string current = Str(obs.Call("GetCurrentProgramScene"), "sceneName");
        PrepareScene();
        if (current != Scene)
        {
            obs.Call("SetCurrentProgramScene", new { sceneName = Scene });
            lock (sync) previousScene = current;
        }
        obs.Call("StartRecord");
    }

    // Обычно состояние приходит событием RecordStateChanged; если его нет за 10 с — спрашиваем OBS сами.
    static void Settle()
    {
        for (int i = 0; i < 20; i++)
        {
            lock (sync) if (status != Status.Busy) return;
            Thread.Sleep(500);
        }
        var record = obs.Call("GetRecordStatus");
        lock (sync)
        {
            status = Bool(record, "outputActive") ? Status.Recording : Status.Idle;
            if (status == Status.Recording) started = DateTime.Now.AddMilliseconds(-Convert.ToDouble(record["outputDuration"]));
        }
        Show();
    }

    // Сцена deck_record с одним захватом экрана, растянутым на весь холст; монитор — основной на данный момент.
    static void PrepareScene()
    {
        var scenes = obs.Call("GetSceneList");
        bool hasScene = false;
        foreach (Dictionary<string, object> s in (System.Collections.IEnumerable)scenes["scenes"])
            if (Str(s, "sceneName") == Scene) hasScene = true;
        if (!hasScene) obs.Call("CreateScene", new { sceneName = Scene });
        obs.TryCall("CreateInput", new
        {
            sceneName = Scene,
            inputName = Input,
            inputKind = "monitor_capture",
            inputSettings = new { method = 0, capture_cursor = true },
        });

        var items = obs.Call("GetInputPropertiesListPropertyItems", new { inputName = Input, propertyName = "monitor_id" });
        string monitor = null;
        foreach (Dictionary<string, object> item in (System.Collections.IEnumerable)items["propertyItems"])
        {
            if (monitor == null) monitor = Str(item, "itemValue");
            if (Regex.IsMatch(Str(item, "itemName") ?? "", @"@ 0,0\b")) { monitor = Str(item, "itemValue"); break; }
        }
        if (monitor != null) obs.Call("SetInputSettings", new { inputName = Input, inputSettings = new { monitor_id = monitor } });

        var video = obs.Call("GetVideoSettings");
        int id = Convert.ToInt32(obs.Call("GetSceneItemId", new { sceneName = Scene, sourceName = Input })["sceneItemId"]);
        obs.Call("SetSceneItemTransform", new
        {
            sceneName = Scene,
            sceneItemId = id,
            sceneItemTransform = new
            {
                positionX = 0, positionY = 0, alignment = 5,  // левый верхний угол
                boundsType = "OBS_BOUNDS_SCALE_INNER", boundsAlignment = 0,
                boundsWidth = Convert.ToInt32(video["baseWidth"]), boundsHeight = Convert.ToInt32(video["baseHeight"]),
            },
        });
    }

    static bool Connected()
    {
        var o = obs;
        return o != null && o.Open;
    }

    static void Connect()
    {
        var o = new Obs();
        o.RecordStateChanged = OnRecordState;
        o.Closed = () =>
        {
            lock (sync) if (status != Status.Idle) status = Status.Idle;
            Show();
        };
        o.Connect(ReadPassword());
        obs = o;
        var record = o.Call("GetRecordStatus");
        lock (sync)
        {
            if (Bool(record, "outputActive"))
            {
                status = Status.Recording;
                started = DateTime.Now.AddMilliseconds(-Convert.ToDouble(record["outputDuration"]));
            }
            else if (status == Status.Recording) status = Status.Idle;
        }
        Show();
    }

    static void OnRecordState(Dictionary<string, object> data)
    {
        string state = Str(data, "outputState");
        string restore = null;
        lock (sync)
        {
            if (state == "OBS_WEBSOCKET_OUTPUT_STARTED") { status = Status.Recording; started = DateTime.Now; }
            else if (state == "OBS_WEBSOCKET_OUTPUT_STOPPED")
            {
                status = Status.Idle;
                restore = previousScene;
                previousScene = null;
            }
            else status = Status.Busy;
        }
        Show();
        if (restore != null)
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { obs.Call("SetCurrentProgramScene", new { sceneName = restore }); }
                catch (Exception e) { Program.Log("Record: restore scene: " + e.Message); }
            });
    }

    static string ReadPassword()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "obs-studio", "plugin_config", "obs-websocket", "config.json");
        var config = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
        return Str(config, "server_password") ?? "";
    }

    static void Show()
    {
        string image;
        lock (sync)
        {
            if (keys.Count == 0) return;
            image = KeyImage(status, DateTime.Now - started);
            foreach (var key in keys) Program.SetImage(key, image);
        }
    }

    // Простой: тёмная плитка с красной кнопкой записи. Запись: красная плитка, белая точка и время.
    static string KeyImage(Status s, TimeSpan elapsed)
    {
        if (s == Status.Idle)
        {
            string path = Path.Combine(Program.Dir, "images", "record_off.png");
            return File.Exists(path) ? "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(path)) : null;
        }
        using (var image = new Bitmap(256, 256))
        using (var g = Graphics.FromImage(image))
        using (var output = new MemoryStream())
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Black);
            using (var tile = RoundedRect(new Rectangle(0, 0, 256, 256), 44))
            using (var brush = new LinearGradientBrush(new Point(0, 0), new Point(256, 256),
                ColorTranslator.FromHtml("#fb7185"), ColorTranslator.FromHtml("#dc2626")))
                g.FillPath(brush, tile);
            using (var white = new SolidBrush(Color.White))
            {
                g.FillEllipse(white, 98, 46, 60, 60);
                string text = s == Status.Busy ? "···"
                    : elapsed.TotalHours >= 1 ? elapsed.ToString(@"h\:mm\:ss") : elapsed.ToString(@"mm\:ss");
                using (var font = new Font("Bahnschrift", text.Length > 5 ? 40 : 52, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString(text, font, white, new RectangleF(0, 120, 256, 100), format);
            }
            image.Save(output, ImageFormat.Png);
            return "data:image/png;base64," + Convert.ToBase64String(output.ToArray());
        }
    }

    static GraphicsPath RoundedRect(Rectangle r, int radius)
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

    static string Str(Dictionary<string, object> d, string key)
    {
        object v;
        return d != null && d.TryGetValue(key, out v) ? v as string : null;
    }

    static bool Bool(Dictionary<string, object> d, string key)
    {
        object v;
        return d != null && d.TryGetValue(key, out v) && v is bool && (bool)v;
    }

    // Минимальный клиент obs-websocket v5: авторизация, запросы с ожиданием ответа, событие RecordStateChanged.
    class Obs
    {
        const int EventSubscriptionOutputs = 1 << 6;
        readonly ClientWebSocket ws = new ClientWebSocket();
        readonly JavaScriptSerializer json = new JavaScriptSerializer();
        readonly object sendLock = new object();
        readonly Dictionary<string, Pending> pending = new Dictionary<string, Pending>();
        readonly ManualResetEvent identified = new ManualResetEvent(false);
        string password;
        int nextId;

        public Action<Dictionary<string, object>> RecordStateChanged;
        public Action Closed;
        public bool Open { get { return ws.State == WebSocketState.Open; } }

        class Pending
        {
            public readonly ManualResetEvent Done = new ManualResetEvent(false);
            public Dictionary<string, object> Response;
        }

        public void Connect(string password)
        {
            this.password = password;
            if (!ws.ConnectAsync(new Uri("ws://127.0.0.1:4455"), CancellationToken.None).Wait(3000))
                throw new Exception("obs-websocket: no connection");
            var reader = new Thread(ReadLoop) { IsBackground = true };
            reader.Start();
            if (!identified.WaitOne(5000)) throw new Exception("obs-websocket: not identified (password?)");
        }

        public Dictionary<string, object> Call(string type, object data = null)
        {
            var response = TryCall(type, data);
            var status = (Dictionary<string, object>)response["requestStatus"];
            if (!(bool)status["result"])
                throw new Exception("obs-websocket: " + type + " failed: " + status["code"] + " " + Str(status, "comment"));
            object payload;
            return response.TryGetValue("responseData", out payload) ? payload as Dictionary<string, object> : null;
        }

        public Dictionary<string, object> TryCall(string type, object data = null)
        {
            string id = Interlocked.Increment(ref nextId).ToString();
            var wait = new Pending();
            lock (pending) pending[id] = wait;
            Send(6, new Dictionary<string, object> { { "requestType", type }, { "requestId", id }, { "requestData", data ?? new { } } });
            bool done = wait.Done.WaitOne(10000);
            lock (pending) pending.Remove(id);
            if (!done) throw new Exception("obs-websocket: " + type + " timed out");
            return wait.Response;
        }

        void Send(int op, object d)
        {
            var bytes = Encoding.UTF8.GetBytes(json.Serialize(new { op = op, d = d }));
            lock (sendLock) ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None).Wait();
        }

        void ReadLoop()
        {
            var buffer = new byte[64 * 1024];
            var message = new MemoryStream();
            try
            {
                while (ws.State == WebSocketState.Open)
                {
                    var r = ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None).Result;
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    message.Write(buffer, 0, r.Count);
                    if (!r.EndOfMessage) continue;
                    var msg = json.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(message.ToArray()));
                    message.SetLength(0);
                    Handle(Convert.ToInt32(msg["op"]), (Dictionary<string, object>)msg["d"]);
                }
            }
            catch (Exception e) { Program.Log("obs-websocket: " + e.Message); }
            if (Closed != null) Closed();
        }

        void Handle(int op, Dictionary<string, object> d)
        {
            switch (op)
            {
                case 0:  // Hello
                    var identify = new Dictionary<string, object> { { "rpcVersion", 1 }, { "eventSubscriptions", EventSubscriptionOutputs } };
                    object auth;
                    if (d.TryGetValue("authentication", out auth))
                    {
                        var a = (Dictionary<string, object>)auth;
                        string secret = Sha256(password + a["salt"]);
                        identify["authentication"] = Sha256(secret + a["challenge"]);
                    }
                    Send(1, identify);
                    break;
                case 2:  // Identified
                    identified.Set();
                    break;
                case 5:  // Event
                    if (Str(d, "eventType") == "RecordStateChanged" && RecordStateChanged != null)
                        RecordStateChanged((Dictionary<string, object>)d["eventData"]);
                    break;
                case 7:  // RequestResponse
                    Pending wait;
                    lock (pending) pending.TryGetValue(Str(d, "requestId") ?? "", out wait);
                    if (wait != null) { wait.Response = d; wait.Done.Set(); }
                    break;
            }
        }

        static string Sha256(string text)
        {
            using (var sha = SHA256.Create())
                return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(text)));
        }
    }
}
