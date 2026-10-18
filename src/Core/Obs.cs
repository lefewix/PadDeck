using System;
using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace PadDeck.Core
{
    public enum ObsCommand { SaveReplay, ToggleRecord, ToggleStream }

    /// <summary>What OBS is doing right now.</summary>
    public sealed record ObsState(bool Live, bool Recording, bool Replay);

    /// <summary>
    /// Talks to OBS's built-in obs-websocket (protocol v5). Port and password are read from OBS's own settings,
    /// so they never need copying into PadDeck.
    /// </summary>
    public static class Obs
    {
        public const string Exe = @"C:\Program Files\obs-studio\bin\64bit\obs64.exe";

        static string ConfigPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obs-studio", "plugin_config", "obs-websocket", "config.json");

        public static string Describe(ObsCommand c) => c switch
        {
            ObsCommand.SaveReplay => "Save replay",
            ObsCommand.ToggleRecord => "Start/stop recording",
            ObsCommand.ToggleStream => "Start/stop streaming",
            _ => c.ToString(),
        };

        /// <summary>obs-websocket v5 auth: base64(sha256(base64(sha256(password + salt)) + challenge)).</summary>
        public static string AuthString(string password, string salt, string challenge)
        {
            string secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password + salt)));
            return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
        }

        public static bool IsRunning()
        {
            var ps = System.Diagnostics.Process.GetProcessesByName("obs64");
            foreach (var p in ps) p.Dispose();
            return ps.Length > 0;
        }

        /// <summary>Starts OBS in the tray with the replay buffer on, so it doesn't pop up over a game.</summary>
        public static void StartWithReplay() =>
            Reset.Launch(new CoreApp { Name = "OBS Studio", Process = "obs64", Target = Exe, Args = "--startreplaybuffer --minimize-to-tray" });

        public static Outcome Run(ObsCommand cmd, string title)
        {
            if (cmd == ObsCommand.SaveReplay && !IsRunning())
            {
                try
                {
                    StartWithReplay();
                    return new Outcome(Glyphs.Replay, "OBS was off", "Starting it with the replay buffer", Tone.Normal);
                }
                catch (Exception ex) { return new Outcome(Glyphs.Warning, title, "OBS is off and wouldn’t start: " + ex.Message, Tone.Error); }
            }
            try { return Task.Run(() => RunAsync(cmd, title)).GetAwaiter().GetResult(); }
            catch (Exception ex) { return new Outcome(Glyphs.Warning, title, ex.Message, Tone.Error); }
        }

        /// <summary>Whether OBS is live, recording and buffering replays. Null when OBS isn't running or can't be reached.</summary>
        public static ObsState State()
        {
            if (!IsRunning()) return null;
            try
            {
                return Task.Run(async () =>
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    using var ws = await Open(cts.Token);
                    var s = await Call(ws, "GetStreamStatus", cts.Token);
                    var r = await Call(ws, "GetRecordStatus", cts.Token);
                    var b = await Call(ws, "GetReplayBufferStatus", cts.Token);
                    await Close(ws);
                    return new ObsState(Active(s), Active(r), Active(b));
                }).GetAwaiter().GetResult();
            }
            catch { return null; }
        }

        /// <summary>Stops the replay buffer, so closing OBS doesn't stop on its "outputs are active" exit prompt.</summary>
        public static bool StopReplayBuffer()
        {
            try
            {
                return Task.Run(async () =>
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    using var ws = await Open(cts.Token);
                    var d = await Call(ws, "StopReplayBuffer", cts.Token);
                    await Close(ws);
                    return Ok(d);
                }).GetAwaiter().GetResult();
            }
            catch { return false; }
        }

        /// <summary>Logs in and asks for the replay buffer state; changes nothing. For checking the connection.</summary>
        public static string Ping()
        {
            var s = State();
            return s == null ? "failed: OBS isn’t running or didn’t answer" : $"connected; live: {s.Live}, recording: {s.Recording}, replay buffer active: {s.Replay}";
        }

        static async Task<ClientWebSocket> Open(CancellationToken ct)
        {
            if (!IsRunning()) throw new InvalidOperationException("OBS isn’t running");
            var cfg = JsonNode.Parse(File.ReadAllText(ConfigPath));
            if (cfg?["server_enabled"]?.GetValue<bool>() != true) throw new InvalidOperationException("OBS’s WebSocket server is off (Tools → WebSocket Server Settings)");
            int port = cfg["server_port"]?.GetValue<int>() ?? 4455;
            string password = cfg["auth_required"]?.GetValue<bool>() == true ? cfg["server_password"]?.GetValue<string>() : null;

            var ws = new ClientWebSocket();
            ws.Options.AddSubProtocol("obswebsocket.json");
            try { await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}"), ct); }
            catch { ws.Dispose(); throw new InvalidOperationException("Couldn’t reach OBS (is it still starting?)"); }

            var hello = await Receive(ws, ct);
            var identify = new JsonObject { ["rpcVersion"] = 1, ["eventSubscriptions"] = 64 }; // 64 = Outputs events
            var auth = hello["d"]?["authentication"];
            if (auth != null)
            {
                if (password == null) throw new InvalidOperationException("OBS wants a password but none is saved");
                identify["authentication"] = AuthString(password, auth["salt"].GetValue<string>(), auth["challenge"].GetValue<string>());
            }
            await Send(ws, 1, identify, ct);
            var identified = await Receive(ws, ct);
            if (identified["op"]?.GetValue<int>() != 2) throw new InvalidOperationException("OBS rejected the connection");
            return ws;
        }

        static async Task<Outcome> RunAsync(ObsCommand cmd, string title)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var ws = await Open(cts.Token);
            try
            {
                if (cmd == ObsCommand.SaveReplay) return await SaveReplay(ws, title, cts.Token);
                var d = await Call(ws, cmd == ObsCommand.ToggleRecord ? "ToggleRecord" : "ToggleStream", cts.Token);
                if (!Ok(d)) return Fail(title, d, "OBS said no");
                bool active = Active(d);
                return cmd == ObsCommand.ToggleRecord
                    ? new Outcome(Glyphs.Record, active ? "Recording" : "Recording stopped", "OBS", active ? Tone.Off : Tone.Normal)
                    : new Outcome(Glyphs.Stream, active ? "Live" : "Stream ended", "OBS", active ? Tone.Off : Tone.Normal);
            }
            finally { await Close(ws); }
        }

        static async Task<Outcome> SaveReplay(ClientWebSocket ws, string title, CancellationToken ct)
        {
            // A replay buffer that's off (e.g. OBS was started without it) is turned on, so the next press saves.
            if (!Active(await Call(ws, "GetReplayBufferStatus", ct)))
            {
                var start = await Call(ws, "StartReplayBuffer", ct);
                if (!Ok(start)) return Fail(title, start, "The replay buffer is off and wouldn’t start");
                return new Outcome(Glyphs.Replay, "Replay buffer was off", "Turned it on. Clips save from now on", Tone.Normal);
            }

            string file = null;
            void OnEvent(JsonNode e)
            {
                if (e?["eventType"]?.GetValue<string>() == "ReplayBufferSaved") file = e["eventData"]?["savedReplayPath"]?.GetValue<string>();
            }
            var saved = await Call(ws, "SaveReplayBuffer", ct, OnEvent);
            if (!Ok(saved)) return Fail(title, saved, "OBS couldn’t save the replay");

            // OBS answers at once and announces the file once it's written. The save is already under way, so
            // running out of time waiting for the file name isn't an error.
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            try
            {
                while (file == null)
                {
                    var m = await Receive(ws, wait.Token);
                    if (m["op"]?.GetValue<int>() == 5) OnEvent(m["d"]);
                }
            }
            catch { }
            if (file != null) AddCover(file);
            return new Outcome(Glyphs.Replay, "Replay saved", file != null ? Path.GetFileName(file) : "Saving…", Tone.On);
        }

        /// <summary>ClipTrimmer gives a new replay its cover picture (the moment it was saved), which File
        /// Explorer and ClipTrimmer's clip list show as the thumbnail. It runs without a window.</summary>
        const string CoverTool = @"C:\claude-tools\ClipTrimmer\dist\ClipTrimmer\ClipTrimmer.exe";

        static void AddCover(string file)
        {
            try
            {
                if (!File.Exists(CoverTool)) return;
                var psi = new System.Diagnostics.ProcessStartInfo(CoverTool) { UseShellExecute = false, CreateNoWindow = true };
                psi.ArgumentList.Add("--covers");
                psi.ArgumentList.Add(Path.GetFullPath(file));
                System.Diagnostics.Process.Start(psi)?.Dispose();
            }
            catch { }
        }

        /// <summary>Sends a request and returns its response, handing any events that arrive meanwhile to <paramref name="onEvent"/>.</summary>
        static async Task<JsonNode> Call(ClientWebSocket ws, string type, CancellationToken ct, Action<JsonNode> onEvent = null)
        {
            await Send(ws, 6, new JsonObject { ["requestType"] = type, ["requestId"] = type }, ct);
            while (true)
            {
                var m = await Receive(ws, ct);
                int op = m["op"]?.GetValue<int>() ?? -1;
                if (op == 7 && m["d"]?["requestId"]?.GetValue<string>() == type) return m["d"];
                if (op == 5) onEvent?.Invoke(m["d"]);
            }
        }

        static bool Ok(JsonNode d) => d?["requestStatus"]?["result"]?.GetValue<bool>() == true;
        static bool Active(JsonNode d) => Ok(d) && d["responseData"]?["outputActive"]?.GetValue<bool>() == true;

        static Outcome Fail(string title, JsonNode d, string fallback) =>
            new Outcome(Glyphs.Warning, title, d?["requestStatus"]?["comment"]?.GetValue<string>() ?? fallback, Tone.Error);

        static async Task Close(ClientWebSocket ws)
        {
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
        }

        static async Task Send(ClientWebSocket ws, int op, JsonObject d, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(new JsonObject { ["op"] = op, ["d"] = d }.ToJsonString());
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }

        static async Task<JsonNode> Receive(ClientWebSocket ws, CancellationToken ct)
        {
            var buf = new byte[16384];
            using var ms = new MemoryStream();
            WebSocketReceiveResult r;
            do
            {
                r = await ws.ReceiveAsync(buf, ct);
                if (r.MessageType == WebSocketMessageType.Close) throw new InvalidOperationException("OBS closed the connection (wrong password?)");
                ms.Write(buf, 0, r.Count);
            } while (!r.EndOfMessage);
            return JsonNode.Parse(ms.ToArray());
        }
    }
}
