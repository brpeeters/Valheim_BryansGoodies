using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Threading;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(BryansGoodies.Loader.Loader), "Bryan's Goodies Loader", BryansGoodies.Loader.Loader.Version, "Bryan")]
[assembly: MelonGame("IronGate", "Valheim")]

namespace BryansGoodies.Loader
{
    // Minimal, stable loader: BryansGoodies.dll in Mods, the only file friends install. It cannot hot-reload itself,
    // so it does as little as possible:
    //  - loads the core (UserData\BryansGoodies\BryansGoodies.Core.dll) from its bytes and runs it;
    //  - swaps cores in the same frame with a state handoff (StopWithState -> StartWithState), and falls back to
    //    BryansGoodies.Core.dll.previous if the new one fails to start or keeps throwing;
    //  - reloads the core when its file changes, and installs the DLL named in an "install" trigger file (safety hatch);
    //  - rescue only: when no core can be loaded (first launch, or everything failed), downloads the latest published
    //    core from GitHub. All the other update mechanics (feed, channels, announcements, loader self-update, triggers,
    //    notices) live in the core, hot-reloadable: see Updater.cs.
    // The core receives a dictionary of services at start ("host"): paths, "core.install", "loader.install".
    // A loaded .NET assembly never unloads: the old one stays in memory, inert, once its patches are removed.
    public partial class Loader : MelonMod
    {
        // Version: see LoaderVersion.g.cs, generated from <LoaderVersion> in the csproj.
        const string RescueFeed = "https://raw.githubusercontent.com/brpeeters/Valheim_BryansGoodies/{channel}/dist/";

        MelonPreferences_Category cat;
        MelonPreferences_Entry<bool> WatchLocalFile;
        MelonPreferences_Entry<string> Channel, BadVersion;

        // Current core.
        Assembly payload;
        Action update, onGui;
        Func<object> stop;      // StopWithState() returns the state for the next version, or Stop()
        string payloadVersion = "0.0.0";
        object handoff;         // state returned by the previous version, given to the next one
        readonly Dictionary<string, object> host = new Dictionary<string, object>();
        DateTime loadedWriteTime;
        int updateErrors;       // consecutive frames where the core's Update threw
        float nextErrorLog, nextWatch, nextHatch;

        // Requests from the core (executed in our own update, after the core's).
        string installRequest;
        // Rescue download.
        volatile bool rescuing;
        float nextRescue;
        byte[] rescued;

        string CoreDir => Path.Combine(MelonUtils.UserDataDirectory, "BryansGoodies");
        string CorePath => Path.Combine(CoreDir, "BryansGoodies.Core.dll");

        public override void OnInitializeMelon()
        {
            cat = MelonPreferences.CreateCategory("BryansGoodiesLoader", "Bryan's Goodies - updates");
            WatchLocalFile = cat.CreateEntry("WatchLocalFile", true, description: "Reload if the file UserData\\BryansGoodies\\BryansGoodies.Core.dll changes");
            Channel = cat.CreateEntry("Channel", "main", description: "Branch of the GitHub repository to follow (main, beta...)");
            BadVersion = cat.CreateEntry("BadVersion", "", description: "Core version that failed to start (skipped unless forced)");

            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            Directory.CreateDirectory(CoreDir);
            CleanupLoaderFiles();

            host["loader.version"] = Version;
            host["loader.path"] = LoaderPath();
            host["core.dir"] = CoreDir;
            host["core.path"] = CorePath;
            host["core.install"] = (Action<string>)(path => installRequest = path);
            host["loader.install"] = (Action<string>)StageLoader;
            host["lastEvent"] = null;

            if (File.Exists(CorePath))
            {
                if (!LoadFromFile() && !Rollback("the core failed to start at launch")) LoggerInstance.Error("No working core: rescue download.");
            }
            else LoggerInstance.Msg("No BryansGoodies.Core.dll yet: rescue download from GitHub.");
        }

        // ---------- Loading / stopping the core ----------

        bool LoadFromFile()
        {
            try
            {
                var bytes = File.ReadAllBytes(CorePath);
                loadedWriteTime = File.GetLastWriteTimeUtc(CorePath);
                Load(bytes);
                return true;
            }
            catch (Exception e) { LoggerInstance.Error("Could not load the core: " + e); return false; }
        }

