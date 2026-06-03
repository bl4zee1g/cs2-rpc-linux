using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using CounterStrike2GSI;
using Newtonsoft.Json.Linq;
using Nodes = CounterStrike2GSI.Nodes;

const int PORT = 3000;
const string APP_ID = "1352354388399882333";

KillExistingInstances();

// Connect to Discord/arrpc via IPC
var ipc = new DiscordIpc(APP_ID);
if (!ipc.Connect())
{
    Console.WriteLine("[!] Could not connect to Discord IPC. Is Discord/Vesktop running?");
    return;
}
Console.WriteLine("[*] Connected to Discord");

var tcp = new TcpListener(IPAddress.Any, PORT);

bool inMatch = false;
string? currentMap = null;
DateTime matchStart = DateTime.UtcNow;

tcp.Start();
Console.WriteLine($"[*] Listening on 0.0.0.0:{PORT}");
Console.WriteLine("[*] Start CS2 and your presence will update automatically.");
Console.WriteLine("[*] Press Ctrl+C to quit.");

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
            var reader = new StreamReader(stream, Encoding.UTF8);

            string? requestLine = reader.ReadLine();
            if (requestLine == null) { client.Close(); continue; }

            int contentLength = 0;
            string? line;
            while (!string.IsNullOrEmpty(line = reader.ReadLine()))
            {
                var m = Regex.Match(line, @"^Content-Length:\s*(\d+)", RegexOptions.IgnoreCase);
                if (m.Success)
                    contentLength = int.Parse(m.Groups[1].Value);
            }

            if (contentLength > 0)
            {
                var buffer = new char[contentLength];
                int totalRead = 0;
                while (totalRead < contentLength)
                {
                    int read = reader.Read(buffer, totalRead, contentLength - totalRead);
                    if (read == 0) break;
                    totalRead += read;
                }
                var json = new string(buffer, 0, totalRead);
                var gameState = new GameState(JObject.Parse(json));
                HandleGameState(gameState);
            }

            var resp = Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n");
            stream.Write(resp, 0, resp.Length);
            reader.Close(); stream.Close(); client.Close();
        }
        catch (SocketException) { break; }
        catch (ObjectDisposedException) { break; }
        catch (Exception ex) { Console.WriteLine($"[!] {ex.Message}"); }
    }
});
listenerThread.IsBackground = true;
listenerThread.Start();

quitEvent.Wait();

ipc.ClearPresence();
ipc.Dispose();
tcp.Stop();
Console.WriteLine("\n[*] Goodbye.");

void HandleGameState(GameState gs)
{
    bool wasInMatch = inMatch;

    inMatch = gs.Player.Activity == Nodes.PlayerActivity.Playing
              && !string.IsNullOrEmpty(gs.Map.Name);

    if (inMatch && !wasInMatch)
        matchStart = DateTime.UtcNow;

    if (!inMatch)
    {
        currentMap = null;
        SetMenuPresence();
        return;
    }

    currentMap = gs.Map.Name;
    SetMatchPresence(gs);
}

void SetMenuPresence()
{
    Console.WriteLine("[*] Menu presence");
    ipc.SetActivity("In Main Menu", "Waiting for a match", matchStart);
}

void SetMatchPresence(GameState gs)
{
    string map = gs.Map.Name;
    string mode = FormatGameMode(gs.Map.Mode);
    int ctScore = gs.Map.CTStatistics.Score;
    int tScore = gs.Map.TStatistics.Score;
    string team = gs.Player.Team.ToString();
    bool alive = gs.Player.State.Health > 0;

    string details = $"Map: {map} | Mode: {mode}";
    string state = $"Score: CT {ctScore} - T {tScore} | Team: {team}";
    if (!alive) state += " (Dead)";

    Console.WriteLine($"[*] Match presence: {details} | {state}");
    ipc.SetActivity(details, state, matchStart);
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

static void KillExistingInstances()
{
    int self = Environment.ProcessId;
    foreach (var proc in Process.GetProcessesByName("cs2-rpc"))
    {
        if (proc.Id == self) continue;
        try { proc.Kill(entireProcessTree: true); proc.WaitForExit(1000); }
        catch { }
        finally { proc.Dispose(); }
    }
}

// Minimal Discord IPC client — speaks the same protocol as Vencord
sealed class DiscordIpc : IDisposable
{
    private readonly string _appId;
    private Socket? _socket;

    public DiscordIpc(string appId) => _appId = appId;

    public bool Connect()
    {
        string[] paths =
        [
            $"discord-ipc-0",
            $"discord-ipc-1",
        ];

        foreach (var name in paths)
        {
            string path = Path.Combine(Path.GetTempPath(), name);  // /tmp/discord-ipc-0
            if (TryConnect(path)) return true;

            // Also try XDG runtime dir
            string? xdg = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            if (xdg != null)
            {
                path = Path.Combine(xdg, name);
                if (TryConnect(path)) return true;
            }
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

            // Send HANDSHAKE
            Send(0, new JObject { ["v"] = 1, ["client_id"] = _appId });

            // Read response
            var (op, data) = Receive();
            var json = JObject.Parse(data);
            Console.WriteLine($"[*] IPC handshake: cmd={json["cmd"]}, evt={json["evt"]}");

            if (json["evt"]?.ToString() == "READY")
                return true;

            _socket.Close();
            _socket = null;
        }
        catch { }
        return false;
    }

    public void SetActivity(string details, string state, DateTime start)
    {
        if (_socket == null) return;

        long startMs = new DateTimeOffset(start).ToUnixTimeMilliseconds();

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
                    ["timestamps"] = new JObject { ["start"] = startMs },
                    ["assets"] = new JObject
                    {
                        ["large_image"] = "cs2_logo",
                        ["large_text"] = "Counter-Strike 2"
                    }
                }
            }
        };

        Send(1, payload);
        var (op, resp) = Receive();
        Console.WriteLine($"[*] IPC response: {resp[..Math.Min(100, resp.Length)]}");
    }

    public void ClearPresence()
    {
        if (_socket == null) return;

        var payload = new JObject
        {
            ["cmd"] = "SET_ACTIVITY",
            ["nonce"] = Guid.NewGuid().ToString(),
            ["args"] = new JObject
            {
                ["pid"] = Environment.ProcessId,
                ["activity"] = null
            }
        };

        Send(1, payload);
        Receive();
    }

    private void Send(int opcode, JObject json)
    {
        if (_socket == null) return;
        byte[] jsonBytes = Encoding.UTF8.GetBytes(json.ToString(Newtonsoft.Json.Formatting.None));
        byte[] header = new byte[8];
        BitConverter.TryWriteBytes(header.AsSpan(0, 4), opcode);
        BitConverter.TryWriteBytes(header.AsSpan(4, 4), jsonBytes.Length);
        _socket.Send(header);
        _socket.Send(jsonBytes);
    }

    private (int op, string json) Receive()
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
