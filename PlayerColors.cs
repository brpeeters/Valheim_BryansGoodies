using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using TMPro;
using UnityEngine;

namespace BryansGoodies
{
    // Each player picks a color that others see on: name above the head, chat,
    // map / minimap / compass and worn equipment.
    //
    // Sync without a server mod:
    //  - network message "BG_PlayerColor" (name, hex color) sent to everybody every 10 s and on every
    //    change. The server (vanilla or modded, dedicated or hosted) relays it without needing the mod.
    //  - the color is also written into the character's network data (key "bg_color"), read for
    //    nearby players.
    // Players without the mod simply do not see the colors.
    public static class PlayerColors
    {
        public const string RpcName = "BG_PlayerColor";
        const string ZdoKey = "bg_color";

        public static MelonPreferences_Entry<bool> Enabled, OnNames, OnChat, OnMap, OnArmor;
        public static MelonPreferences_Entry<string> MyColor;
        public static MelonPreferences_Entry<float> ArmorStrength;

        public static readonly (string name, Color color)[] Palette =
        {
            ("red", new Color(0.90f, 0.20f, 0.20f)),
            ("orange", new Color(1.00f, 0.55f, 0.10f)),
            ("yellow", new Color(1.00f, 0.90f, 0.20f)),
            ("green", new Color(0.30f, 0.85f, 0.30f)),
            ("teal", new Color(0.20f, 0.85f, 0.75f)),
            ("cyan", new Color(0.30f, 0.80f, 1.00f)),
            ("blue", new Color(0.25f, 0.40f, 1.00f)),
            ("purple", new Color(0.60f, 0.30f, 0.95f)),
            ("pink", new Color(1.00f, 0.45f, 0.75f)),
            ("white", new Color(0.95f, 0.95f, 0.95f)),
            ("gray", new Color(0.55f, 0.55f, 0.55f)),
            ("black", new Color(0.12f, 0.12f, 0.12f)),
        };

        static readonly Dictionary<string, Color> colors = new Dictionary<string, Color>();
        static ZRoutedRpc registeredRpc;
        static readonly FieldInfo RpcFunctions = AccessTools.Field(typeof(ZRoutedRpc), "m_functions");
        static float nextBroadcast, nextScan;
        static string lastSentHex;

        public static void Init(MelonPreferences_Category cat)
        {
            Enabled = Prefs.Entry(cat, "Colors_Enabled", true, "Player colors");
            MyColor = Prefs.Entry(cat, "Colors_MyColor", "", "My color (#RRGGBB), empty = none");
            OnNames = Prefs.Entry(cat, "Colors_OnNames", true, "Color names above heads");
            OnChat = Prefs.Entry(cat, "Colors_OnChat", true, "Color names in chat");
            OnMap = Prefs.Entry(cat, "Colors_OnMap", true, "Color players on the map and compass");
            OnArmor = Prefs.Entry(cat, "Colors_OnArmor", true, "Tint worn equipment");
            ArmorStrength = Prefs.Entry(cat, "Colors_ArmorStrength", 0.6f, "Equipment tint strength (0-1)");
        }

        // ---------- Data ----------

        public static string LocalName => Game.instance?.GetPlayerProfile()?.GetName();

        public static bool TryGet(string playerName, out Color c)
        {
            c = Color.white;
            if (!Enabled.Value || string.IsNullOrEmpty(playerName)) return false;
            return colors.TryGetValue(playerName, out c);
        }

        public static bool TryGetMapColor(string playerName, out Color c)
        {
            c = Color.white;
            return OnMap.Value && TryGet(playerName, out c);
        }

        public static Color? ParseColor(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            s = s.Trim();
            foreach (var p in Palette)
                if (p.name.Equals(s, StringComparison.OrdinalIgnoreCase)) return p.color;
            if (!s.StartsWith("#")) s = "#" + s;
            return ColorUtility.TryParseHtmlString(s, out var c) ? c : (Color?)null;
        }

        public static string ToHex(Color c) => "#" + ColorUtility.ToHtmlStringRGB(c);