        void Load(byte[] bytes)
        {
            Unload();
            var asm = Assembly.Load(bytes);
            var main = asm.GetType("BryansGoodies.Main") ?? throw new Exception("type BryansGoodies.Main not found");
            const BindingFlags PS = BindingFlags.Public | BindingFlags.Static;
            Action Method(string name) => (Action)Delegate.CreateDelegate(typeof(Action), main.GetMethod(name, PS) ?? throw new Exception(name + " not found"));
            var u = Method("Update"); var g = Method("OnGUI");
            // Cores since 1.3.3 hand their state over: StartWithState(object) / object StopWithState(). Older ones: Start() / Stop().
            var startWithState = main.GetMethod("StartWithState", PS);
            var startPlain = main.GetMethod("Start", PS) ?? throw new Exception("Start not found");
            var stopWithState = main.GetMethod("StopWithState", PS);
            var stopPlain = main.GetMethod("Stop", PS) ?? throw new Exception("Stop not found");
            Func<object> st = stopWithState != null ? (Func<object>)Delegate.CreateDelegate(typeof(Func<object>), stopWithState)
                                                    : () => { stopPlain.Invoke(null, null); return null; };
            string version = (string)main.GetField("Version", PS).GetRawConstantValue();

            // The state dictionary carries the loader services too.
            var state = handoff as Dictionary<string, object> ?? new Dictionary<string, object>();
            state["host"] = host;
            if (startWithState != null) startWithState.Invoke(null, new object[] { state }); // may throw: the handoff is kept for a rollback
            else startPlain.Invoke(null, null);
            handoff = null;
            payload = asm; stop = st; update = u; onGui = g; payloadVersion = version;
            updateErrors = 0;
            LoggerInstance.Msg($"Core {payloadVersion} active.");
        }

        void Unload()
        {
            if (payload == null) return;
            try { handoff = stop?.Invoke(); } catch (Exception e) { LoggerInstance.Warning("Stopping the old version: " + e); handoff = null; }
            payload = null; stop = null; update = onGui = null;
        }

        // Restores BryansGoodies.Core.dll.previous and remembers the failed version so the updater does not fetch it again.
        bool Rollback(string why)
        {
            string bak = CorePath + ".previous", bad = CorePath + ".bad";
            string failed = payloadVersion;
            try { failed = AssemblyName.GetAssemblyName(CorePath).Version.ToString(3); } catch { }
            LoggerInstance.Error($"Core {failed}: {why}.");
            if (!File.Exists(bak)) return false;
            try
            {
                if (File.Exists(bad)) File.Delete(bad);
                File.Move(CorePath, bad);
                File.Move(bak, CorePath);
            }
            catch (Exception e) { LoggerInstance.Error("Rollback failed: " + e.Message); return false; }
            BadVersion.Value = failed;
            MelonPreferences.Save();
            payload = null; update = onGui = null; stop = null; // the failed core is dropped without calling its Stop
            host["lastEvent"] = $"Bryan's Goodies {failed} failed to start, back on the previous version";
            if (!LoadFromFile()) return false;
            LoggerInstance.Warning($"Previous core {payloadVersion} restored; {failed} will be skipped until a forced update.");
            return true;
        }

        // ---------- Services for the core ----------

        // Installs a downloaded core (validated by the updater) and swaps to it. Called from our own update loop.
        void Install(string newPath)
        {
            string bak = CorePath + ".previous";
            try
            {
                if (File.Exists(bak)) File.Delete(bak);
                if (File.Exists(CorePath)) File.Move(CorePath, bak);
                File.Move(newPath, CorePath);
            }
            catch (Exception e) { LoggerInstance.Warning("Installing the update: " + e.Message); return; }
            if (!LoadFromFile()) Rollback("the update failed to start");
        }

        // Puts a new loader file in place for the next launch: the running DLL is renamed, the new one takes its name.
        void StageLoader(string newPath)
        {
            string me = LoaderPath() ?? throw new Exception("loader file not located");
            string old = me + ".old";
            if (File.Exists(old)) File.Delete(old);
            File.Move(me, old);
            File.Move(newPath, me);
            LoggerInstance.Msg($"Loader staged for the next launch: {me}");
        }

        string LoaderPath()
        {
            string loc = null;
            try { loc = typeof(Loader).Assembly.Location; } catch { }
            if (!string.IsNullOrEmpty(loc) && File.Exists(loc) && Path.GetFileName(loc).Equals("BryansGoodies.dll", StringComparison.OrdinalIgnoreCase)) return loc;
            string guess = Path.Combine(MelonUtils.BaseDirectory, "Mods", "BryansGoodies.dll");
            return File.Exists(guess) ? guess : null;
        }

