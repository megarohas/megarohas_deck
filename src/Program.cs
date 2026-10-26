// MegarohasDeck — личный плагин megarohas для fifine Control Deck
// (движок Mirabox StreamDock, протокол как у Elgato Stream Deck).
// Здесь — WebSocket-связь с софтом деки и раздача событий по модулям (Displays, Audio, Record, Vpn, Gaming).
// Сборка: build.ps1 (csc из .NET Framework 4, C# 5).
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

static class Program
{
    public const string Prefix = "com.megarohas.deck.";

    static readonly ClientWebSocket ws = new ClientWebSocket();
    static readonly object sendLock = new object();
    static readonly JavaScriptSerializer json = new JavaScriptSerializer();
    public static readonly string Dir = AppDomain.CurrentDomain.BaseDirectory;
    static readonly string logPath = Path.Combine(Dir, "MegarohasDeck.log");
    // Файл debug рядом с exe — писать в лог все входящие сообщения.
    public static readonly bool Debug = File.Exists(Path.Combine(Dir, "debug"));

    static void Main(string[] args)
    {
        string port = Arg(args, "-port"), uuid = Arg(args, "-pluginUUID"), register = Arg(args, "-registerEvent");
        if (port == null || uuid == null || register == null) return;
        try
        {
            ws.ConnectAsync(new Uri("ws://127.0.0.1:" + port), CancellationToken.None).Wait();
            Send(new { @event = register, uuid = uuid });
            using (new Timer(_ => Poll(), null, 1000, 1000))
                ReadLoop();
        }
        catch (Exception e) { Log(e.ToString()); }
    }

    // Выходим, когда софт деки закрывает соединение.
    static void ReadLoop()
    {
        var buffer = new byte[16 * 1024];
        var message = new MemoryStream();
        while (ws.State == WebSocketState.Open)
        {
            var r = ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None).Result;
            if (r.MessageType == WebSocketMessageType.Close) return;
            message.Write(buffer, 0, r.Count);
            if (!r.EndOfMessage) continue;
            string text = Encoding.UTF8.GetString(message.ToArray());
            message.SetLength(0);
            if (Debug) Log(text);
            try { Dispatch(json.Deserialize<Dictionary<string, object>>(text)); }
            catch (Exception e) { Log(e + "\n" + text); }
        }
    }

    static void Dispatch(Dictionary<string, object> msg)
    {
        string ev = Field(msg, "event"), action = Field(msg, "action"), context = Field(msg, "context");
        if (ev == null || action == null || context == null || !action.StartsWith(Prefix)) return;
        string name = action.Substring(Prefix.Length);
        if (name.StartsWith(Displays.Group)) Displays.Handle(ev, name.Substring(Displays.Group.Length), context);
        else if (name.StartsWith(Audio.Group)) Audio.Handle(ev, name.Substring(Audio.Group.Length), context);
        else if (name.StartsWith(Record.Group)) Record.Handle(ev, name.Substring(Record.Group.Length), context);
        else if (name.StartsWith(Vpn.Group)) Vpn.Handle(ev, name.Substring(Vpn.Group.Length), context);
        else if (name.StartsWith(Gaming.Group)) Gaming.Handle(ev, name.Substring(Gaming.Group.Length), context);
    }

    // Раз в секунду: подсветка следует за изменениями, сделанными не с деки.
    static void Poll()
    {
        try
        {
            Displays.Refresh(false);
            Audio.Refresh(false);
            Record.Tick();
            Vpn.Refresh(false);
            Gaming.Refresh(false);
        }
        catch (Exception e) { Log(e.ToString()); }
    }

    public static void SetState(string context, int state)
    {
        Send(new { @event = "setState", context = context, payload = new { state = state } });
    }

    public static void SetImage(string context, string dataUri)
    {
        Send(new { @event = "setImage", context = context, payload = new { image = dataUri, target = 0 } });
    }

    public static void ShowAlert(string context)
    {
        Send(new { @event = "showAlert", context = context });
    }

    public static void Send(object message)
    {
        var bytes = Encoding.UTF8.GetBytes(json.Serialize(message));
        lock (sendLock)
        {
            try { ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None).Wait(); }
            catch (Exception e) { Log(e.Message); }
        }
    }

    static string Field(Dictionary<string, object> msg, string name)
    {
        object value;
        return msg != null && msg.TryGetValue(name, out value) ? value as string : null;
    }

    static string Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    public static void Log(string line)
    {
        try { File.AppendAllText(logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + line + Environment.NewLine); }
        catch { }
    }
}