        public static void SetMyColor(Color? c)
        {
            MyColor.Value = c.HasValue ? ToHex(c.Value) : "";
            Main.Save();
            nextBroadcast = 0f; // broadcast right away
        }

        static void Store(string name, string hex)
        {
            if (string.IsNullOrEmpty(name)) return;
            var c = ParseColor(hex);
            if (c.HasValue) colors[name] = c.Value; else colors.Remove(name);
        }

        // ---------- Network ----------

        static void RegisterRpc(ZRoutedRpc rpc)
        {
            registeredRpc = rpc;
            // The game rejects a duplicate registration: after a hot reload, replace the old one.
            (RpcFunctions?.GetValue(rpc) as System.Collections.IDictionary)?.Remove(RpcName.GetStableHashCode());
            rpc.Register<string, string>(RpcName, (sender, name, hex) => Store(name, hex));
        }

        // Hot reload: the other players' colors are kept (otherwise they would come back within 10 s).
        public static void Export(Dictionary<string, object> h)
        {
            h["colors"] = new Dictionary<string, Color>(colors);
            h["colors.lastSentHex"] = lastSentHex;
        }

        public static void Import(Dictionary<string, object> h)
        {
            if (h.TryGetValue("colors", out var o) && o is Dictionary<string, Color> d)
                foreach (var kv in d) colors[kv.Key] = kv.Value;
            if (h.TryGetValue("colors.lastSentHex", out var s)) lastSentHex = s as string;
            if (ZRoutedRpc.instance != null) RegisterRpc(ZRoutedRpc.instance); // before Update() would clear the colors
        }

        public static void Update()
        {
            var rpc = ZRoutedRpc.instance;
            if (rpc != null && rpc != registeredRpc)
            {
                colors.Clear();
                lastSentHex = null;
                RegisterRpc(rpc);
            }

            string me = LocalName;
            var player = Player.m_localPlayer;
            if (player == null || string.IsNullOrEmpty(me)) return;

            string myHex = Enabled.Value ? (MyColor.Value ?? "") : "";
            Store(me, myHex);

            float now = Time.unscaledTime;
            if (now >= nextBroadcast || myHex != lastSentHex)
            {
                nextBroadcast = now + 10f;
                lastSentHex = myHex;
                var zdo = player.GetComponent<ZNetView>()?.GetZDO();
                if (zdo != null && zdo.GetString(ZdoKey) != myHex) zdo.Set(ZdoKey, myHex);
                if (rpc != null && ZNet.instance != null) rpc.InvokeRoutedRPC(ZRoutedRpc.Everybody, RpcName, me, myHex);
            }

            if (now >= nextScan)
            {
                nextScan = now + 1f;
                foreach (var p in Player.GetAllPlayers())
                {
                    if (p == null || p == player) continue;
                    var zdo = p.GetComponent<ZNetView>()?.GetZDO();
                    if (zdo == null) continue;
                    string hex = zdo.GetString(ZdoKey, null);
                    if (hex != null) Store(p.GetPlayerName(), hex);
                }
                Armor.Apply();
            }
        }

        // ---------- /color command ----------

        public static void RegisterCommand()
        {
            new Terminal.ConsoleCommand("color", "[color|#RRGGBB|none] : pick your player color", args =>
            {
                var ctx = args.Context;
                if (args.Length < 2)
                {
                    ctx.AddString("Your color: " + (string.IsNullOrEmpty(MyColor.Value) ? "none" : MyColor.Value));
                    ctx.AddString("Colors: " + string.Join(", ", Palette.Select(p => "<color=" + ToHex(p.color) + ">" + p.name + "</color>").ToArray()) + ", or #RRGGBB, or none");
                    return;
                }
                string a = args[1];
                if (a == "none" || a == "off") { SetMyColor(null); ctx.AddString("Color removed."); return; }
                var c = ParseColor(a);
                if (!c.HasValue) { ctx.AddString("Unknown color: " + a); return; }
                SetMyColor(c);
                ctx.AddString("Your color: <color=" + ToHex(c.Value) + ">" + ToHex(c.Value) + "</color>");
            });
        }