        void CleanupLoaderFiles()
        {
            string me = LoaderPath();
            if (me == null) return;
            foreach (var f in new[] { me + ".old", me + ".new" })
                try { if (File.Exists(f)) File.Delete(f); } catch { }
        }

        // ---------- Loop ----------

        public override void OnUpdate()
        {
            float now = Time.unscaledTime;
            try { update?.Invoke(); updateErrors = 0; }
            catch (Exception e)
            {
                updateErrors++;
                if (now >= nextErrorLog) { nextErrorLog = now + 5f; LoggerInstance.Error($"Core ({updateErrors} consecutive errors): " + e); }
                if (updateErrors >= 300 && payload != null) // about 5 s of failing frames: the core is broken
                {
                    Unload();
                    if (!Rollback("keeps throwing in Update")) LoggerInstance.Error("No previous core to restore.");
                }
            }

            if (installRequest != null) { string p = installRequest; installRequest = null; Install(p); }

            // Safety hatch independent of the core: a file UserData\BryansGoodies\install whose content is the path of a
            // core DLL (for example the dist\ build) installs that file and swaps to it, even if the running core is stuck.
            if (now >= nextHatch)
            {
                nextHatch = now + 2f;
                string hatch = Path.Combine(CoreDir, "install");
                if (File.Exists(hatch))
                {
                    string src = null;
                    try { src = File.ReadAllText(hatch).Trim(); File.Delete(hatch); } catch { }
                    if (!string.IsNullOrEmpty(src) && File.Exists(src))
                    {
                        try { File.Copy(src, CorePath + ".new", true); LoggerInstance.Msg("Install file: installing " + src); Install(CorePath + ".new"); }
                        catch (Exception e) { LoggerInstance.Warning("Install file: " + e.Message); }
                    }
                    else LoggerInstance.Warning("Install file: path not found: " + src);
                }
            }

            if (payload == null) Rescue(now);

            if (WatchLocalFile.Value && now >= nextWatch)
            {
                nextWatch = now + 2f;
                try
                {
                    if (payload != null && File.Exists(CorePath) && File.GetLastWriteTimeUtc(CorePath) != loadedWriteTime)
                    {
                        LoggerInstance.Msg("Local file changed: reloading.");
                        host["lastEvent"] = "Bryan's Goodies reloaded from the local build";
                        if (!LoadFromFile()) Rollback("the new local build failed to start");
                    }
                }
                catch (Exception e) { LoggerInstance.Warning("File watch: " + e.Message); }
            }
        }

        public override void OnGUI()
        {
            try { onGui?.Invoke(); }
            catch (Exception e) { if (Time.unscaledTime >= nextErrorLog) { nextErrorLog = Time.unscaledTime + 5f; LoggerInstance.Error("Core (GUI): " + e); } }
        }

        // ---------- Rescue: no core at all, fetch the latest published one ----------

        void Rescue(float now)
        {
            if (rescued != null)
            {
                var bytes = rescued; rescued = null;
                try
                {
                    File.WriteAllBytes(CorePath, bytes);
                    host["lastEvent"] = "Bryan's Goodies installed";
                    if (!LoadFromFile()) LoggerInstance.Error("Rescued core failed to start.");
                }
                catch (Exception e) { LoggerInstance.Error("Rescue install: " + e.Message); }
                return;
            }
            if (rescuing || now < nextRescue) return;
            nextRescue = now + 120f;
            rescuing = true;
            string url = RescueFeed.Replace("{channel}", Channel.Value.Trim()) + "BryansGoodies.Core.dll?t=" + DateTime.UtcNow.Ticks;
            LoggerInstance.Msg("Rescue: downloading the latest core...");
            new Thread(() =>
            {
                try
                {
                    var req = (HttpWebRequest)WebRequest.Create(url);
                    req.UserAgent = "BryansGoodies.Loader/" + Version;
                    req.Timeout = 20000;
                    using (var resp = (HttpWebResponse)req.GetResponse())
                    using (var s = resp.GetResponseStream())
                    using (var ms = new MemoryStream())
                    {
                        s.CopyTo(ms);
                        var bytes = ms.ToArray();
                        if (bytes.Length < 1024 || bytes[0] != (byte)'M' || bytes[1] != (byte)'Z') throw new Exception("not a DLL");
                        rescued = bytes;
                    }
                    LoggerInstance.Msg("Rescue: core downloaded.");
                }
                catch (Exception e) { LoggerInstance.Warning("Rescue failed: " + e.Message + " (retry in 2 min)"); }
                finally { rescuing = false; }
            }) { IsBackground = true, Name = "BryansGoodies.Rescue" }.Start();
        }
    }
}
