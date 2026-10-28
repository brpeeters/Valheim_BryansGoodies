using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace BryansGoodies
{
    // Update mechanics, living in the core so that they are hot-reloadable like everything else.
    // The loader (BryansGoodies.dll in Mods) stays minimal and offers a few services through a dictionary
    // handed over at start ("host"): where the files are, "core.install" (swap to a downloaded core),
    // "loader.install" (put a new loader in place for the next launch). See Loader\Loader.cs.
    //
    // The feed is a directory (URL or local path) holding: version.txt, BryansGoodies.Core.dll, loader.txt,
    // BryansGoodies.dll. By default it is the dist/ folder of the GitHub repository on the branch named by the
    // Channel setting ("main" for everyone, "beta" for volunteers).
    //   version.txt : first line = core version; a line "force" installs that version even if older (rollback for
    //                 everyone); other lines = release notes shown in game.
    //   loader.txt  : first line = loader version. A newer loader is downloaded and put in place for the next launch.
    //
    // When to check the feed:
    //  - at start (game launch, or right after a hot reload: cheap, and it catches a version published meanwhile);
    //  - when another player on the same channel announces a newer version (network message "BG_Version",
    //    "version|channel", sent by each client on the GitHub feed when its core starts and then every minute;
    //    the server relays it without installing anything; a local feed never announces its own build);
    //  - when the local feed's version.txt changes (local path only), or a "check-now" file appears in UserData\BryansGoodies;
    //  - as a fallback, every CheckIntervalMinutes (0 = never); after a failure, again 90 s later (3 tries).
    // Other trigger files in UserData\BryansGoodies: "announce" (publish.cmd: announce the version just published on
    // GitHub to the other players, even from a local feed), "notice" (show its text as a loader notice).
    public static class Updater
    {
        const string RpcName = "BG_Version";
        const string DefaultFeedTemplate = "https://raw.githubusercontent.com/brpeeters/Valheim_BryansGoodies/{channel}/dist/";

        public static MelonPreferences_Category Cat;
        public static MelonPreferences_Entry<bool> AutoUpdate, UpdateLoader;
        public static MelonPreferences_Entry<string> Channel, Feed, BadVersion;
        public static MelonPreferences_Entry<float> CheckIntervalMinutes;

        // Services and paths from the loader.
        static Dictionary<string, object> host;
        static string CoreDir => host != null && host.TryGetValue("core.dir", out var d) ? (string)d : Path.Combine(MelonUtils.UserDataDirectory, "BryansGoodies");
        static string CorePath => Path.Combine(CoreDir, "BryansGoodies.Core.dll");
        static string LoaderVersion => host != null && host.TryGetValue("loader.version", out var v) ? (string)v : "0.0.0";
        static string LoaderPath => host != null && host.TryGetValue("loader.path", out var p) ? p as string : null;

        // Download thread <-> main thread.
        static readonly object sync = new object();
        static volatile bool checking;
        static string readyPath, readyVersion, readyNotes; // downloaded core, validated, to be installed by the loader
        static readonly Queue<string> messages = new Queue<string>();
        static float nextCheck, nextBroadcast, lastRemoteCheck = -1000f, retryAt = float.MaxValue;
        static int retries;
        static string loaderStaged;    // loader version already put in place for the next launch
        static string announceVersion; // published version to announce on behalf of a local-feed client (publish.cmd)
        static float announceUntil;
        static ZRoutedRpc registeredRpc;
        static readonly FieldInfo RpcFunctions = AccessTools.Field(typeof(ZRoutedRpc), "m_functions");

        // Banner at the bottom of the screen: what is going on right now (nothing when idle). Events go to the center text.
        static string phase;    // set by the download thread: "checking", "downloading x", "installing x"...
        static GUIStyle bannerStyle;

        public static void Init()
        {
            Cat = MelonPreferences.CreateCategory("BryansGoodiesLoader", "Bryan's Goodies - updates");
            AutoUpdate = Prefs.Entry(Cat, "AutoUpdate", true, "Download new versions automatically");
            UpdateLoader = Prefs.Entry(Cat, "UpdateLoader", true, "Also update the loader itself (active at the next launch)");
            Channel = Prefs.Entry(Cat, "Channel", "main", "Branch of the GitHub repository to follow (main, beta...)");
            Feed = Prefs.Entry(Cat, "Feed", "", "Feed directory override: URL or local path holding version.txt and the DLLs (empty = GitHub, Channel branch)");
            CheckIntervalMinutes = Prefs.Entry(Cat, "CheckIntervalMinutes", 30f, "Fallback check (minutes, 0 = never)");
            BadVersion = Prefs.Entry(Cat, "BadVersion", "", "Core version that failed to start (skipped unless forced)");
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        public static void Start(Dictionary<string, object> h)
        {
            host = h;
            if (host != null && host.TryGetValue("lastEvent", out var ev) && ev is string s && s.Length > 0) { messages.Enqueue(s); host["lastEvent"] = null; }
            nextCheck = Time.unscaledTime + 3f; // a check shortly after every start: launch, hot reload...
            nextBroadcast = 0f;
            Main.Log.Msg($"Updater ready. Feed: {FeedBase()}, loader {LoaderVersion}");
        }

        // ---------- State handed over between versions ----------

        public static void Export(Dictionary<string, object> h)
        {
            h["updater"] = new object[] { announceVersion, announceUntil, loaderStaged, lastRemoteCheck };
            h["updater.messages"] = new List<string>(messages);
        }

        public static void Import(Dictionary<string, object> h)
        {
            if (h.TryGetValue("updater", out var o) && o is object[] u)
            {
                announceVersion = u[0] as string; announceUntil = (float)u[1]; loaderStaged = u[2] as string;
                lastRemoteCheck = (float)u[3];
            }
            if (h.TryGetValue("updater.messages", out var m) && m is List<string> list) foreach (var s in list) messages.Enqueue(s);
            if (h.TryGetValue("updater.notes", out var n)) // we are the version that was just installed
                messages.Enqueue($"Bryan's Goodies updated to {Main.Version}{(n is string notes && notes.Length > 0 ? ": " + notes : "")}");
        }

        // ---------- Feed ----------

        static string LocalPath(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            if (url.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return new Uri(url).LocalPath;
            return url.Length > 1 && (url[1] == ':' || url.StartsWith("\\\\")) ? url : null;
        }

        static string FeedBase()
        {
            string f = string.IsNullOrEmpty(Feed.Value) ? DefaultFeedTemplate.Replace("{channel}", Channel.Value.Trim()) : Feed.Value.Trim();
            return f.EndsWith("/") || f.EndsWith("\\") ? f : f + (LocalPath(f) != null ? "\\" : "/");
        }

        static string FeedFile(string name) => FeedBase() + name;

        static void ParseVersionFile(string text, out string version, out bool force, out string notes)
        {
            var lines = text.Replace("\r", "").Split('\n');
            version = lines[0].Trim(); force = false; var n = new List<string>();
            for (int i = 1; i < lines.Length; i++)
            {
                string l = lines[i].Trim();
                if (l.Length == 0) continue;
                if (l.Equals("force", StringComparison.OrdinalIgnoreCase)) force = true; else n.Add(l);
            }
            notes = n.Count > 0 ? string.Join(" ", n) : null;
        }

        static bool Newer(string a, string b) => System.Version.TryParse(a?.Trim(), out var va) && System.Version.TryParse(b?.Trim(), out var vb) && va > vb;
        static bool Same(string a, string b) => System.Version.TryParse(a?.Trim(), out var va) && System.Version.TryParse(b?.Trim(), out var vb) && va == vb;

        // ---------- Loop ----------

        public static void Update()
        {
            float now = Time.unscaledTime;
            ShowMessages();
            Network(now);

            if (AutoUpdate.Value && !checking && readyPath == null)
            {
                if (now >= nextCheck)
                {
                    float minutes = CheckIntervalMinutes.Value;
                    nextCheck = minutes > 0f ? now + minutes * 60f : float.MaxValue;
                    retries = 0;
                    StartCheck("periodic check");
                }
                else if (now >= retryAt) { retryAt = float.MaxValue; StartCheck($"retry {retries}"); }
                else if (TakeTrigger("check-now") != null) { retries = 0; StartCheck("check-now file"); }
                else if (LocalFeedChanged()) { retries = 0; StartCheck("local version file changed"); }
            }

            string notice = TakeTrigger("notice");
            if (!string.IsNullOrEmpty(notice)) messages.Enqueue(notice);
            if (TakeTrigger("announce") != null) AnnouncePublished();

            // A validated core is ready: ask the loader to swap. Our StopWithState() will carry the notes to the next version.
            if (readyPath != null && host != null && host.TryGetValue("core.install", out var install) && install is Action<string> doInstall)
            {
                string path; lock (sync) { path = readyPath; readyPath = null; }
                pendingNotes = readyNotes ?? "";
                Main.Log.Msg($"Update: installing {readyVersion}.");
                doInstall(path); // deferred by the loader to its own update, after ours
            }
        }

        static string pendingNotes;
        public static void ExportNotes(Dictionary<string, object> h) { if (pendingNotes != null) h["updater.notes"] = pendingNotes; }

        // Trigger file in UserData\BryansGoodies: returns its content ("" if empty) and deletes it, or null if absent.
        static float nextTriggerPoll;
        static readonly Dictionary<string, string> triggers = new Dictionary<string, string>();
        static string TakeTrigger(string name)
        {
            float now = Time.unscaledTime;
            if (now >= nextTriggerPoll)
            {
                nextTriggerPoll = now + 1f;
                foreach (var n in new[] { "check-now", "notice", "announce" })
                {
                    string p = Path.Combine(CoreDir, n);
                    if (!File.Exists(p)) continue;
                    try { triggers[n] = File.ReadAllText(p).Trim(); File.Delete(p); } catch { }
                }
            }
            if (!triggers.TryGetValue(name, out var text)) return null;
            triggers.Remove(name);
            return text;
        }

        // Local feed: version.txt is watched (polled every second, cheap for a local file).
        static DateTime feedWriteTime;
        static float nextFeedPoll;
        static bool LocalFeedChanged()
        {
            float now = Time.unscaledTime;
            if (now < nextFeedPoll) return false;
            nextFeedPoll = now + 1f;
            string path = LocalPath(FeedFile("version.txt"));
            if (path == null || !File.Exists(path)) return false;
            var t = File.GetLastWriteTimeUtc(path);
            if (feedWriteTime == default) { feedWriteTime = t; return false; } // first sight: the start check covers it
            if (t == feedWriteTime) return false;
            feedWriteTime = t;
            return true;
        }

        // ---------- Events: center text of the game. Status: banner at the bottom while something is going on ----------

        static void ShowMessages()
        {
            if (messages.Count == 0 || MessageHud.instance == null || Player.m_localPlayer == null) return;
            lock (sync)
                while (messages.Count > 0)
                {
                    string m = messages.Dequeue();
                    MessageHud.instance.ShowMessage(MessageHud.MessageType.Center, m);
                    Main.Log.Msg("Notice: " + m);
                }
        }

        static string Status()
        {
            float now = Time.unscaledTime;
            if (phase != null) return "Bryan's Goodies: " + phase;
            if (readyPath != null) return $"Bryan's Goodies: installing {readyVersion}";
            if (retryAt != float.MaxValue) return $"Bryan's Goodies: update failed, retry in {Mathf.Max(0, Mathf.CeilToInt(retryAt - now))} s";
            if (loaderStaged != null) return $"Bryan's Goodies: loader {loaderStaged} ready, restart the game to use it";
            if (announceVersion != null && now < announceUntil) return $"Bryan's Goodies: telling the other players about {announceVersion} ({Mathf.CeilToInt((announceUntil - now) / 60f)} min left)";
            return null;
        }

        public static void OnGUI()
        {
            if (Event.current.type != EventType.Repaint || Player.m_localPlayer == null) return;
            string noticeLabel = Status();
            if (noticeLabel == null) return;
            if (bannerStyle == null) bannerStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            float s = Mathf.Max(1f, Screen.height / 1080f);
            bannerStyle.fontSize = Mathf.RoundToInt(15 * s);
            var r = new Rect(Screen.width / 2f - 400 * s, Screen.height - 48 * s, 800 * s, 24 * s);
            GUI.color = new Color(0f, 0f, 0f, 0.8f);
            GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), noticeLabel, bannerStyle);
            GUI.color = new Color(1f, 0.85f, 0.5f, 1f);
            GUI.Label(r, noticeLabel, bannerStyle);
            GUI.color = Color.white;
        }

        // ---------- Version announcement between players ----------

        static void Network(float now)
        {
            var rpc = ZRoutedRpc.instance;
            if (rpc == null) { registeredRpc = null; return; }
            if (rpc != registeredRpc)
            {
                registeredRpc = rpc;
                // The game rejects a duplicate registration: after a hot reload, replace the old one.
                (RpcFunctions?.GetValue(rpc) as System.Collections.IDictionary)?.Remove(RpcName.GetStableHashCode());
                rpc.Register<string>(RpcName, OnRemoteVersion);
                nextBroadcast = now + 5f;
            }
            // Only versions from the shared GitHub feed are announced: a build under test on a local feed must not send the
            // other players looking for something not published yet. After publish.cmd, the published version is announced instead.
            if (now >= nextBroadcast && ZNet.instance != null)
            {
                nextBroadcast = now + 60f;
                string v = string.IsNullOrEmpty(Feed.Value) ? Main.Version : (now < announceUntil ? announceVersion : null);
                if (v != null) rpc.InvokeRoutedRPC(ZRoutedRpc.Everybody, RpcName, v + "|" + Channel.Value.Trim());
            }
        }

        static void OnRemoteVersion(long sender, string announced)
        {
            if (!AutoUpdate.Value || checking || readyPath != null) return;
            var parts = (announced ?? "").Split('|');
            string version = parts[0], channel = parts.Length > 1 ? parts[1] : "main";
            if (!string.IsNullOrEmpty(Feed.Value) || channel != Channel.Value.Trim()) return; // not our feed
            if (!Newer(version, Main.Version) || Same(version, BadVersion.Value)) return;
            float now = Time.unscaledTime;
            if (now - lastRemoteCheck < 120f) return; // no more than one attempt every 2 minutes
            lastRemoteCheck = now;
            Main.Log.Msg($"Another player has version {version} (we have {Main.Version}).");
            retries = 0;
            StartCheck("version announced by another player");
        }

        // Reads the version published on GitHub for our channel (ignoring a local Feed override) and announces it for 10 minutes.
        static void AnnouncePublished()
        {
            string url = DefaultFeedTemplate.Replace("{channel}", Channel.Value.Trim()) + "version.txt";
            Main.Log.Msg("Announce: reading the published version...");
            new Thread(() =>
            {
                for (int attempt = 1; attempt <= 5; attempt++)
                {
                    try
                    {
                        ParseVersionFile(Download(url), out string remote, out _, out _);
                        if (!System.Version.TryParse(remote, out _)) throw new Exception("bad version.txt");
                        announceVersion = remote;
                        announceUntil = Time.unscaledTime + 600f;
                        nextBroadcast = 0f;
                        lock (sync) messages.Enqueue($"Bryan's Goodies {remote} published, telling the other players");
                        return;
                    }
                    catch (Exception e) { Main.Log.Warning($"Announce: attempt {attempt} failed: {e.Message}"); Thread.Sleep(30000); }
                }
            }) { IsBackground = true, Name = "BryansGoodies.Announce" }.Start();
        }

        // ---------- Download (separate thread), then the loader installs (main thread) ----------

        static void StartCheck(string why)
        {
            checking = true;
            string current = Main.Version, bad = BadVersion.Value, newPath = CorePath + ".new", corePath = CorePath;
            bool localRebuild = why == "local version file changed" && LocalPath(FeedBase()) != null; // same version, new build
            string feedVersion = FeedFile("version.txt"), feedCore = FeedFile("BryansGoodies.Core.dll");
            string feedLoaderVersion = FeedFile("loader.txt"), feedLoader = FeedFile("BryansGoodies.dll");
            bool doLoader = UpdateLoader.Value;
            Main.Log.Msg($"Update: checking ({why})...");
            phase = "checking for updates";
            new Thread(() =>
            {
                bool failed = false;
                try
                {
                    ParseVersionFile(Download(feedVersion), out string remote, out bool force, out string notes);
                    bool wanted = !Same(remote, current) && (force || Newer(remote, current));
                    if (wanted && !force && Same(remote, bad)) { Main.Log.Warning($"Update: {remote} online but it failed here before; skipped until forced."); wanted = false; }
                    // Local feed, same version number: a rebuild while iterating. Installed if the file really differs.
                    bool rebuild = !wanted && localRebuild && Same(remote, current);
                    if (!wanted && !rebuild) Main.Log.Msg($"Update: version {remote} online, {current} installed, nothing to do.");
                    else
                    {
                        var bytes = DownloadBytes(feedCore);
                        if (rebuild)
                        {
                            bool unchanged = File.Exists(corePath) && SameBytes(bytes, File.ReadAllBytes(corePath));
                            if (unchanged) Main.Log.Msg($"Update: local build {remote} unchanged, nothing to do.");
                            else { Main.Log.Msg($"Update: local build {remote} changed, reloading it."); notes = "local build"; }
                            if (unchanged) bytes = null;
                        }
                        else Main.Log.Msg($"Update: {remote} available{(force ? " (forced)" : "")}, downloading...");
                        if (bytes != null)
                        {
                            phase = $"downloading {remote}";
                            File.WriteAllBytes(newPath, bytes);
                            var name = AssemblyName.GetAssemblyName(newPath); // checks that it really is a .NET assembly
                            if (name.Name != "BryansGoodies.Core") throw new Exception("the downloaded file is not BryansGoodies.Core.dll (" + name.Name + ")");
                            if (!Same(name.Version.ToString(3), remote)) throw new Exception($"downloaded file is {name.Version.ToString(3)}, feed says {remote} (cache not refreshed yet?)");
                            lock (sync) { readyPath = newPath; readyVersion = remote; readyNotes = notes; }
                            Main.Log.Msg($"Update: {remote} downloaded{(notes != null ? " - " + notes : "")}.");
                        }
                    }

                    if (doLoader)
                    {
                        try { SelfUpdateLoader(feedLoaderVersion, feedLoader); }
                        catch (Exception e) { Main.Log.Warning("Loader update failed: " + e.Message); }
                    }
                }
                catch (Exception e)
                {
                    failed = true;
                    Main.Log.Warning("Update failed: " + e.Message);
                    try { if (File.Exists(newPath)) File.Delete(newPath); } catch { }
                }
                finally
                {
                    // Whatever happened above, the status and the "checking" flag are always released here.
                    if (failed && retries < 3) { retries++; retryAt = Time.unscaledTime + 90f; Main.Log.Msg($"Update: retry {retries} in 90 s."); }
                    phase = null;
                    checking = false;
                }
            }) { IsBackground = true, Name = "BryansGoodies.Update" }.Start();
        }

        // A newer loader is downloaded next to the running one; the loader service renames files for the next launch.
        static void SelfUpdateLoader(string feedLoaderVersion, string feedLoader)
        {
            string remote;
            try { remote = Download(feedLoaderVersion).Replace("\r", "").Split('\n')[0].Trim(); }
            catch { return; } // no loader.txt in this feed: nothing to do
            if (!Newer(remote, LoaderVersion) || Same(remote, loaderStaged)) return;
            string me = LoaderPath;
            if (me == null || host == null || !host.TryGetValue("loader.install", out var inst) || !(inst is Action<string> install))
            { Main.Log.Warning($"Loader {remote} available but this loader cannot stage it."); return; }
            Main.Log.Msg($"Loader {remote} available (running {LoaderVersion}), downloading...");
            phase = $"downloading loader {remote}";
            string newPath = me + ".new";
            File.WriteAllBytes(newPath, DownloadBytes(feedLoader));
            var name = AssemblyName.GetAssemblyName(newPath);
            if (name.Name != "BryansGoodies") { File.Delete(newPath); throw new Exception("the downloaded file is not the loader (" + name.Name + ")"); }
            if (!Same(name.Version.ToString(3), remote)) { File.Delete(newPath); throw new Exception($"downloaded loader is {name.Version.ToString(3)}, feed says {remote}"); }
            install(newPath);
            loaderStaged = remote;
            lock (sync) messages.Enqueue($"Bryan's Goodies loader {remote} ready, restart the game to use it");
        }

        static bool SameBytes(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        static string Download(string url) => System.Text.Encoding.UTF8.GetString(DownloadBytes(url));

        static byte[] DownloadBytes(string url)
        {
            // Local feed for testing without GitHub: a plain path (E:\...\dist\version.txt, \\server\share\...) or file:///...
            string local = LocalPath(url);
            if (local != null) return File.ReadAllBytes(local);

            // Changing query parameter to bypass the cache (raw.githubusercontent.com keeps a file for a few minutes).
            url += (url.Contains("?") ? "&" : "?") + "t=" + DateTime.UtcNow.Ticks;
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "BryansGoodies/" + Main.Version;
            req.Timeout = 20000;
            req.AllowAutoRedirect = true;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var s = resp.GetResponseStream())
            using (var ms = new MemoryStream())
            {
                s.CopyTo(ms);
                var bytes = ms.ToArray();
                if (bytes.Length > 15 && System.Text.Encoding.ASCII.GetString(bytes, 0, 15).TrimStart().StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase))
                    throw new Exception("the link returns a web page, not the file");
                return bytes;
            }
        }
    }
}
