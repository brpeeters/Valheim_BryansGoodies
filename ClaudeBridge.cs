using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace BryansGoodies
{
    // "/claude <request>": ask Claude for anything (spawn a mob, give an item, build a house...).
    //
    // Flow:
    //  1. Any player with the mod types /claude <text>. The request (text + context: position, facing, biome, time...)
    //     is sent to every client as the network message "BG_ClaudeAsk". The sender sees "sent to Claude".
    //  2. The client(s) marked as Claude host (setting Claude_Host, Bryan's machine) append it to
    //     UserData\BryansGoodies\claude\inbox.jsonl, one JSON object per line. Claude watches that file.
    //  3. Claude answers by writing UserData\BryansGoodies\claude\outbox\<request id>.txt: a script, one action per line
    //     (several files per request are fine, e.g. a quick word then the actions; the id stays valid 10 minutes).
    //     Claude can also write to any player at any time: a file whose first line is "@<player>" or "@all".
    //     The host trims it to what the requester is allowed (setting Claude_Policy, per player: fun, spawn, give, build,
    //     call, all, none) and sends it back ("BG_ClaudeRun", targeted); the requester's own game executes it, so what is
    //     spawned, given or built is created by the requester's client and visible to everyone. The request text is
    //     untrusted data for Claude; the host-side policy is the hard limit whatever the request says.
    //
    // Script actions (one per line, "#" starts a comment):
    //   say <text>                       whisper from "Claude" in the requester's chat window (local, nobody else sees it)
    //   center <text>                    message in the center of the requester's screen
    //   chat <text>                      posted in the chat as the requester (everyone nearby sees it)
    //   spawn <prefab> [n] [where]       where = here | front:<m> | <x> <y> <z>   (default front:3), creature or object
    //   give <item prefab> [n] [quality] into the requester's inventory
    //   place <piece prefab> <x> <y> <z> [rotY]   build piece at absolute coordinates (y = "g" for ground level, "g+1.5" above it)
    //   placerel <piece prefab> <dx> <dy> <dz> [rotY]  same, relative to the requester's position and facing (dz = forward)
    //   ground <x> <z>                   (no-op; helper for Claude: the context already contains the ground height)
    //   weather <Rain|ThunderStorm|Clear|Snow|Mist...|normal>   local weather of the requester (normal = back to the game's)
    //   repair [radius]                  repairs every damaged build piece around the requester (default 10 m)
    //   destroy                          removes what the requester is looking at (rock, tree, build piece; no drops for rocks)
    //   shout [force] [radius]           FUS RO DAH: creatures and loose objects in a cone in front are thrown away
    //   tp <x> <y> <z>                   teleport the requester
    //   call <expression> [args...]      the /call reflection, e.g. call me.SetHealth 50 (its result is reported back)
    //   scan [radius] [at x y z] [y min max] [like <part>]   reports the build pieces around a point (default: the requester), horizontal radius
    //   look                             reports what the requester is looking at
    //   remove <x> <y> <z> [prefab part] [radius]   removes the build piece closest to a point (default within 1.5 m); reports it
    //   stand <x> <y> <z> <item prefab> [orientation]   puts an item on the item stand closest to a point (decoration, nothing taken from anyone)
    //   clearitems [radius] [at x y z] [like <part>]   removes the loose items lying around (default 5 m around the requester)
    //   terrain ops | terrain <op> <x> <y> <z> [times]   one of the game's terrain operations (hoe: raise, path...) placed at the given height; capped at ~8 m by the game
    // Whatever scan, look, call and failed actions report is sent back by the requester to the host, who appends one
    // JSON line {"player","id","time","text"} to UserData\BryansGoodies\claude\info.jsonl for the agents to read.
    public static class ClaudeBridge
    {
        const string AskRpc = "BG_ClaudeAsk", RunRpc = "BG_ClaudeRun", InfoRpc = "BG_ClaudeInfo";
        static string Info => Path.Combine(Dir, "info.jsonl");

        public static MelonPreferences_Entry<bool> Enabled, Host;
        public static MelonPreferences_Entry<string> Policy;
        public static MelonPreferences_Entry<float> RateLimit;
        static readonly Dictionary<string, string> requesterNames = new Dictionary<string, string>(); // request id -> player name (host side)
        static readonly Dictionary<string, List<float>> recent = new Dictionary<string, List<float>>(); // player -> request times (host side)
        static readonly Dictionary<string, float> requestTimes = new Dictionary<string, float>(); // request id -> time (host side)

        static ZRoutedRpc registeredRpc;
        static readonly System.Reflection.FieldInfo RpcFunctions = AccessTools.Field(typeof(ZRoutedRpc), "m_functions");
        static readonly System.Reflection.FieldInfo HideTimer = AccessTools.Field(typeof(Chat), "m_hideTimer");
        static float nextOutboxPoll;
        static readonly Dictionary<string, long> requesters = new Dictionary<string, long>(); // request id -> sender uid (host side)

        static string Dir => Path.Combine(MelonUtils.UserDataDirectory, "BryansGoodies", "claude");
        static string Inbox => Path.Combine(Dir, "inbox.jsonl");
        static string Outbox => Path.Combine(Dir, "outbox");

        public static void Init(MelonPreferences_Category cat)
        {
            Enabled = Prefs.Entry(cat, "Claude_Enabled", true, "/claude command (ask Claude for anything)");
            Host = Prefs.Entry(cat, "Claude_Host", false, "This machine relays /claude requests to Claude (inbox/outbox files)");
            Policy = Prefs.Entry(cat, "Claude_Policy", "default=all",
                "Host side: what each player may get. Levels: none, fun (say/center/chat/weather), spawn, give, build, call, all. Example: default=fun; Clothilde=all; Bob=fun,spawn");
            RateLimit = Prefs.Entry(cat, "Claude_RequestsPerMinute", 6f, "Host side: max /claude requests per player per minute");
            if (Policy.Value == "default=fun") { Policy.Value = "default=all"; Main.Save(); } // everyone has the same rights (Bryan, 2026-10-29)
        }

        public static void Register()
        {
            new Terminal.ConsoleCommand("claude", "<request> : ask Claude (spawn a mob, give an item, build something...)", args =>
            {
                var ctx = args.Context;
                if (!Enabled.Value) { ctx.AddString("/claude is disabled (Bryan's Goodies menu)."); return; }
                string text = (args.ArgsAll ?? "").Trim();
                if (text.Length == 0) { ctx.AddString("Usage: claude <what you want>"); return; }
                var p = Player.m_localPlayer;
                if (p == null) { ctx.AddString("No player."); return; }
                string payload = BuildRequest(p, text);
                var rpc = ZRoutedRpc.instance;
                if (rpc != null && ZNet.instance != null) rpc.InvokeRoutedRPC(ZRoutedRpc.Everybody, AskRpc, payload); // includes ourselves
                else OnAsk(ZDOMan.GetSessionID(), payload); // single player: straight to the local host (if any)
                // The request is echoed in the requester's chat window (local), so the exchange reads like a conversation.
                if (Chat.instance != null) { Chat.instance.AddString(p.GetPlayerName() + " to Claude", text, Talker.Type.Whisper); HideTimer?.SetValue(Chat.instance, 0f); }
            });
        }

        public static void Export(Dictionary<string, object> h) => h["claude.requesters"] = new Dictionary<string, long>(requesters);
        public static void Import(Dictionary<string, object> h)
        {
            if (h.TryGetValue("claude.requesters", out var o) && o is Dictionary<string, long> d) foreach (var kv in d) requesters[kv.Key] = kv.Value;
        }

        // ---------- Request (JSON, one line) ----------

        static string BuildRequest(Player p, string text)
        {
            var pos = p.transform.position; var fwd = p.transform.forward;
            string biome = p.GetCurrentBiome().ToString();
            float day = EnvMan.instance != null ? EnvMan.instance.GetDayFraction() : 0f;
            int dayNo = EnvMan.instance != null ? EnvMan.instance.GetDay() : 0;
            var inv = new List<string>();
            foreach (var it in p.GetInventory().GetAllItems()) inv.Add(it.m_shared.m_name.Replace("$item_", "") + " x" + it.m_stack);
            var nearby = new List<string>();
            foreach (var q in Player.GetAllPlayers()) if (q != p) nearby.Add(q.GetPlayerName() + " at " + F(Vector3.Distance(pos, q.transform.position)) + "m");
            var sb = new StringBuilder();
            sb.Append("{\"id\":\"").Append(DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")).Append('-').Append(UnityEngine.Random.Range(1000, 9999)).Append('"');
            sb.Append(",\"player\":").Append(J(p.GetPlayerName()));
            sb.Append(",\"text\":").Append(J(text));
            sb.Append(",\"pos\":[").Append(F(pos.x)).Append(',').Append(F(pos.y)).Append(',').Append(F(pos.z)).Append(']');
            sb.Append(",\"forward\":[").Append(F(fwd.x)).Append(',').Append(F(fwd.y)).Append(',').Append(F(fwd.z)).Append(']');
            sb.Append(",\"biome\":").Append(J(biome));
            sb.Append(",\"day\":").Append(dayNo).Append(",\"dayFraction\":").Append(F(day));
            sb.Append(",\"health\":").Append(F(p.GetHealth())).Append(",\"maxHealth\":").Append(F(p.GetMaxHealth()));
            sb.Append(",\"inventory\":").Append(J(string.Join(", ", inv)));
            sb.Append(",\"nearbyPlayers\":").Append(J(string.Join(", ", nearby)));
            sb.Append(",\"time\":").Append(J(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
            sb.Append('}');
            return sb.ToString();
        }

        static string F(float v) => v.ToString("0.0", CultureInfo.InvariantCulture);
        static string J(string s) => "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n") + "\"";

        // ---------- Network ----------

        public static void Update()
        {
            var rpc = ZRoutedRpc.instance;
            if (rpc != null && rpc != registeredRpc)
            {
                registeredRpc = rpc;
                var table = RpcFunctions?.GetValue(rpc) as System.Collections.IDictionary;
                table?.Remove(AskRpc.GetStableHashCode()); table?.Remove(RunRpc.GetStableHashCode()); table?.Remove(InfoRpc.GetStableHashCode());
                rpc.Register<string>(InfoRpc, OnInfo);
                rpc.Register<string>(AskRpc, OnAsk);
                rpc.Register<string>(RunRpc, OnRun);
            }
            if (Host.Value) PollOutbox();
        }

        static void OnAsk(long sender, string payload)
        {
            if (!Host.Value) return;
            try
            {
                string id = Extract(payload, "id"), player = Extract(payload, "player") ?? "?";
                if (Level(player) == "none") { Main.Log.Msg($"/claude request from {player} ignored (policy: none)."); return; }
                if (!recent.TryGetValue(player, out var times)) recent[player] = times = new List<float>();
                float now = Time.unscaledTime;
                times.RemoveAll(t => now - t > 60f);
                if (times.Count >= Mathf.Max(1, (int)RateLimit.Value)) { Main.Log.Warning($"/claude request from {player} dropped (rate limit)."); return; }
                times.Add(now);
                Directory.CreateDirectory(Outbox);
                File.AppendAllText(Inbox, payload + "\n");
                if (id != null) { requesters[id] = sender; requesterNames[id] = player; requestTimes[id] = now; }
                foreach (var old in new List<string>(requestTimes.Keys))
                    if (now - requestTimes[old] > 600f) { requestTimes.Remove(old); requesters.Remove(old); requesterNames.Remove(old); }
                Main.Log.Msg($"/claude request from {player}: {Extract(payload, "text")}");
            }
            catch (Exception e) { Main.Log.Warning("Claude inbox: " + e.Message); }
        }

        static string Extract(string json, string key)
        {
            int i = json.IndexOf("\"" + key + "\":\"", StringComparison.Ordinal);
            if (i < 0) return null;
            i += key.Length + 4;
            var sb = new StringBuilder();
            for (; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '\\' && i + 1 < json.Length) { sb.Append(json[++i]); continue; }
                if (c == '"') break;
                sb.Append(c);
            }
            return sb.ToString();
        }

        // Host side: answers written by Claude in outbox\<id>.txt are sent to the requester.
        static void PollOutbox()
        {
            float now = Time.unscaledTime;
            if (now < nextOutboxPoll) return;
            nextOutboxPoll = now + 1f;
            WriteOnline();
            if (!Directory.Exists(Outbox)) return;
            // Two kinds of files:
            //  - <request id>.txt : answer to a request (sent to its requester; the id stays valid 10 minutes, several files allowed);
            //  - any name, first line "@<player name>" or "@all" : addressed script, independent of any request.
            var files = Directory.GetFiles(Outbox, "*.txt"); Array.Sort(files, StringComparer.Ordinal);
            foreach (var file in files)
            {
                string name = Path.GetFileNameWithoutExtension(file), script;
                try { script = File.ReadAllText(file).Replace("\r", ""); File.Delete(file); } catch { continue; }
                if (script.StartsWith("@"))
                {
                    int nl = script.IndexOf('\n');
                    string to = (nl < 0 ? script : script.Substring(0, nl)).Substring(1).Trim();
                    script = nl < 0 ? "" : script.Substring(nl + 1);
                    if (to.Equals("all", StringComparison.OrdinalIgnoreCase)) { foreach (var kv in Players()) Send(kv.Value, kv.Key, script); Main.Log.Msg($"Claude script {name} sent to everyone."); }
                    else
                    {
                        long uid = -1; string who = to;
                        foreach (var kv in Players()) if (kv.Key.Equals(to, StringComparison.OrdinalIgnoreCase)) { uid = kv.Value; who = kv.Key; }
                        if (uid == -1) { Main.Log.Warning($"Claude script {name}: player '{to}' not found."); continue; }
                        Send(uid, who, script);
                        Main.Log.Msg($"Claude script {name} sent to {who}.");
                    }
                    continue;
                }
                string id = name; int dot = id.IndexOf('.'); if (dot > 0) id = id.Substring(0, dot);
                if (!requesters.TryGetValue(id, out long ruid)) { Main.Log.Warning($"Claude answer {id}: unknown request, executed here."); ruid = ZDOMan.GetSessionID(); }
                requesterNames.TryGetValue(id, out string rwho);
                Send(ruid, rwho ?? Me(), script, id);
                Main.Log.Msg($"Claude answer {id} sent.");
            }
        }

        // Host side: claude\online.txt lists the connected players, one name per line, rewritten when the set changes.
        // The coordinator watches it to start (or wake) one agent per connected player.
        static string lastOnline;
        static void WriteOnline()
        {
            var names = new List<string>(Players().Keys); names.Sort(StringComparer.OrdinalIgnoreCase);
            string text = string.Join("\n", names) + "\n";
            if (text == lastOnline) return;
            try { Directory.CreateDirectory(Dir); File.WriteAllText(Path.Combine(Dir, "online.txt"), text); lastOnline = text; }
            catch (Exception e) { Main.Log.Warning("Claude online: " + e.Message); }
        }

        static string Me() => Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerName() : "?";

        // Connected players with the mod or not: name -> network uid (ourselves included).
        static Dictionary<string, long> Players()
        {
            var d = new Dictionary<string, long>();
            if (Player.m_localPlayer != null) d[Me()] = ZDOMan.GetSessionID();
            if (ZNet.instance != null)
                foreach (var peer in ZNet.instance.GetPeers())
                    if (peer != null && !peer.m_server && !string.IsNullOrEmpty(peer.m_playerName)) d[peer.m_playerName] = peer.m_uid;
            // On a dedicated server our only peer is the server: the other players come from the server's player list
            // (routed RPCs reach them through the server, by their character's user id).
            if (ZNet.instance != null)
                foreach (var info in ZNet.instance.GetPlayerList())
                    if (!string.IsNullOrEmpty(info.m_name) && !d.ContainsKey(info.m_name) && info.m_characterID.UserID != 0) d[info.m_name] = info.m_characterID.UserID;
            return d;
        }

        // Host side: filters for that player, then sends (or runs locally for ourselves).
        static void Send(long uid, string who, string script, string id = null)
        {
            script = Filter(script, who);
            if (script.Trim().Length == 0) return;
            script = "#id " + (id ?? "none") + "\n#level " + Level(who) + "\n" + script;
            var rpc = ZRoutedRpc.instance;
            if (rpc != null && uid != ZDOMan.GetSessionID()) rpc.InvokeRoutedRPC(uid, RunRpc, script);
            else OnRun(ZDOMan.GetSessionID(), script);
        }

        // Requester side: executes the script.
        static void OnRun(long sender, string script)
        {
            var p = Player.m_localPlayer;
            if (p == null) return;
            int done = 0; string id = "none";
            runLevel = "fun";
            output = new StringBuilder();
            foreach (var raw in script.Replace("\r", "").Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("#id ")) { id = line.Substring(4).Trim(); continue; }
                if (line.StartsWith("#level ")) { runLevel = line.Substring(7).Trim(); continue; }
                if (line.Length == 0 || line.StartsWith("#")) continue;
                try { Execute(p, line); done++; }
                catch (Exception e)
                {
                    Main.Log.Warning($"Claude action failed: {line} -> {e.Message}");
                    p.Message(MessageHud.MessageType.TopLeft, "Claude: failed: " + line);
                    output.AppendLine("failed: " + line + " -> " + e.Message);
                }
            }
            Main.Log.Msg($"Claude script: {done} action(s) executed.");
            // Whatever the actions reported (scan, call results, failures) goes back to the host(s), who write it for the agents.
            if (output.Length > 0)
            {
                string payload = "{\"player\":" + J(p.GetPlayerName()) + ",\"id\":" + J(id) + ",\"time\":" + J(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")) + ",\"text\":" + J(output.ToString().TrimEnd()) + "}";
                var rpc = ZRoutedRpc.instance;
                if (rpc != null && ZNet.instance != null) rpc.InvokeRoutedRPC(ZRoutedRpc.Everybody, InfoRpc, payload); else OnInfo(ZDOMan.GetSessionID(), payload);
            }
            output = null;
        }

        static StringBuilder output; // what the running script reports back (requester side)
        static string runLevel = "fun"; // policy level of the requester, sent by the host in the script header

        // Progression: Claude must not hand out or build what the player has not unlocked yet. A line ending with
        // "anyway" skips the check, but only for a player whose policy level is "all" (the host).
        static string[] Progression(Player p, GameObject prefab, ref string[] a)
        {
            bool anyway = a.Length > 0 && a[a.Length - 1] == "anyway";
            if (anyway) Array.Resize(ref a, a.Length - 1);
            if (anyway && runLevel == "all") return a;
            var piece = prefab.GetComponent<Piece>();
            var item = prefab.GetComponent<ItemDrop>();
            string why = null;
            // Some items also carry a Piece (feasts such as LoxPie, MeatPlatter): they are checked as items.
            if (piece != null && item == null)
            {
                if (!p.IsRecipeKnown(piece.m_name)) why = "piece not unlocked yet";
                else foreach (var res in piece.m_resources)
                    if (res.m_resItem != null && !p.IsMaterialKnown(res.m_resItem.m_itemData.m_shared.m_name)) { why = "material " + res.m_resItem.m_itemData.m_shared.m_name + " not discovered yet"; break; }
            }
            else if (item != null)
            {
                string sn = item.m_itemData.m_shared.m_name;
                bool crafted = ObjectDB.instance != null && ObjectDB.instance.GetRecipe(item.m_itemData) != null;
                if (crafted ? !p.IsRecipeKnown(sn) : !p.IsMaterialKnown(sn)) why = crafted ? "recipe not unlocked yet" : "material not discovered yet";
            }
            if (why != null) throw new Exception($"progression: {prefab.name} is {why} for {p.GetPlayerName()} (host may add 'anyway')");
            return a;
        }
        static void Report(string line) { output?.AppendLine(line); }

        // Host side: information coming back from a requester, one JSON line per script in claude\info.jsonl.
        static void OnInfo(long sender, string payload)
        {
            if (!Host.Value) return;
            try { Directory.CreateDirectory(Dir); File.AppendAllText(Info, payload + "\n"); }
            catch (Exception e) { Main.Log.Warning("Claude info: " + e.Message); }
        }

        // ---------- Policy (host side): the script is trimmed to what the requester may get ----------

        static readonly Dictionary<string, string> ActionLevel = new Dictionary<string, string>
        {
            ["say"] = "fun", ["center"] = "fun", ["chat"] = "fun", ["weather"] = "fun",
            ["spawn"] = "spawn", ["give"] = "give", ["place"] = "build", ["placerel"] = "build",
            ["tp"] = "give", ["call"] = "call", ["repair"] = "build", ["destroy"] = "build", ["shout"] = "spawn", ["scan"] = "fun", ["look"] = "fun", ["remove"] = "build", ["stand"] = "build", ["clearitems"] = "build", ["terrain"] = "build",
            ["prefabs"] = "fun", ["known"] = "fun", ["info"] = "fun", ["where"] = "fun", ["kill"] = "spawn", ["heal"] = "give", ["tame"] = "spawn", ["push"] = "spawn",
            ["fly"] = "give", ["god"] = "give", ["time"] = "fun", ["on"] = "call", ["set"] = "call",
        };

        // "default=fun; Clothilde=all; Bob=fun,spawn" -> levels of a player (comma separated), "all" or "none".
        static string Level(string player)
        {
            // The host (this machine's player) always has everything, unless the policy names it explicitly.
            string def = "fun", mine = null;
            foreach (var part in (Policy.Value ?? "").Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq < 0) continue;
                string name = part.Substring(0, eq).Trim(), levels = part.Substring(eq + 1).Trim().ToLowerInvariant();
                if (name.Equals("default", StringComparison.OrdinalIgnoreCase)) def = levels;
                else if (name.Equals(player, StringComparison.OrdinalIgnoreCase)) mine = levels;
            }
            if (mine != null) return mine;
            // The host (this machine's player) has everything unless the policy names it explicitly.
            var me = Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerName() : null;
            return me != null && me.Equals(player, StringComparison.OrdinalIgnoreCase) ? "all" : def;
        }

        static bool Allowed(string player, string action)
        {
            string levels = Level(player);
            if (levels == "all") return true;
            if (levels == "none") return false;
            if (!ActionLevel.TryGetValue(action, out var needed)) return false; // unknown action: never
            foreach (var l in levels.Split(',')) if (l.Trim() == needed) return true;
            return false;
        }

        static string Filter(string script, string player)
        {
            var kept = new List<string>(); var refused = new List<string>();
            foreach (var raw in script.Replace("\r", "").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int sp = line.IndexOf(' ');
                string cmd = (sp < 0 ? line : line.Substring(0, sp)).ToLowerInvariant();
                if (Allowed(player, cmd)) kept.Add(line); else refused.Add(cmd);
            }
            if (refused.Count > 0)
            {
                refused.Sort(); var uniq = new List<string>(); foreach (var r in refused) if (!uniq.Contains(r)) uniq.Add(r);
                kept.Add("say not allowed for you: " + string.Join(", ", uniq) + " (ask " + (Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerName() : "the host") + ")");
                Main.Log.Msg($"Claude policy: {player} ({Level(player)}) refused: {string.Join(", ", uniq)}");
            }
            return string.Join("\n", kept);
        }

        // ---------- Actions ----------

        static void Execute(Player p, string line)
        {
            int sp = line.IndexOf(' ');
            string cmd = (sp < 0 ? line : line.Substring(0, sp)).ToLowerInvariant();
            string rest = sp < 0 ? "" : line.Substring(sp + 1).Trim();
            var a = rest.Length == 0 ? new string[0] : rest.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            switch (cmd)
            {
                case "say": // local chat line, whisper style; the chat window is shown for a few seconds as for a real message
                    if (Chat.instance != null) { Chat.instance.AddString("Claude", rest, Talker.Type.Whisper); HideTimer?.SetValue(Chat.instance, 0f); }
                    break;
                case "center": p.Message(MessageHud.MessageType.Center, rest); break;
                case "chat": Chat.instance?.SendText(Talker.Type.Normal, rest); break;
                case "spawn":
                {
                    var prefab = Prefab(a[0]);
                    int n = a.Length > 1 && int.TryParse(a[1], out var k) ? Mathf.Clamp(k, 1, 50) : 1;
                    var where = Where(p, a, 2);
                    for (int i = 0; i < n; i++)
                    {
                        var pos = where + (n > 1 ? new Vector3(UnityEngine.Random.Range(-1.5f, 1.5f), 0f, UnityEngine.Random.Range(-1.5f, 1.5f)) : Vector3.zero);
                        pos.y = GroundAt(pos) + 0.2f;
                        UnityEngine.Object.Instantiate(prefab, pos, Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f));
                    }
                    break;
                }
                case "give":
                {
                    var prefab = Prefab(a[0]); a = Progression(p, prefab, ref a);
                    int n = a.Length > 1 && int.TryParse(a[1], out var k) ? Mathf.Clamp(k, 1, 999) : 1;
                    int q = a.Length > 2 && int.TryParse(a[2], out var qq) ? Mathf.Clamp(qq, 1, 4) : 1;
                    var item = p.GetInventory().AddItem(prefab.name, n, q, 0, 0L, "Claude", false);
                    if (item == null)
                    {
                        var drop = UnityEngine.Object.Instantiate(prefab, p.transform.position + p.transform.forward + Vector3.up, Quaternion.identity).GetComponent<ItemDrop>();
                        if (drop != null) { drop.m_itemData.m_stack = Mathf.Min(n, drop.m_itemData.m_shared.m_maxStackSize); drop.SetQuality(q); }
                    }
                    else p.Message(MessageHud.MessageType.TopLeft, "$msg_added " + item.m_shared.m_name, n, item.GetIcon());
                    break;
                }
                case "place":
                {
                    var prefab = Prefab(a[0]); a = Progression(p, prefab, ref a);
                    var pos = new Vector3(Pf(a[1]), 0f, Pf(a[3]));
                    pos.y = a[2] == "g" ? GroundAt(pos) : a[2].StartsWith("g+") ? GroundAt(pos) + Pf(a[2].Substring(2)) : Pf(a[2]); // "g" = ground level
                    float rot = a.Length > 4 ? Pf(a[4]) : 0f;
                    UnityEngine.Object.Instantiate(prefab, pos, Quaternion.Euler(0f, rot, 0f));
                    break;
                }
                case "placerel":
                {
                    var prefab = Prefab(a[0]); a = Progression(p, prefab, ref a);
                    var fwd = p.transform.forward; fwd.y = 0f; fwd.Normalize();
                    var right = Vector3.Cross(Vector3.up, fwd);
                    var pos = p.transform.position + right * Pf(a[1]) + Vector3.up * Pf(a[2]) + fwd * Pf(a[3]);
                    float yaw = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg + (a.Length > 4 ? Pf(a[4]) : 0f);
                    UnityEngine.Object.Instantiate(prefab, pos, Quaternion.Euler(0f, yaw, 0f));
                    break;
                }
                case "tp": p.TeleportTo(new Vector3(Pf(a[0]), Pf(a[1]), Pf(a[2])), p.transform.rotation, true); break;
                case "call": Report("call " + rest + " => " + CallCommand.Eval(rest)); break;
                case "scan_old": // superseded by ClaudeVerbs.Scan (selector language); kept for reference
                {
                    float r = 15f; var pos = p.transform.position; float yMin = float.MinValue, yMax = float.MaxValue; string like = null;
                    for (int i = 0; i < a.Length; i++)
                    {
                        if (a[i] == "at" && i + 3 < a.Length) { pos = new Vector3(Pf(a[i + 1]), Pf(a[i + 2]), Pf(a[i + 3])); i += 3; }
                        else if (a[i] == "y" && i + 2 < a.Length) { yMin = Pf(a[i + 1]); yMax = Pf(a[i + 2]); i += 2; }
                        else if (a[i] == "like" && i + 1 < a.Length) { like = a[i + 1]; i++; }
                        else if (float.TryParse(a[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float rr)) r = rr;
                    }
                    int n = 0, shown = 0; const int cap = 1200;
                    Report($"scan {r:0} m around {F(pos.x)} {F(pos.y)} {F(pos.z)}{(yMin > float.MinValue ? $" y {F(yMin)}..{F(yMax)}" : "")}{(like != null ? " like " + like : "")}; player at {F(p.transform.position.x)} {F(p.transform.position.y)} {F(p.transform.position.z)} facing {F(p.transform.forward.x)} {F(p.transform.forward.z)}; ground {F(GroundAt(pos))}");
                    var flat = pos; // horizontal distance, so that a tall building is fully covered
                    foreach (var w in UnityEngine.Object.FindObjectsByType<WearNTear>(FindObjectsSortMode.None))
                    {
                        if (w == null) continue;
                        var t = w.transform;
                        var d = t.position - flat; d.y = 0f;
                        if (d.magnitude > r || t.position.y < yMin || t.position.y > yMax) continue;
                        string name = w.gameObject.name.Replace("(Clone)", "");
                        if (like != null && name.IndexOf(like, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        n++;
                        if (shown < cap) { shown++; Report($"piece {name} {F(t.position.x)} {F(t.position.y)} {F(t.position.z)} rot {t.eulerAngles.y:0} health {Mathf.RoundToInt(w.GetHealthPercentage() * 100)}% support {Mathf.RoundToInt(Support(w) * 100)}%"); }
                    }
                    Report(shown < n ? $"{n} piece(s), {shown} shown (cap {cap}): narrow with a smaller radius, y min max or like" : $"{n} piece(s)");
                    break;
                }
                case "look": // what the requester is looking at
                {
                    var go = p.GetHoverObject();
                    var root = go != null ? go.GetComponentInParent<ZNetView>() : null;
                    if (root != null) go = root.gameObject;
                    Report(go == null ? "look: nothing" : $"look: {go.name.Replace("(Clone)", "")} at {F(go.transform.position.x)} {F(go.transform.position.y)} {F(go.transform.position.z)} rot {go.transform.eulerAngles.y:0}");
                    break;
                }
                case "weather": // local only: each client computes its own weather from the world time
                    if (EnvMan.instance != null) EnvMan.instance.SetForceEnvironment(rest.Equals("normal", StringComparison.OrdinalIgnoreCase) ? "" : rest);
                    break;
                case "repair": // repairs every damaged build piece around the requester (default 10 m)
                {
                    float r = a.Length > 0 ? Pf(a[0]) : 10f; int n = 0;
                    foreach (var w in UnityEngine.Object.FindObjectsByType<WearNTear>(FindObjectsSortMode.None))
                        if (w != null && Vector3.Distance(w.transform.position, p.transform.position) <= r && w.Repair()) n++;
                    p.Message(MessageHud.MessageType.TopLeft, n + " piece(s) repaired");
                    break;
                }
                case "destroy": // removes what the requester is looking at (or the closest destructible within 5 m in front): rocks, trees, build pieces
                {
                    GameObject target = p.GetHoverObject();
                    if (target == null)
                    {
                        float best = 5f; var center = p.transform.position + p.transform.forward * 2f;
                        foreach (var nv in UnityEngine.Object.FindObjectsByType<ZNetView>(FindObjectsSortMode.None))
                        {
                            if (nv == null || !nv.IsValid() || nv.GetComponent<Character>() != null || nv.GetComponent<ItemDrop>() != null || nv.GetComponent<Player>() != null) continue;
                            if (nv.GetComponent<IDestructible>() == null) continue;
                            float d = Vector3.Distance(nv.transform.position, center);
                            if (d < best) { best = d; target = nv.gameObject; }
                        }
                    }
                    if (target == null) throw new Exception("nothing to destroy here");
                    var view = target.GetComponentInParent<ZNetView>();
                    if (view == null || !view.IsValid() || view.GetComponentInParent<Character>() != null) throw new Exception("not a destroyable object");
                    string what = view.gameObject.name;
                    var destructible = view.GetComponent<Destructible>();
                    var piece = view.GetComponent<WearNTear>();
                    if (destructible != null) destructible.Destroy();
                    else if (piece != null) piece.Remove(true); // removed without giving materials back
                    else { view.ClaimOwnership(); view.Destroy(); } // rocks (MineRock5...): removed without drops
                    p.Message(MessageHud.MessageType.TopLeft, "destroyed: " + what.Replace("(Clone)", ""));
                    break;
                }
                case "shout": // FUS RO DAH: everything in a cone in front is thrown away ([force] [radius], defaults 60 and 12 m)
                {
                    float force = a.Length > 0 ? Pf(a[0]) : 60f, radius = a.Length > 1 ? Pf(a[1]) : 12f; int n = 0;
                    var fwd = p.transform.forward; fwd.y = 0f; fwd.Normalize();
                    foreach (var c in Character.GetAllCharacters())
                    {
                        if (c == null || c == p) continue;
                        var d = c.transform.position - p.transform.position; d.y = 0f;
                        if (d.magnitude > radius || Vector3.Angle(fwd, d) > 45f) continue;
                        var nv = c.GetComponent<ZNetView>(); if (nv != null && nv.IsValid() && !nv.IsOwner()) nv.ClaimOwnership();
                        c.ApplyPushback((d.normalized + Vector3.up * 0.6f).normalized, force * (1f - 0.5f * d.magnitude / radius));
                        c.Stagger(-d.normalized); n++;
                    }
                    foreach (var rb in UnityEngine.Object.FindObjectsByType<Rigidbody>(FindObjectsSortMode.None))
                    {
                        if (rb == null || rb.isKinematic || rb.GetComponentInParent<Character>() != null) continue;
                        var d = rb.transform.position - p.transform.position; d.y = 0f;
                        if (d.magnitude > radius || Vector3.Angle(fwd, d) > 45f) continue;
                        var nv = rb.GetComponentInParent<ZNetView>(); if (nv != null && nv.IsValid() && !nv.IsOwner()) nv.ClaimOwnership();
                        rb.AddForce((d.normalized + Vector3.up * 0.6f).normalized * force * 8f, ForceMode.Impulse); n++;
                    }
                    Chat.instance?.SendText(Talker.Type.Shout, "FUS RO DAH");
                    p.Message(MessageHud.MessageType.TopLeft, n + " thing(s) blown away");
                    break;
                }
                case "remove": // remove <x> <y> <z> [prefab part] [radius]: closest piece to a point; any other form goes to the selector language
                {
                    if (a.Length < 3 || !float.TryParse(a[0], NumberStyles.Float, CultureInfo.InvariantCulture, out _)) { ClaudeVerbs.TryExecute(p, "remove", a, Report); break; }
                    var at = new Vector3(Pf(a[0]), Pf(a[1]), Pf(a[2]));
                    string like = a.Length > 3 && !float.TryParse(a[3], NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? a[3] : null;
                    float maxD = a.Length > 4 ? Pf(a[4]) : a.Length > 3 && like == null ? Pf(a[3]) : 1.5f;
                    WearNTear best = null; float bestD = maxD;
                    foreach (var w in UnityEngine.Object.FindObjectsByType<WearNTear>(FindObjectsSortMode.None))
                    {
                        if (w == null) continue;
                        if (like != null && w.gameObject.name.IndexOf(like, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        float d = Vector3.Distance(w.transform.position, at);
                        if (d < bestD) { bestD = d; best = w; }
                    }
                    if (best == null) throw new Exception($"no piece within {maxD} m of {F(at.x)} {F(at.y)} {F(at.z)}");
                    string nm = best.gameObject.name.Replace("(Clone)", "");
                    var nv = best.GetComponent<ZNetView>(); if (nv != null && nv.IsValid() && !nv.IsOwner()) nv.ClaimOwnership();
                    best.Remove(true); // no materials back: what Claude builds costs nothing
                    Report($"removed {nm} at {F(best.transform.position.x)} {F(best.transform.position.y)} {F(best.transform.position.z)} ({bestD:0.00} m from the point)");
                    break;
                }
                case "stand": // stand <x> <y> <z> <item prefab> [orientation 0-7]: puts an item on the closest item stand (within 1.5 m), no inventory involved
                {
                    var at = new Vector3(Pf(a[0]), Pf(a[1]), Pf(a[2]));
                    var prefab = Prefab(a[3]); a = Progression(p, prefab, ref a);
                    int orientation = a.Length > 4 ? (int)Pf(a[4]) : 0;
                    // The ItemStand component may sit on a child (or be inactive): look for the piece by its root ZNetView, then inside it.
                    ItemStand best = null; float bestD = 1.5f; int seen = 0;
                    foreach (var view in UnityEngine.Object.FindObjectsByType<ZNetView>(FindObjectsSortMode.None))
                    {
                        if (view == null) continue;
                        float d = Vector3.Distance(view.transform.position, at);
                        if (d >= bestD) continue;
                        var st = view.GetComponentInChildren<ItemStand>(true);
                        if (st == null) continue;
                        seen++; bestD = d; best = st;
                    }
                    if (best == null) throw new Exception($"no item stand within 1.5 m of {F(at.x)} {F(at.y)} {F(at.z)}");
                    var nv = best.GetComponentInParent<ZNetView>();
                    if (nv == null || !nv.IsValid()) throw new Exception("item stand not ready");
                    if (best.HaveAttachment()) throw new Exception("item stand already holds something");
                    var drop = prefab.GetComponent<ItemDrop>() ?? throw new Exception(a[3] + " is not an item");
                    var item = drop.m_itemData.Clone();
                    item.m_dropPrefab = prefab; item.m_stack = 1; item.m_equipped = false;
                    if (!nv.IsOwner()) nv.ClaimOwnership();
                    int hash = prefab.name.GetStableHashCode();
                    nv.GetZDO().Set(ZDOVars.s_item, hash);
                    ItemDrop.SaveToZDO(item, nv.GetZDO());
                    nv.InvokeRPC(ZNetView.Everybody, "SetVisualItem", hash, item.m_variant, item.m_quality, orientation);
                    Report($"stand: {prefab.name} on {best.gameObject.name.Replace("(Clone)", "")} at {F(best.transform.position.x)} {F(best.transform.position.y)} {F(best.transform.position.z)}");
                    break;
                }
                case "clearitems": // clearitems [radius] [at x y z] [like <item part>]: removes the loose items on the ground (horizontal radius, default 5 m)
                {
                    float r = 5f; var pos = p.transform.position; string like = null;
                    for (int i = 0; i < a.Length; i++)
                    {
                        if (a[i] == "at" && i + 3 < a.Length) { pos = new Vector3(Pf(a[i + 1]), Pf(a[i + 2]), Pf(a[i + 3])); i += 3; }
                        else if (a[i] == "like" && i + 1 < a.Length) { like = a[i + 1]; i++; }
                        else if (float.TryParse(a[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float rr)) r = rr;
                    }
                    int n = 0;
                    foreach (var drop in UnityEngine.Object.FindObjectsByType<ItemDrop>(FindObjectsSortMode.None))
                    {
                        if (drop == null) continue;
                        var d = drop.transform.position - pos; d.y = 0f;
                        if (d.magnitude > r) continue;
                        string nm = drop.gameObject.name.Replace("(Clone)", "");
                        if (like != null && nm.IndexOf(like, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        var nv = drop.GetComponent<ZNetView>();
                        if (nv == null || !nv.IsValid()) continue;
                        if (!nv.IsOwner()) nv.ClaimOwnership();
                        nv.Destroy(); n++;
                    }
                    Report($"clearitems: {n} item(s) removed within {r:0} m of {F(pos.x)} {F(pos.y)} {F(pos.z)}");
                    p.Message(MessageHud.MessageType.TopLeft, n + " item(s) cleaned up");
                    break;
                }
                case "terrain": // terrain ops | terrain <op> <x> <y> <z> [times]: one of the game's terrain operations (the hoe's), placed at the given height
                {
                    // The game only transmits the operation's prefab name to the zone owner (never custom settings), so only the
                    // registered operations exist. "raise" raises the ground towards y by its delta per application (repeat with
                    // [times]); "path" levels and paints; the game caps changes at about +/- 8 m from the original ground.
                    if (ObjectDB.instance == null) throw new Exception("no ObjectDB");
                    if (a.Length == 0 || a[0] == "ops")
                    {
                        var names = new List<string>();
                        foreach (var t in ObjectDB.instance.m_terrainOps) if (t != null) names.Add(t.gameObject.name + "(" + (t.m_settings.m_raise ? "raise " + t.m_settings.m_raiseDelta.ToString("0.#", CultureInfo.InvariantCulture) + "m r" + t.m_settings.m_raiseRadius.ToString("0.#", CultureInfo.InvariantCulture) : t.m_settings.m_level ? "level r" + t.m_settings.m_levelRadius.ToString("0.#", CultureInfo.InvariantCulture) : t.m_settings.m_smooth ? "smooth" : "paint") + ")");
                        Report("terrain ops: " + string.Join(", ", names));
                        break;
                    }
                    TerrainOp template = null;
                    foreach (var t in ObjectDB.instance.m_terrainOps) if (t != null && t.gameObject.name.Equals(a[0], StringComparison.OrdinalIgnoreCase)) template = t;
                    if (template == null) throw new Exception("unknown terrain op '" + a[0] + "' (see: terrain ops)");
                    var at = new Vector3(Pf(a[1]), Pf(a[2]), Pf(a[3]));
                    int times = a.Length > 4 ? Mathf.Clamp((int)Pf(a[4]), 1, 20) : 1;
                    var hm = Heightmap.FindHeightmap(at);
                    if (hm == null) throw new Exception("no terrain loaded there");
                    // The operation is sent to the owner of the zone's terrain data; far from the players nobody may own it,
                    // so the requester takes it over first (as the game does for anything it modifies).
                    var comp = hm.GetAndCreateTerrainCompiler();
                    var cnv = comp != null ? comp.GetComponent<ZNetView>() : null;
                    if (cnv != null && cnv.IsValid() && !cnv.IsOwner()) cnv.ClaimOwnership();
                    for (int i = 0; i < times; i++) UnityEngine.Object.Instantiate(template.gameObject, at, Quaternion.identity); // Awake applies it through the zone's TerrainComp (synced) and destroys it
                    Report($"terrain {template.gameObject.name} x{times} at {F(at.x)} {F(at.y)} {F(at.z)}: ground was {F(GroundAt(at))} (updates within a second; caps at about 8 m from the original ground)");
                    break;
                }
                case "ground": break;
                default:
                    if (!ClaudeVerbs.TryExecute(p, cmd, a, Report)) throw new Exception("unknown action '" + cmd + "'");
                    break;
            }
        }

        static float Pf(string s) => float.Parse(s, CultureInfo.InvariantCulture);

        // Structural support of a build piece, 0..1 (1 = fully supported); the game destroys a piece whose support drops to 0.
        static readonly System.Reflection.FieldInfo SupportField = AccessTools.Field(typeof(WearNTear), "m_support");
        static readonly System.Reflection.MethodInfo MaxSupportMethod = AccessTools.Method(typeof(WearNTear), "GetMaxSupport");
        static float Support(WearNTear w)
        {
            try
            {
                float sup = (float)SupportField.GetValue(w);
                float max = MaxSupportMethod != null ? (float)MaxSupportMethod.Invoke(w, null) : 1f;
                return max > 0f ? Mathf.Clamp01(sup / max) : 1f;
            }
            catch { return 1f; }
        }

        static GameObject Prefab(string name)
        {
            var go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(name) : null;
            if (go == null && ObjectDB.instance != null) go = ObjectDB.instance.GetItemPrefab(name);
            return go ?? throw new Exception("prefab '" + name + "' not found");
        }

        static Vector3 Where(Player p, string[] a, int from)
        {
            if (a.Length <= from || a[from] == "here") return p.transform.position + p.transform.forward * (a.Length <= from ? 3f : 0f);
            if (a[from].StartsWith("front:")) return p.transform.position + p.transform.forward * Pf(a[from].Substring(6));
            if (a.Length >= from + 3) return new Vector3(Pf(a[from]), Pf(a[from + 1]), Pf(a[from + 2]));
            throw new Exception("bad position");
        }

        static float GroundAt(Vector3 pos)
        {
            if (ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(pos, out float h)) return h;
            return pos.y;
        }
    }
}