        // ---------- Name above the head ----------

        [HarmonyPatch(typeof(EnemyHud), "UpdateHuds")]
        static class EnemyHud_Patch
        {
            static readonly FieldInfo HudsField = AccessTools.Field(typeof(EnemyHud), "m_huds");
            static readonly Type HudDataType = AccessTools.Inner(typeof(EnemyHud), "HudData");
            static readonly FieldInfo CharField = AccessTools.Field(HudDataType, "m_character");
            static readonly FieldInfo NameField = AccessTools.Field(HudDataType, "m_name");
            static Color? defaultColor;

            static void Postfix(EnemyHud __instance)
            {
                if (!(HudsField.GetValue(__instance) is IDictionary huds)) return;
                foreach (var hud in huds.Values)
                {
                    if (!(CharField.GetValue(hud) is Player p) || p == null) continue;
                    if (!(NameField.GetValue(hud) is TMP_Text text) || text == null) continue;
                    if (defaultColor == null) defaultColor = text.color;
                    text.color = OnNames.Value && TryGet(p.GetPlayerName(), out var c) ? c : defaultColor.Value;
                }
            }
        }

        // ---------- Chat ----------

        [HarmonyPatch(typeof(Terminal), nameof(Terminal.AddString), new[] { typeof(string) })]
        static class Chat_Patch
        {
            const string Tag = "<color=orange>";

            static void Prefix(Terminal __instance, ref string text)
            {
                if (!(__instance is Chat) || !Enabled.Value || !OnChat.Value || text == null || !text.Contains(Tag)) return;
                foreach (var kv in colors)
                {
                    string tagged = Tag + kv.Key;
                    int i = text.IndexOf(tagged, StringComparison.Ordinal);
                    if (i < 0) continue;
                    int end = i + tagged.Length;
                    // The name must be complete ("Bob" must not match "Bobby").
                    if (end < text.Length && text[end] != '<' && text[end] != ' ') continue;
                    text = text.Substring(0, i) + "<color=" + ToHex(kv.Value) + ">" + text.Substring(i + Tag.Length);
                    break;
                }
            }
        }

        // ---------- Map / minimap ----------

        [HarmonyPatch(typeof(Minimap), "UpdatePins")]
        static class Minimap_Patch
        {
            static readonly AccessTools.FieldRef<Minimap, List<Minimap.PinData>> PlayerPins =
                AccessTools.FieldRefAccess<Minimap, List<Minimap.PinData>>("m_playerPins");

            static void Postfix(Minimap __instance)
            {
                var pins = PlayerPins(__instance);
                if (pins == null) return;
                foreach (var pin in pins)
                {
                    if (pin?.m_iconElement == null) continue;
                    if (!TryGetMapColor(pin.m_name, out var c))
                    {
                        if (pin.m_iconElement.sprite != pin.m_icon) pin.m_iconElement.sprite = pin.m_icon;
                        continue;
                    }
                    // Recolored icon (hue replaced), not a color multiply.
                    var sp = Recolor.Sprite(pin.m_icon, c);
                    if (pin.m_iconElement.sprite != sp) pin.m_iconElement.sprite = sp;
                    pin.m_iconElement.color = Color.white;
                    var t = pin.m_NamePinData?.PinNameText;
                    if (t != null) t.color = c; // plain white text: the color replaces it directly
                }
            }
        }

        // ---------- Equipment ----------

        // Equipment piece materials are replaced by copies whose main texture has been recolored
        // (hue replaced, brightness kept). The originals are restored when the color is removed.
        // Copies are shared per (material, color, strength) and released as soon as they are unused.
        static class Armor
        {
            static readonly AccessTools.FieldRef<VisEquipment, GameObject> Helmet = AccessTools.FieldRefAccess<VisEquipment, GameObject>("m_helmetItemInstance");
            static readonly AccessTools.FieldRef<VisEquipment, List<GameObject>>[] Lists =
            {
                AccessTools.FieldRefAccess<VisEquipment, List<GameObject>>("m_chestItemInstances"),
                AccessTools.FieldRefAccess<VisEquipment, List<GameObject>>("m_legItemInstances"),
                AccessTools.FieldRefAccess<VisEquipment, List<GameObject>>("m_shoulderItemInstances"),
            };

