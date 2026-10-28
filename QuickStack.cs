using System;
using System.Collections.Generic;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using UnityEngine.UI;

namespace BryansGoodies
{
    // Quick stack: one key stores into nearby chests the items they already contain.
    // Favorites (Alt + click in the inventory, golden star): never stored, neither by the quick stack
    // nor by the chest's "Stack all" button.
    public static class QuickStack
    {
        const string FavKey = "bg_fav";

        public static MelonPreferences_Entry<bool> Enabled, KeepHotbar, Debug;
        public static MelonPreferences_Entry<KeyCode> Key, FavModifier;
        public static MelonPreferences_Entry<float> Radius;

        static int movedTotal, containersUsed, containersBusy;
        static bool running, sending;
        public static bool Running => running;

        // Hot reload: an operation in progress (answers still expected) continues in the next version.
        public static void Export(Dictionary<string, object> h)
        {
            if (running) h["quickstack.state"] = new object[] { movedTotal, containersUsed, containersBusy, startTime };
        }

        public static void Import(Dictionary<string, object> h)
        {
            if (!h.TryGetValue("quickstack.state", out var o) || !(o is object[] s)) return;
            movedTotal = (int)s[0]; containersUsed = (int)s[1]; containersBusy = (int)s[2]; startTime = (float)s[3];
            running = true; sending = false;
            var p = Player.m_localPlayer;
            if (p != null && Containers.PendingCount(Containers.Purpose.QuickStack) == 0) Finish(p);
        }
        static float startTime;

        public static void Init(MelonPreferences_Category cat)
        {
            Enabled = Prefs.Entry(cat, "QuickStack_Enabled", true, "Quick stack into nearby chests");
            Key = Prefs.Entry(cat, "QuickStack_Key", KeyCode.K, "Quick stack key");
            Radius = Prefs.Entry(cat, "QuickStack_Radius", 10f, "Radius (m)");
            KeepHotbar = Prefs.Entry(cat, "QuickStack_KeepHotbar", true, "Never store the hotbar (first row)");
            FavModifier = Prefs.Entry(cat, "QuickStack_FavoriteModifier", KeyCode.LeftAlt, "Key to hold + click to mark a favorite");
            Debug = Prefs.Entry(cat, "QuickStack_Debug", true, "Log quick stacking to the MelonLoader / BepInEx log");
            Containers.QuickStackResponse += OnResponse;
        }

        // Diagnostic log (file BepInEx\LogOutput.log or MelonLoader\Latest.log).
        public static void Log(string msg) { if (Debug.Value) Main.Log.Msg("[QuickStack] " + msg); }
        public static void Warn(string msg) => Main.Log.Warning("[QuickStack] " + msg);

        static string Describe(Container c)
        {
            var p = Player.m_localPlayer;
            float d = p != null ? Vector3.Distance(p.transform.position, c.transform.position) : -1f;
            return $"{c.m_name} at {d:0.0} m ({c.GetInventory().NrOfItems()} item(s), owner: {Containers.OwnerName(c)})";
        }

        public static bool IsFavorite(ItemDrop.ItemData item) =>
            item != null && item.m_customData != null && item.m_customData.ContainsKey(FavKey);

        static void ToggleFavorite(ItemDrop.ItemData item)
        {
            if (IsFavorite(item)) item.m_customData.Remove(FavKey);
            else item.m_customData[FavKey] = "1";
        }

        // Player items that must never go into a chest.
        static bool Protected(ItemDrop.ItemData item, Inventory from)
        {
            if (IsFavorite(item)) return true;
            var p = Player.m_localPlayer;
            if (p != null && from == p.GetInventory())
            {
                if (p.IsItemEquiped(item)) return true;
                if (KeepHotbar.Value && item.m_gridPos.y == 0) return true;
            }
            return false;
        }

        public static void HandleKeys()
        {
            // Safety: an operation cannot block the key for more than 5 s.
            if (running && Time.time - startTime > 5f)
            {
                Warn($"operation stuck for 5 s, giving up ({Containers.PendingCount(Containers.Purpose.QuickStack)} response(s) never received)");
                running = false;
            }
            if (!ZInput.GetKeyDown(Key.Value, false)) return;
            Log($"key {Key.Value} pressed");
            if (!Enabled.Value) { Log("ignored: feature disabled"); return; }
            if (running)
            {
                Log($"ignored: operation running for {Time.time - startTime:0.0} s, {Containers.PendingCount(Containers.Purpose.QuickStack)} response(s) pending");
                return;
            }
            var p = Player.m_localPlayer;
            if (p == null || p.IsDead()) { Log("ignored: no local player or player is dead"); return; }

            movedTotal = containersUsed = containersBusy = 0;
            running = true;
            sending = true;
            startTime = Time.time;
            var inv = p.GetInventory();
            try
            {
                var nearby = Containers.Nearby(p.transform.position, Radius.Value);
                Log($"{nearby.Count} accessible chest(s) within {Radius.Value:0} m, inventory: {inv.NrOfItems()} item(s)");
                foreach (var c in nearby)
                {
                    string desc = Describe(c);
                    if (Containers.InUseByOther(c)) { Log($"  {desc}: opened by another player, skipped"); containersBusy++; continue; }
                    if (!HasCommonItem(c.GetInventory(), inv)) { Log($"  {desc}: nothing in common with the inventory"); continue; } // local contents, may lag a little: simple pre-filter
                    // If the chest is already ours, the response arrives immediately (during this call).
                    Log($"  {desc}: requesting access");
                    try { Containers.Request(c, Containers.Purpose.QuickStack); }
                    catch (Exception e) { Warn($"  {c.m_name}: error during the request or the stacking\n{e}"); }
                }
            }
            finally { sending = false; }
            int pendingLeft = Containers.PendingCount(Containers.Purpose.QuickStack);
            if (pendingLeft == 0) Finish(p);
            else Log($"{pendingLeft} response(s) pending from the network");
        }

