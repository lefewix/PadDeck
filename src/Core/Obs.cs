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

    /// <summary>
    /// Talks to OBS's built-in obs-websocket (protocol v5). Port and password are read from OBS's own settings,
    /// so they never need copying into PadDeck.
    /// </summary>
    public static class Obs
    {
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

        public static Outcome Run(ObsCommand cmd, string title)
        {
            try { return RunAsync(cmd, title).GetAwaiter().GetResult(); }
            catch (Exception ex) { return new Outcome(Glyphs.Warning, title, ex.Message, Tone.Error); }
        }

        /// <summary>Logs in and asks for the replay buffer state; changes nothing. For checking the connection.</summary>
        public static string Ping()
        {
            try
            {
                return Task.Run(async () =>
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    using var ws = await Open(cts.Token);
                    await Send(ws, 6, new JsonObject { ["requestType"] = "GetReplayBufferStatus", ["requestId"] = "ping" }, cts.Token);
                    while (true)
                    {
                        var m = await Receive(ws, cts.Token);
                        if (m["op"]?.GetValue<int>() == 7) return "connected; replay buffer active: " + m["d"]?["responseData"]?["outputActive"];
                    }
                }).GetAwaiter().GetResult();
            }
            catch (Exception ex) { return "failed: " + ex.Message; }
        }

        static async Task<ClientWebSocket> Open(CancellationToken ct)
        {
            if (System.Diagnostics.Process.GetProcessesByName("obs64").Length == 0) throw new InvalidOperationException("OBS isn’t running");
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

            string request = cmd switch { ObsCommand.SaveReplay => "SaveReplayBuffer", ObsCommand.ToggleRecord => "ToggleRecord", _ => "ToggleStream" };
            await Send(ws, 6, new JsonObject { ["requestType"] = request, ["requestId"] = "paddeck" }, cts.Token);

            // Wait for our response, then (for replays) the ReplayBufferSaved event that carries the file name.
            bool? ok = null;
            string comment = null, file = null;
            bool? active = null;
            while (!cts.IsCancellationRequested)
            {
                var m = await Receive(ws, cts.Token);
                int op = m["op"]?.GetValue<int>() ?? -1;
                var d = m["d"];
                if (op == 7 && d?["requestId"]?.GetValue<string>() == "paddeck")
                {
                    ok = d["requestStatus"]?["result"]?.GetValue<bool>();
                    int code = d["requestStatus"]?["code"]?.GetValue<int>() ?? 0;
                    comment = code == 604 ? NotRunning(cmd) : d["requestStatus"]?["comment"]?.GetValue<string>();
                    active = d["responseData"]?["outputActive"]?.GetValue<bool>();
                    if (ok != true || cmd != ObsCommand.SaveReplay) break;
                }
                else if (op == 5 && d?["eventType"]?.GetValue<string>() == "ReplayBufferSaved")
                {
                    file = d["eventData"]?["savedReplayPath"]?.GetValue<string>();
                    if (ok == true) break;
                }
            }
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }

            if (ok != true) return new Outcome(Glyphs.Warning, title, comment ?? "OBS didn’t answer", Tone.Error);
            return cmd switch
            {
                ObsCommand.SaveReplay => new Outcome(Glyphs.Replay, "Replay saved", file != null ? Path.GetFileName(file) : "Saving…", Tone.On),
                ObsCommand.ToggleRecord => new Outcome(Glyphs.Record, active == true ? "Recording" : "Recording stopped", "OBS", active == true ? Tone.Off : Tone.Normal),
                _ => new Outcome(Glyphs.Stream, active == true ? "Live" : "Stream ended", "OBS", active == true ? Tone.Off : Tone.Normal),
            };
        }

        static string NotRunning(ObsCommand c) => c == ObsCommand.SaveReplay ? "The replay buffer isn’t running" : "OBS isn’t doing that right now";

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