            static readonly Dictionary<Renderer, Material[]> originals = new Dictionary<Renderer, Material[]>();
            static readonly Dictionary<(int, int, int), (Material mat, (int, int, int) tex)> mats =
                new Dictionary<(int, int, int), (Material, (int, int, int))>();

            public static void Apply()
            {
                var usedMats = new HashSet<(int, int, int)>();
                var usedTex = new HashSet<(int, int, int)>();
                var visited = new HashSet<Renderer>();
                float strength = Mathf.Clamp01(ArmorStrength.Value);

                foreach (var p in Player.GetAllPlayers())
                {
                    if (p == null) continue;
                    var vis = p.GetComponent<VisEquipment>();
                    if (vis == null) continue;
                    Color c = Color.white;
                    bool on = OnArmor.Value && strength > 0f && TryGet(p.GetPlayerName(), out c);
                    if (!on) continue;

                    foreach (var go in Instances(vis))
                    {
                        if (go == null) continue;
                        foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                        {
                            if (!(r is MeshRenderer || r is SkinnedMeshRenderer)) continue;
                            visited.Add(r);
                            if (!originals.TryGetValue(r, out var orig)) originals[r] = orig = r.sharedMaterials;
                            var arr = new Material[orig.Length];
                            for (int i = 0; i < orig.Length; i++)
                                arr[i] = Recolored(orig[i], c, strength, usedMats, usedTex) ?? orig[i];
                            if (!Same(r.sharedMaterials, arr)) r.sharedMaterials = arr;
                        }
                    }
                }

                // Restore the original materials where the color is no longer active.
                var drop = new List<Renderer>();
                foreach (var kv in originals)
                {
                    if (visited.Contains(kv.Key)) continue;
                    if (kv.Key != null) kv.Key.sharedMaterials = kv.Value;
                    drop.Add(kv.Key);
                }
                foreach (var r in drop) originals.Remove(r);

                // Release unused copies.
                var unused = new List<(int, int, int)>();
                foreach (var kv in mats)
                    if (!usedMats.Contains(kv.Key)) unused.Add(kv.Key);
                foreach (var k in unused)
                {
                    if (mats[k].mat != null) UnityEngine.Object.Destroy(mats[k].mat);
                    mats.Remove(k);
                }
                Recolor.ReleaseTexturesExcept(usedTex);
            }

            static Material Recolored(Material src, Color c, float strength, HashSet<(int, int, int)> usedMats, HashSet<(int, int, int)> usedTex)
            {
                if (src == null || !src.HasProperty("_MainTex") || src.mainTexture == null) return null;
                var key = (src.GetInstanceID(), Recolor.Key(c), Mathf.RoundToInt(strength * 20f));
                if (!mats.TryGetValue(key, out var entry) || entry.mat == null)
                {
                    var tex = Recolor.Texture(src.mainTexture, c, strength, out var texKey);
                    if (tex == null) return null;
                    var m = new Material(src) { name = src.name + " (BG color)" };
                    m.mainTexture = tex;
                    entry = (m, texKey);
                    mats[key] = entry;
                }
                usedMats.Add(key);
                usedTex.Add(entry.tex);
                return entry.mat;
            }

            static bool Same(Material[] a, Material[] b)
            {
                if (a.Length != b.Length) return false;
                for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
                return true;
            }

            static IEnumerable<GameObject> Instances(VisEquipment vis)
            {
                yield return Helmet(vis);
                foreach (var l in Lists)
                {
                    var list = l(vis);
                    if (list == null) continue;
                    foreach (var go in list) yield return go;
                }
            }
        }
    }
}
