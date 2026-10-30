// Кнопка «Запись»: запись экрана через OBS, не открывая его окно. OBS в фоне не висит: нажатие запускает его
// (свёрнутым в трей, сразу на сцене deck_record с захватом основного экрана — того, что сейчас на 0,0, монитор или
// ТВ), и запись начинается. Повторное нажатие — стоп, и запущенный плагином OBS закрывается (если его окно так и не
// открыли). Открытый вручную OBS плагин только использует: пишет на deck_record и возвращает прежнюю сцену.
// Пока идёт запись, клавиша красная и на ней тикает время.
// Связь — obs-websocket v5 (встроен в OBS 28+), пароль берётся из его config.json.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Management;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
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
    static bool finishing;        // запись кончилась, OBS закрывается / возвращается на прежнюю сцену
    static Obs obs;
    static int connecting;
    static readonly object connectLock = new object();

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
                    if (now == Status.Busy || finishing) return;
                    status = Status.Busy;
                }
                Show();
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    bool failed = false, finish;
                    try
                    {
                        if (now == Status.Recording) obs.Call("StopRecord"); else Start();
                        Settle();
                    }
                    catch (Exception e)
                    {
                        Program.Log("Record: " + e.Message);
                        failed = true;
                    }
                    lock (sync)
                    {
                        if (failed) status = now;
                        // запись так и не началась — не оставляем зря запущенный OBS
                        finish = now != Status.Recording && status == Status.Idle && !finishing;
                        if (finish) finishing = true;
                    }
                    Show();
                    if (failed) lock (sync) foreach (var key in keys) Program.ShowAlert(key);
                    if (finish) Finish();
                });
                break;
        }
    }

    // PID того OBS, связь с которым оборвалась: он закрывается (закрытие у OBS занимает до пары минут) или в нём
    // выключили websocket. К нему не переподключаемся — попытки подключения не дают ему закрыться.
    static int deadPid;
    static DateTime lastAttempt;

    // Раз в секунду: время на клавише и подхват OBS, запущенного вручную (если запись идёт из самого OBS).
    public static void Tick()
    {
        lock (sync)
        {
            if (status == Status.Recording && keys.Count > 0) Show();
            if (status == Status.Busy || finishing) return;  // OBS сейчас запускает или закрывает сама кнопка
        }
        if (Connected()) return;
        int pid = ObsPid();
        if (pid == 0 || pid == deadPid || (DateTime.Now - lastAttempt).TotalSeconds < 5) return;
        if (Interlocked.Exchange(ref connecting, 1) == 1) return;
        lastAttempt = DateTime.Now;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { Connect(); }
            catch { }
            finally { connecting = 0; }
        });
    }

    // PID OBS, который запустил сам плагин (0 — такого нет): его закрываем после записи.
    static int launchedPid;

    static int ObsPid()
    {
        var running = Process.GetProcessesByName("obs64");
        int pid = running.Length > 0 ? running[0].Id : 0;
        foreach (var p in running) p.Dispose();
        return pid;
    }

    // OBS 32 убрал флаг --disable-shutdown-check, а после аварийного закрытия молча ждёт ответа в окне «безопасный
    // режим». Отметки о запуске лежат в .sentinel — пока OBS не запущен, это остатки прошлого сеанса
    // (run_*, см. frontend/utility/CrashHandler.cpp), удаляем их, и окно не появляется.
    // Сам OBS запускаем вне Job (Program.Launch): запущенный напрямую, он погибал бы вместе с софтом деки (и снова «аварийно»).
    // startScene — сцена, с которой OBS стартует (--scene): сразу deck_record, чтобы при запуске не ожила камера
    // из сцены, на которой OBS закрывали в прошлый раз.
    static int LaunchObs(string startScene)
    {
        string sentinel = Path.Combine(AppData, "obs-studio", ".sentinel");
        if (Directory.Exists(sentinel))
            foreach (var file in Directory.GetFiles(sentinel, "run_*"))
                try { File.Delete(file); }
                catch (Exception e) { Program.Log("Record: sentinel: " + e.Message); }
        string exe = Path.Combine(ObsDir, "obs64.exe"), args = "--minimize-to-tray --disable-updater";
        if (startScene != null) args += " --scene \"" + startScene + "\"";
        return Program.Launch(exe, args, ObsDir);
    }

    static readonly string AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    // Файл текущей коллекции сцен: OBS 31+ хранит выбор в user.ini (имя с .json), прежние — в global.ini (без).
    static string CollectionFile()
    {
        string dir = Path.Combine(AppData, "obs-studio");
        foreach (var ini in new[] { "user.ini", "global.ini" })
        {
            string path = Path.Combine(dir, ini);
            if (!File.Exists(path)) continue;
            var m = Regex.Match(File.ReadAllText(path), @"^SceneCollectionFile=(.+?)\s*$", RegexOptions.Multiline);
            if (!m.Success) continue;
            string name = m.Groups[1].Value;
            return Path.Combine(dir, "basic", "scenes", name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? name : name + ".json");
        }
        throw new Exception("no SceneCollectionFile in OBS config");
    }

    // Текущая сцена из сохранённой коллекции OBS (пока он закрыт) и есть ли в ней deck_record.
    static string SavedScene(out bool hasDeckScene)
    {
        hasDeckScene = false;
        try
        {
            var collection = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }
                .Deserialize<Dictionary<string, object>>(File.ReadAllText(CollectionFile()));
            object order;
            if (collection.TryGetValue("scene_order", out order))
                foreach (Dictionary<string, object> s in (System.Collections.IEnumerable)order)
                    if (Str(s, "name") == Scene) hasDeckScene = true;
            return Str(collection, "current_program_scene") ?? Str(collection, "current_scene");
        }
        catch (Exception e)
        {
            Program.Log("Record: saved scene: " + e.Message);
            return null;
        }
    }

    // Запись кончилась или так и не началась (finishing уже выставлен под sync). OBS, запущенный плагином, закрываем,
    // а прежнюю сцену возвращаем в его файле коллекции: переключи мы её в самом OBS, на ней могла бы ожить
    // вебкамера. Открытый вручную OBS просто возвращаем на прежнюю сцену.
    static void Finish()
    {
        string restore;
        lock (sync) { restore = previousScene; previousScene = null; }
        Show();
        try
        {
            if (CloseLaunchedObs()) { if (restore != null) PutBackScene(restore); }
            else if (restore != null && Connected())
                try { obs.Call("SetCurrentProgramScene", new { sceneName = restore }); }
                catch (Exception e) { Program.Log("Record: restore scene: " + e.Message); }
        }
        finally
        {
            lock (sync) finishing = false;
            Show();
        }
    }

    // Закрыть OBS, запущенный плагином, — штатно (WM_CLOSE главному окну, как «Выход» в меню трея). Если окно OBS
    // открыли — им пользуются, не закрываем. true — OBS закрылся.
    static bool CloseLaunchedObs()
    {
        int pid = Interlocked.Exchange(ref launchedPid, 0);
        if (pid == 0) return false;
        try
        {
            using (var p = Process.GetProcessById(pid))
            {
                IntPtr window = IntPtr.Zero;
                for (int i = 0; i < 20 && !p.HasExited; i++)  // сразу после запуска окна ещё может не быть
                {
                    window = MainWindow(pid);
                    if (window != IntPtr.Zero) break;
                    Thread.Sleep(500);
                }
                if (p.HasExited) return true;
                if (window == IntPtr.Zero)
                {
                    Program.Log("Record: no OBS window, killing it");
                    p.Kill();
                }
                else if (IsWindowVisible(window)) return false;
                else PostMessage(window, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
                if (p.WaitForExit(20000)) return true;
                Program.Log("Record: OBS did not close in 20 s");
                return false;
            }
        }
        catch (ArgumentException) { return true; }  // уже закрыт
        catch (Exception e)
        {
            Program.Log("Record: close OBS: " + e.Message);
            return false;
        }
    }

    // Главное окно OBS — верхнее окно процесса с заголовком «OBS <версия> - …» (в трее оно просто скрыто).
    static IntPtr MainWindow(int pid)
    {
        IntPtr window = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            uint owner;
            GetWindowThreadProcessId(hwnd, out owner);
            if (owner != pid) return true;
            var title = new StringBuilder(256);
            GetWindowText(hwnd, title, title.Capacity);
            if (!title.ToString().StartsWith("OBS ")) return true;
            window = hwnd;
            return false;
        }, IntPtr.Zero);
        return window;
    }

    // OBS, закрытый на deck_record, запомнил её в файле коллекции — ставим там прежнюю сцену, чтобы OBS, открытый
    // потом вручную, был на своей сцене. Правим только эти два значения и только пока OBS не запущен.
    static void PutBackScene(string scene)
    {
        try
        {
            string file = CollectionFile();
            byte[] raw = File.ReadAllBytes(file);
            bool bom = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;
            string text = new UTF8Encoding(false).GetString(raw, bom ? 3 : 0, raw.Length - (bom ? 3 : 0));
            string value = new JavaScriptSerializer().Serialize(scene);
            string patched = Regex.Replace(text, "(\"current_(?:program_)?scene\":\\s*)\"" + Scene + "\"",
                m => m.Groups[1].Value + value);
            if (patched == text || ObsPid() != 0) return;
            string temp = file + ".deck";
            File.WriteAllText(temp, patched, new UTF8Encoding(bom));
            File.Replace(temp, file, null);
        }
        catch (Exception e) { Program.Log("Record: put back scene: " + e.Message); }
    }

    // OBS, запущенный плагином (только он стартует с --disable-updater), — на случай, если софт деки
    // перезапускался, пока OBS работал.
    static bool LaunchedByDeck(int pid)
    {
        try
        {
            using (var search = new ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE ProcessId = " + pid))
                foreach (ManagementObject p in search.Get())
                    using (p) return ((p["CommandLine"] as string) ?? "").Contains("--disable-updater");
        }
        catch (Exception e) { Program.Log("Record: OBS command line: " + e.Message); }
        return false;
    }

    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    // Нажатие на запись, а связи с OBS нет: если OBS не запущен — запускаем; если он как раз закрывается —
    // ждём, пока закроется, и запускаем заново; потом подключаемся.
    static void Start()
    {
        string savedScene = null;  // сцена, на которой OBS закрывали, — вернём её после записи
        if (!Connected())
        {
            var deadline = DateTime.Now.AddSeconds(60);
            int launches = 0;
            while (true)
            {
                int pid = ObsPid();
                if (pid == 0)
                {
                    if (launches++ == 2) throw new Exception("OBS does not start");
                    bool hasDeckScene;
                    savedScene = SavedScene(out hasDeckScene);
                    launchedPid = LaunchObs(hasDeckScene ? Scene : null);
                    deadPid = 0;  // прежний OBS уже закрылся, а номер процесса Windows может выдать снова
                    Thread.Sleep(2000);
                }
                else if (pid != deadPid)
                {
                    try { Connect(); break; }
                    catch { }
                }
                if (DateTime.Now > deadline) throw new Exception("OBS websocket is not reachable");
                Thread.Sleep(500);
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
        else if (savedScene != null && savedScene != Scene)
            lock (sync) previousScene = savedScene;  // OBS стартовал сразу на deck_record
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
        lock (connectLock)
        {
            if (Connected()) return;
            int pid = ObsPid();
            var o = new Obs();
            o.RecordStateChanged = OnRecordState;
            o.Closed = () =>
            {
                if (obs != o) return;  // соединение, которое так и не прижилось
                if (o.Identified) deadPid = pid;
                lock (sync) if (status != Status.Idle) status = Status.Idle;
                Show();
            };
            Dictionary<string, object> record;
            try
            {
                o.Connect(ReadPassword());
                record = WhenReady(o);
            }
            catch
            {
                o.Dispose();  // недоделанное соединение тоже держит OBS при закрытии
                throw;
            }
            obs = o;
            bool ours = launchedPid == pid || (launchedPid == 0 && LaunchedByDeck(pid)), leftover = false;
            if (ours) launchedPid = pid;
            lock (sync)
            {
                if (Bool(record, "outputActive"))
                {
                    status = Status.Recording;
                    started = DateTime.Now.AddMilliseconds(-Convert.ToDouble(record["outputDuration"]));
                }
                else if (status == Status.Recording) status = Status.Idle;
                // OBS от плагина, который ничего не пишет (софт деки перезапускался), — закрываем, в фоне ему не место
                leftover = ours && status == Status.Idle && !finishing;
                if (leftover) finishing = true;
            }
            Show();
            if (leftover) ThreadPool.QueueUserWorkItem(_ => Finish());
        }
    }

    // Websocket у OBS поднимается раньше, чем догружаются сцены, и до тех пор запросы отвечают 207 (NotReady).
    static Dictionary<string, object> WhenReady(Obs o)
    {
        var deadline = DateTime.Now.AddSeconds(30);
        while (true)
            try { return o.Call("GetRecordStatus"); }
            catch (ObsError e)
            {
                if (e.Code != 207 || DateTime.Now > deadline) throw;
                Thread.Sleep(250);
            }
    }

    static void OnRecordState(Dictionary<string, object> data)
    {
        string state = Str(data, "outputState");
        bool finish = false;
        lock (sync)
        {
            if (state == "OBS_WEBSOCKET_OUTPUT_STARTED") { status = Status.Recording; started = DateTime.Now; }
            else if (state == "OBS_WEBSOCKET_OUTPUT_STOPPED")
            {
                status = Status.Idle;
                finish = !finishing;  // файл уже дописан — можно закрывать OBS
                finishing = true;
            }
            else status = Status.Busy;
        }
        Show();
        if (finish) ThreadPool.QueueUserWorkItem(_ => Finish());
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
            image = KeyImage(finishing ? Status.Busy : status, DateTime.Now - started);
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

    // Отказ на запрос obs-websocket; Code — код из requestStatus.
    class ObsError : Exception
    {
        public readonly int Code;
        public ObsError(string message, int code) : base(message) { Code = code; }
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
        public bool Open
        {
            get
            {
                try { return ws.State == WebSocketState.Open; }
                catch (ObjectDisposedException) { return false; }
            }
        }
        public bool Identified { get { return identified.WaitOne(0); } }

        public void Dispose()
        {
            try { ws.Abort(); } catch { }
            try { ws.Dispose(); } catch { }
        }

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
                throw new ObsError("obs-websocket: " + type + " failed: " + status["code"] + " " + Str(status, "comment"),
                    Convert.ToInt32(status["code"]));
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

        // Когда OBS закрывается, он присылает Close: отвечаем на него и сразу освобождаем сокет. Иначе OBS ждёт
        // ответа 5 с, а потом ещё 2 минуты не может закрыться, пока висящее соединение не отвалится по таймауту.
        void ReadLoop()
        {
            var buffer = new byte[64 * 1024];
            var message = new MemoryStream();
            try
            {
                while (ws.State == WebSocketState.Open)
                {
                    var r = ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None).Result;
                    if (r.MessageType == WebSocketMessageType.Close)
                    {
                        try { ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).Wait(1000); }
                        catch { }
                        break;
                    }
                    message.Write(buffer, 0, r.Count);
                    if (!r.EndOfMessage) continue;
                    var msg = json.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(message.ToArray()));
                    message.SetLength(0);
                    Handle(Convert.ToInt32(msg["op"]), (Dictionary<string, object>)msg["d"]);
                }
            }
            catch (Exception e) { Program.Log("obs-websocket: " + e.Message); }
            Dispose();
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