        static void Finish(Player p)
        {
            running = false;
            string msg = movedTotal > 0
                ? $"{movedTotal} item(s) stored in {containersUsed} chest(s)"
                : "Nothing to store";
            if (containersBusy > 0) msg += $"\n{containersBusy} chest(s) in use";
            Log($"summary after {Time.time - startTime:0.00} s: {msg.Replace("\n", " ; ")}");
            p.Message(MessageHud.MessageType.Center, msg);
        }

        static bool HasCommonItem(Inventory container, Inventory from)
        {
            foreach (var item in from.GetAllItems())
                if (!Protected(item, from) && container.ContainsItemByName(item.m_shared.m_name)) return true;
            return false;
        }

        // Response from each chest's owner (or timeout).
        static void OnResponse(Container c, bool granted)
        {
            var p = Player.m_localPlayer;
            if (p == null) return;
            if (!running) { Log($"  {c.m_name}: response ({(granted ? "granted" : "denied")}) arrived outside an operation, ignored"); return; }
            if (!granted) { Log($"  {c.m_name}: access denied or no response"); containersBusy++; }
            else
            {
                try
                {
                    var nv = Containers.GetNView(c);
                    if (nv != null && nv.IsValid() && !nv.IsOwner()) Warn($"  {c.m_name}: access granted but we are still not the owner");
                    int n = c.GetInventory().StackAll(p.GetInventory(), message: false);
                    Log($"  {c.m_name}: access granted, {n} item(s) stored");
                    if (n > 0)
                    {
                        movedTotal += n;
                        containersUsed++;
                        InventoryGui.instance?.m_moveItemEffects.Create(c.transform.position, Quaternion.identity);
                    }
                }
                catch (Exception e) { Warn($"  {c.m_name}: error during stacking\n{e}"); }
            }
            // Last expected response (after all requests have been sent): summary.
            if (running && !sending && Containers.PendingCount(Containers.Purpose.QuickStack) == 0) Finish(p);
        }

        // ---------- Inventory.StackAll: same logic as vanilla, minus protected items ----------

        // Inventory.Changed() gained optional parameters in a game update (Oct. 2026):
        // we pass the default values, however many there are, to survive future versions.
        static readonly System.Reflection.MethodInfo ChangedMethod = AccessTools.Method(typeof(Inventory), "Changed");
        static readonly object[] ChangedArgs = DefaultArgs(ChangedMethod);
        static void InvChanged(Inventory inv) => ChangedMethod.Invoke(inv, ChangedArgs);

        static object[] DefaultArgs(System.Reflection.MethodInfo m)
        {
            var ps = m.GetParameters();
            var args = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++)
                args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue
                        : ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null;
            return args;
        }

        [HarmonyPatch(typeof(Inventory), nameof(Inventory.StackAll))]
        static class StackAll_Patch
        {
            static bool Prefix(Inventory __instance, Inventory fromInventory, bool message, ref int __result)
            {
                var p = Player.m_localPlayer;
                if (p == null) return true;
                int before = message ? __instance.CountItems(null) : 0;
                int added = 0;
                foreach (var item in new List<ItemDrop.ItemData>(fromInventory.GetAllItems()))
                {
                    if (Protected(item, fromInventory)) continue;
                    if (!__instance.ContainsItemByName(item.m_shared.m_name)) continue;
                    int stack = item.m_stack;
                    if (__instance.AddItem(item))
                    {
                        fromInventory.RemoveItem(item);
                        added += stack;
                    }
                }
                __result = message ? __instance.CountItems(null) - before : added;
                if (message)
                    p.Message(MessageHud.MessageType.Center, __result > 0 ? "$msg_stackall " + __result : "$msg_stackall_none");
                InvChanged(__instance);
                InvChanged(fromInventory);
                Game.instance.IncrementPlayerStat(PlayerStatType.PlaceStacks);
                return false;
            }
        }

        // ---------- Alt + click: favorite ----------

