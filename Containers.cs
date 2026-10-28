using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace BryansGoodies
{
    // Access to nearby chests, following the game's multiplayer rules:
    //  - a chest can only be modified by the player who is its network "owner";
    //  - to become the owner, we go through the vanilla "RPC_RequestStack" request: the current owner refuses if
    //    the chest is opened by someone else or is private, otherwise it hands it over to us and then responds;
    //  - we only write into a chest after that response, and only if we are the owner.
    // We intercept the response (RPC_StackResponse) for our own requests, in order to do our own processing
    // (quick stack or a simple ownership claim) instead of the vanilla "stack all".
    public static class Containers
    {
        public enum Purpose { Claim, QuickStack }

        static readonly AccessTools.FieldRef<Container, ZNetView> NView = AccessTools.FieldRefAccess<Container, ZNetView>("m_nview");
        static readonly System.Reflection.MethodInfo CheckAccessMethod = AccessTools.Method(typeof(Container), "CheckAccess");
        static bool CheckAccessFn(Container c, long playerID) => (bool)CheckAccessMethod.Invoke(c, new object[] { playerID });

        static readonly Dictionary<Container, (Purpose purpose, float time)> pending = new Dictionary<Container, (Purpose, float)>();
        static readonly Dictionary<Container, float> lastClaim = new Dictionary<Container, float>();
        static List<Container> all = new List<Container>();
        static float nextRefresh;

        public static event Action<Container, bool> QuickStackResponse;

        public static ZNetView GetNView(Container c) => NView(c);

        // Readable name of a chest's network owner (for the logs).
        public static string OwnerName(Container c)
        {
            var nv = NView(c);
            if (nv == null || !nv.IsValid()) return "invalid nview";
            long id = nv.GetZDO().GetOwner();
            if (id == 0) return "nobody";
            if (id == ZDOMan.GetSessionID()) return "us";
            if (ZNet.instance != null)
                foreach (var peer in ZNet.instance.GetPeers())
                    if (peer != null && peer.m_uid == id) return (peer.m_server ? "server " : "player ") + peer.m_playerName + " (" + id + ")";
            return "unknown or disconnected peer (" + id + ")";
        }

        // List of all loaded chests, refreshed at most once per second.
        static List<Container> All()
        {
            if (Time.time >= nextRefresh)
            {
                nextRefresh = Time.time + 1f;
                all = new List<Container>(UnityEngine.Object.FindObjectsByType<Container>(FindObjectsSortMode.None));
            }
            return all;
        }

        // Chests in range the player is allowed to use (ward, private chest), excluding tombstones.
        public static List<Container> Nearby(Vector3 pos, float radius)
        {
            var res = new List<Container>();
            long me = Game.instance.GetPlayerProfile().GetPlayerID();
            float r2 = radius * radius;
            foreach (var c in All())
            {
                if (c == null || !c.isActiveAndEnabled) continue;
                var nv = NView(c);
                if (nv == null || !nv.IsValid()) continue;
                if ((c.transform.position - pos).sqrMagnitude > r2) continue;
                if (c.GetComponent<TombStone>() != null) continue;
                if (c.m_checkGuardStone && !PrivateArea.CheckAccess(c.transform.position, 0f, flash: false)) continue;
                if (!CheckAccessFn(c, me)) continue;
                res.Add(c);
            }
            return res;
        }

        // Opened by another player (according to the network data).
        public static bool InUseByOther(Container c)
        {
            var nv = NView(c);
            return !nv.IsOwner() && nv.GetZDO().GetInt(ZDOVars.s_inUse) == 1;
        }

        // Chests usable right away: we are their owner.
        public static List<Container> Owned(Vector3 pos, float radius)
        {
            var res = Nearby(pos, radius);
            res.RemoveAll(c => !NView(c).IsOwner());
            return res;
        }

        // Is the network owner there to respond? (us, or a connected peer)
        public static bool OwnerReachable(Container c)
        {
            var nv = NView(c);
            if (nv == null || !nv.IsValid()) return false;
            long id = nv.GetZDO().GetOwner();
            if (id == ZDOMan.GetSessionID()) return true;
            if (id == 0 || ZNet.instance == null) return false;
            foreach (var peer in ZNet.instance.GetPeers())
                if (peer != null && peer.m_uid == id) return true;
            return false;
        }

        public static bool Request(Container c, Purpose purpose)
        {
            if (pending.TryGetValue(c, out var cur))
            {
                // A simple ownership claim (crafting) already in progress: its response will serve the quick stack.
                if (cur.purpose == Purpose.Claim && purpose == Purpose.QuickStack) { pending[c] = (purpose, cur.time); Log($"  {c.m_name}: ownership claim already in progress, reused for the quick stack"); return true; }
                Log($"  {c.m_name}: request already pending ({cur.purpose}), nothing sent"); return false;
            }
            // Owner with nobody behind it (player who just disconnected, chest without an owner):
            // nobody will respond. The game itself then claims ownership directly; we do the same.
            if (!OwnerReachable(c))
            {
                var nv = NView(c);
                Log($"  {c.m_name}: owner unreachable ({OwnerName(c)}), ownership claimed directly");
                nv.ClaimOwnership();
                if (purpose == Purpose.QuickStack) QuickStackResponse?.Invoke(c, nv.IsOwner());
                return true;
            }
            pending[c] = (purpose, Time.time);
            c.StackAll(); // sends "RPC_RequestStack" to the current owner (immediate if it is us)
            return true;
        }

        static void Log(string msg) => QuickStack.Log("[Containers] " + msg);

        // Claims ownership (without moving anything) of nearby chests not yet ours, at most every 5 s per chest
        // (force: right away, even if a recent request has already been made).
        public static void ClaimNearby(Vector3 pos, float radius, bool force = false)
        {
            foreach (var c in Nearby(pos, radius))
            {
                if (NView(c).IsOwner() || InUseByOther(c)) continue;
                if (!force && lastClaim.TryGetValue(c, out float t) && Time.time - t < 5f) continue;
                lastClaim[c] = Time.time;
                Request(c, Purpose.Claim);
            }
        }

        public static bool PendingAny() => pending.Count > 0;

        // Hot reload: pending requests follow to the next version, which owns the patch that receives the answers.
        public static void Export(Dictionary<string, object> h)
        {
            var list = new List<object[]>();
            foreach (var kv in pending) if (kv.Key != null) list.Add(new object[] { kv.Key, (int)kv.Value.purpose, kv.Value.time });
            h["containers.pending"] = list;
            var claims = new List<object[]>();
            foreach (var kv in lastClaim) if (kv.Key != null) claims.Add(new object[] { kv.Key, kv.Value });
            h["containers.lastClaim"] = claims;
        }

        public static void Import(Dictionary<string, object> h)
        {
            if (h.TryGetValue("containers.pending", out var p) && p is List<object[]> list)
                foreach (var e in list) if (e[0] is Container c) pending[c] = ((Purpose)(int)e[1], (float)e[2]);
            if (h.TryGetValue("containers.lastClaim", out var l) && l is List<object[]> claims)
                foreach (var e in claims) if (e[0] is Container c) lastClaim[c] = (float)e[1];
        }

        public static int PendingCount(Purpose p)
        {
            int n = 0;
            foreach (var kv in pending) if (kv.Value.purpose == p) n++;
            return n;
        }

        // Response delay of a connected owner (a refusal or a grant arrives in under 0.5 s in practice).
        const float Timeout = 3f;

        public static void Update()
        {
            // Owner connected but silent (heavy latency, stall): we do not insist and do not force it,
            // they might be writing into the chest. Unreachable owners are handled
            // directly in Request().
            List<Container> expired = null;
            foreach (var kv in pending)
                if (kv.Key == null || Time.time - kv.Value.time > Timeout) (expired ??= new List<Container>()).Add(kv.Key);
            if (expired != null)
                foreach (var c in expired)
                {
                    var p = pending[c].purpose;
                    pending.Remove(c);
                    if (c == null) continue;
                    QuickStack.Warn($"[Containers] {c.m_name}: no response from {OwnerName(c)} after {Timeout:0.0} s ({p}), giving up");
                    if (p == Purpose.QuickStack) QuickStackResponse?.Invoke(c, false);
                }
            if (lastClaim.Count > 200) lastClaim.Clear();
        }

        [HarmonyPatch(typeof(Container), "RPC_StackResponse")]
        static class StackResponse_Patch
        {
            static bool Prefix(Container __instance, bool granted)
            {
                if (!pending.TryGetValue(__instance, out var req)) { Log($"  {__instance.m_name}: vanilla response ({granted}), not ours"); return true; } // vanilla request: normal behavior
                pending.Remove(__instance);
                // The owner handed the chest over to us; like the vanilla "Take all", we make sure we are
                // the owner locally before touching it (the network update may arrive after the response).
                var nv = NView(__instance);
                bool wasOwner = nv != null && nv.IsValid() && nv.IsOwner();
                if (granted && nv != null && nv.IsValid() && !nv.IsOwner()) nv.ClaimOwnership();
                Log($"  {__instance.m_name}: response {(granted ? "granted" : "denied")} after {Time.time - req.time:0.00} s ({req.purpose}, owner before: {(wasOwner ? "us" : "other")})");
                if (req.purpose == Purpose.QuickStack) QuickStackResponse?.Invoke(__instance, granted);
                return false;
            }
        }
    }
}
