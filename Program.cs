using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using CounterStrike2GSI;
using Newtonsoft.Json.Linq;
using Nodes = CounterStrike2GSI.Nodes;

const string APP_ID = "1352354388399882333";


var ipc = new DiscordIpc(APP_ID);
if (!ipc.Connect())
{
    Console.WriteLine("[!] Could not connect to Discord IPC. Is Discord/Vesktop running?");
    return;
}
Console.WriteLine("[*] Connected to Discord");

var tcp = new TcpListener(IPAddress.Loopback, 3000);
tcp.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
tcp.Start();

Console.WriteLine("[*] Listening on 127.0.0.1:3000");
Console.WriteLine("[*] Start CS2 and your presence will update automatically.");
Console.WriteLine("[*] Press Ctrl+C to quit.");

bool inMatch = false;
DateTime matchStart = DateTime.UtcNow;
DateTime lastDataReceived = DateTime.UtcNow;
bool presenceActive = false;

var quitEvent = new ManualResetEventSlim();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; quitEvent.Set(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => quitEvent.Set();

var listenerThread = new Thread(() =>
{
    while (true)
    {
        try
        {
            var client = tcp.AcceptTcpClient();
            var stream = client.GetStream();
            stream.ReadTimeout = 5000;

            // Read raw bytes until we find the end of headers
            var headerBuf = new List<byte>();
            int prev3 = 0, prev2 = 0, prev1 = 0;
            while (true)
            {
                int b = stream.ReadByte();
                if (b == -1) { client.Close(); break; }
                headerBuf.Add((byte)b);
                if (prev2 == '\r' && prev1 == '\n' && b == '\r') { /* possible end */ }
                if (prev1 == '\r' && b == '\n' && headerBuf.Count >= 4)
                {
                    // Check if last 4 bytes are \r\n\r\n
                    var hb = headerBuf;
                    if (hb[^4] == '\r' && hb[^3] == '\n' && hb[^2] == '\r' && hb[^1] == '\n')
                        break;
                }
                prev1 = b;
            }

            var headers = Encoding.UTF8.GetString(headerBuf.ToArray());
            var clMatch = Regex.Match(headers, @"Content-Length:\s*(\d+)", RegexOptions.IgnoreCase);
            int contentLength = clMatch.Success ? int.Parse(clMatch.Groups[1].Value) : 0;

            if (contentLength > 0)
            {
                var body = new byte[contentLength];
                int totalRead = 0;
                while (totalRead < contentLength)
                {
                    int read = stream.Read(body, totalRead, contentLength - totalRead);
                    if (read == 0) break;
                    totalRead += read;
                }
                var json = Encoding.UTF8.GetString(body, 0, totalRead);
                var gameState = new GameState(JObject.Parse(json));
                lastDataReceived = DateTime.UtcNow;
                HandleGameState(gameState);
            }

            var resp = Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n");
            stream.Write(resp, 0, resp.Length);
            stream.Close(); client.Close();
        }
        catch (SocketException) { break; }
        catch (ObjectDisposedException) { break; }
        catch (IOException) { }
        catch (Exception ex) { Console.WriteLine($"[!] {ex.GetType().Name}: {ex.Message}"); }
    }
});
listenerThread.IsBackground = true;
listenerThread.Start();

// Watchdog: clear presence if CS2 stops sending data for 30s, keep running for next launch
var watchdog = new Thread(() =>
{
    while (!quitEvent.IsSet)
    {
        Thread.Sleep(5000);
        if (presenceActive && (DateTime.UtcNow - lastDataReceived).TotalSeconds > 30)
        {
            Console.WriteLine("[*] CS2 closed, clearing presence — waiting for next launch");
            ipc.ClearPresence();
            presenceActive = false;
            inMatch = false;
        }
    }
});
watchdog.IsBackground = true;
watchdog.Start();

quitEvent.Wait();

ipc.ClearPresence();
ipc.Dispose();
tcp.Stop();

void HandleGameState(GameState gs)
{
    if (!presenceActive)
        Console.WriteLine("[*] CS2 detected — updating presence");
    bool wasInMatch = inMatch;

    inMatch = !string.IsNullOrEmpty(gs.Map.Name);

    if (inMatch && !wasInMatch)
        matchStart = DateTime.UtcNow;

    if (!inMatch)
    {
        ipc.SetActivity("In Main Menu", "Waiting for a match", matchStart);
        presenceActive = true;
        return;
    }

    string map = gs.Map.Name;
    string mode = FormatGameMode(gs.Map.Mode);
    int ctScore = gs.Map.CTStatistics.Score;
    int tScore = gs.Map.TStatistics.Score;
    string team = gs.Player.Team.ToString();
    bool alive = gs.Player.State.Health > 0;

    string details = $"Map: {map} | Mode: {mode}";
    string state = $"Score: CT {ctScore} - T {tScore} | Team: {team}";
    if (!alive) state += " (Dead)";

    ipc.SetActivity(details, state, matchStart);
    presenceActive = true;
}

static string FormatGameMode(Nodes.GameMode mode) => mode switch
{
    Nodes.GameMode.Competitive => "Competitive",
    Nodes.GameMode.Casual => "Casual",
    Nodes.GameMode.Deathmatch => "Deathmatch",
    Nodes.GameMode.Scrimcomp2v2 => "Wingman",
    Nodes.GameMode.Scrimcomp5v5 => "Premier",
    Nodes.GameMode.Custom => "Custom",
    Nodes.GameMode.Cooperative => "Cooperative",
    Nodes.GameMode.Training => "Training",
    Nodes.GameMode.Skirmish => "Skirmish",
    _ => mode.ToString()
};

sealed class DiscordIpc : IDisposable
{
    private readonly string _appId;
    private Socket? _socket;

    public DiscordIpc(string appId) => _appId = appId;

    public bool Connect()
    {
        string[] names = ["discord-ipc-0", "discord-ipc-1"];
        foreach (var name in names)
        {
            if (TryConnect(Path.Combine(Path.GetTempPath(), name))) return true;
            string? xdg = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            if (xdg != null && TryConnect(Path.Combine(xdg, name))) return true;
        }
        return false;
    }

    private bool TryConnect(string path)
    {
        try
        {
            var sock = new Socket(AddressFamily.Unix, SocketType.Stream, (ProtocolType)0);
            sock.Connect(new UnixDomainSocketEndPoint(path));
            _socket = sock;

            Send(0, new JObject { ["v"] = 1, ["client_id"] = _appId });
            var (_, data) = Receive();
            var json = JObject.Parse(data);

            if (json["evt"]?.ToString() == "READY")
            {
                Console.WriteLine($"[*] IPC: connected ({json["data"]?["user"]?["username"]})");
                return true;
            }

            _socket.Close();
            _socket = null;
        }
        catch { }
        return false;
    }

    public void SetActivity(string details, string state, DateTime start)
    {
        if (_socket == null) return;

        var payload = new JObject
        {
            ["cmd"] = "SET_ACTIVITY",
            ["nonce"] = Guid.NewGuid().ToString(),
            ["args"] = new JObject
            {
                ["pid"] = Environment.ProcessId,
                ["activity"] = new JObject
                {
                    ["type"] = 0,
                    ["details"] = details,
                    ["state"] = state,
                    ["timestamps"] = new JObject { ["start"] = new DateTimeOffset(start).ToUnixTimeMilliseconds() },
                    ["assets"] = new JObject
                    {
                        ["large_image"] = "cs2_logo",
                        ["large_text"] = "Counter-Strike 2"
                    }
                }
            }
        };

        Send(1, payload);
        Receive();
    }

    public void ClearPresence()
    {
        if (_socket == null) return;
        Send(1, new JObject
        {
            ["cmd"] = "SET_ACTIVITY",
            ["nonce"] = Guid.NewGuid().ToString(),
            ["args"] = new JObject { ["pid"] = Environment.ProcessId, ["activity"] = null }
        });
        Receive();
    }

    private void Send(int opcode, JObject json)
    {
        if (_socket == null) return;
        byte[] body = Encoding.UTF8.GetBytes(json.ToString(Newtonsoft.Json.Formatting.None));
        byte[] header = new byte[8];
        BitConverter.TryWriteBytes(header.AsSpan(0, 4), opcode);
        BitConverter.TryWriteBytes(header.AsSpan(4, 4), body.Length);
        _socket.Send(header);
        _socket.Send(body);
    }

    private (int op, string data) Receive()
    {
        if (_socket == null) return (-1, "{}");
        byte[] header = new byte[8];
        int read = 0;
        while (read < 8)
        {
            int n = _socket.Receive(header, read, 8 - read, SocketFlags.None);
            if (n == 0) return (-1, "{}");
            read += n;
        }
        int op = BitConverter.ToInt32(header, 0);
        int len = BitConverter.ToInt32(header, 4);
        byte[] body = new byte[len];
        read = 0;
        while (read < len)
        {
            int n = _socket.Receive(body, read, len - read, SocketFlags.None);
            if (n == 0) break;
            read += n;
        }
        return (op, Encoding.UTF8.GetString(body, 0, read));
    }

    public void Dispose()
    {
        _socket?.Close();
        _socket?.Dispose();
    }
}
