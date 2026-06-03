using System.Diagnostics;
using CounterStrike2GSI;
using Nodes = CounterStrike2GSI.Nodes;
using DiscordRPC;

const int PORT = 3000;
const string APP_ID = "1352354388399882333";

// Kill any leftover cs2-rpc from a previous CS2 session
KillExistingInstances();

var gsl = new GameStateListener(PORT);
var rpc = new DiscordRpcClient(APP_ID);

rpc.OnReady += (_, e) => Console.WriteLine($"[*] Connected to Discord as {e.User.Username}");
rpc.OnConnectionFailed += (_, _) => Console.WriteLine("[!] Could not connect to Discord. Is it running?");

if (!rpc.Initialize())
{
    Console.WriteLine("[!] Discord RPC init failed. Is Discord running?");
    return;
}

bool inMatch = false;
string? currentMap = null;
DateTime matchStart = DateTime.UtcNow;

gsl.NewGameState += OnNewGameState;

if (!gsl.Start())
{
    Console.WriteLine("[!] Could not start GSI listener. Try running as admin.");
    return;
}

Console.WriteLine($"[*] Listening on http://localhost:{PORT}/");
Console.WriteLine("[*] Start CS2 and your presence will update automatically.");
Console.WriteLine("[*] Press Ctrl+C to quit.");

// Handle both Ctrl+C (SIGINT) and process termination (SIGTERM)
var quitEvent = new ManualResetEventSlim();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    quitEvent.Set();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => quitEvent.Set();

quitEvent.Wait();

rpc.ClearPresence();
rpc.Dispose();
gsl.Stop();
Console.WriteLine("\n[*] Goodbye.");

void OnNewGameState(GameState gs)
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
    rpc.SetPresence(new RichPresence
    {
        Details = "In Main Menu",
        State = "Waiting for a match",
        Assets = new Assets
        {
            LargeImageKey = "cs2_logo",
            LargeImageText = "Counter-Strike 2"
        },
        Timestamps = new Timestamps { Start = matchStart }
    });
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

    if (!alive)
        state += " (Dead)";

    rpc.SetPresence(new RichPresence
    {
        Details = details,
        State = state,
        Assets = new Assets
        {
            LargeImageKey = "cs2_logo",
            LargeImageText = "Counter-Strike 2"
        },
        Timestamps = new Timestamps { Start = matchStart }
    });
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
        catch { /* already dead or we lack perms */ }
        finally { proc.Dispose(); }
    }
}