        static readonly AccessTools.FieldRef<InventoryGrid, Inventory> GridInventory = AccessTools.FieldRefAccess<InventoryGrid, Inventory>("m_inventory");
        static readonly System.Reflection.MethodInfo GetButtonPos = AccessTools.Method(typeof(InventoryGrid), "GetButtonPos");

        static bool IsPlayerGrid(InventoryGrid grid) =>
            Player.m_localPlayer != null && GridInventory(grid) == Player.m_localPlayer.GetInventory();

        [HarmonyPatch(typeof(InventoryGrid), "OnLeftDown")]
        static class OnLeftDown_Patch
        {
            static bool Prefix(InventoryGrid __instance, UIInputHandler clickHandler)
            {
                if (!Enabled.Value || !ZInput.GetKey(FavModifier.Value, false) || !IsPlayerGrid(__instance)) return true;
                var pos = (Vector2i)GetButtonPos.Invoke(__instance, new object[] { clickHandler.gameObject });
                var item = GridInventory(__instance).GetItemAt(pos.x, pos.y);
                if (item == null) return true;
                ToggleFavorite(item);
                return false;
            }
        }

        // ---------- Star on favorites ----------

        static readonly AccessTools.FieldRef<InventoryGrid, List<InventoryElement>> Elements =
            AccessTools.FieldRefAccess<InventoryGrid, List<InventoryElement>>("m_elements");
        static Sprite starSprite;

        [HarmonyPatch(typeof(InventoryGrid), "UpdateGui")]
        static class UpdateGui_Patch
        {
            static void Postfix(InventoryGrid __instance)
            {
                var inv = GridInventory(__instance);
                var elements = Elements(__instance);
                if (inv == null || elements == null) return;
                bool player = IsPlayerGrid(__instance);
                int w = inv.GetWidth();
                var favSlots = new HashSet<int>();
                if (player && Enabled.Value)
                    foreach (var item in inv.GetAllItems())
                        if (IsFavorite(item)) favSlots.Add(item.m_gridPos.y * w + item.m_gridPos.x);

                for (int i = 0; i < elements.Count; i++)
                {
                    var el = elements[i];
                    if (el == null) continue;
                    var star = el.transform.Find("bg_fav");
                    bool on = favSlots.Contains(i);
                    if (star == null)
                    {
                        if (!on) continue;
                        star = CreateStar(el.transform);
                    }
                    if (star.gameObject.activeSelf != on) star.gameObject.SetActive(on);
                }
            }
        }

        static Transform CreateStar(Transform parent)
        {
            var go = new GameObject("bg_fav", typeof(RectTransform), typeof(Image));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(1f, 1f);
            rt.anchoredPosition = new Vector2(-2f, -2f);
            rt.sizeDelta = new Vector2(18f, 18f);
            var img = go.GetComponent<Image>();
            img.sprite = StarSprite();
            img.color = new Color(1f, 0.82f, 0.25f);
            img.raycastTarget = false;
            return go.transform;
        }

        // Five-pointed star drawn once (no asset to load).
        static Sprite StarSprite()
        {
            if (starSprite != null) return starSprite;
            const int N = 64;
            var tex = new Texture2D(N, N, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var pts = new Vector2[10];
            for (int k = 0; k < 10; k++)
            {
                float a = Mathf.PI / 2f + k * Mathf.PI / 5f;
                float r = (k % 2 == 0 ? 0.48f : 0.2f) * N;
                pts[k] = new Vector2(N / 2f + Mathf.Cos(a) * r, N / 2f + Mathf.Sin(a) * r);
            }
            var px = new Color32[N * N];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    // 4x4 supersampling for smooth edges; dark outline for readability.
                    int inside = 0, near = 0;
                    for (int sy = 0; sy < 4; sy++)
                        for (int sx = 0; sx < 4; sx++)
                        {
                            var q = new Vector2(x + (sx + 0.5f) / 4f, y + (sy + 0.5f) / 4f);
                            if (InPoly(q, pts)) inside++;
                            else if (InPoly(q + new Vector2(2, 0), pts) || InPoly(q - new Vector2(2, 0), pts) ||
                                     InPoly(q + new Vector2(0, 2), pts) || InPoly(q - new Vector2(0, 2), pts)) near++;
                        }
                    float a = inside / 16f, o = near / 16f;
                    byte v = (byte)(255 * a);
                    px[y * N + x] = new Color32(v, v, v, (byte)(255 * Mathf.Clamp01(a + o * 0.8f)));
                }
            tex.SetPixels32(px);
            tex.Apply();
            starSprite = Sprite.Create(tex, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f));
            return starSprite;
        }

        static bool InPoly(Vector2 p, Vector2[] poly)
        {
            bool c = false;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
                if ((poly[i].y > p.y) != (poly[j].y > p.y) &&
                    p.x < (poly[j].x - poly[i].x) * (p.y - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x)
                    c = !c;
            return c;
        }
    }
}
