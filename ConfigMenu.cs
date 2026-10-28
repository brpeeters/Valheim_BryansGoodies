using System;
using System.Collections.Generic;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace BryansGoodies
{
    // In-game configuration window (IMGUI). While open: mouse released, the character stops moving.
    public static class ConfigMenu
    {
        public static bool IsOpen;
        public static bool WaitingForKey => keyTarget != null;

        static MelonPreferences_Entry<KeyCode> keyTarget;
        static Rect window = new Rect(60, 60, 460, 640);
        static Vector2 scroll;
        static GUIStyle header, swatch;
        static float r, g, b;
        static bool rgbFromPref = true;

        public static void Toggle()
        {
            IsOpen = !IsOpen;
            keyTarget = null;
            rgbFromPref = true;
            if (!IsOpen) Main.Save();
        }

        // Hot reload: the menu stays open where it was.
        public static void Export(Dictionary<string, object> h) => h["menu"] = new object[] { IsOpen, window, scroll };
        public static void Import(Dictionary<string, object> h)
        {
            if (!h.TryGetValue("menu", out var o) || !(o is object[] m)) return;
            IsOpen = (bool)m[0]; window = (Rect)m[1]; scroll = (Vector2)m[2];
            keyTarget = null; rgbFromPref = true;
        }

        static float Scale => Mathf.Max(1f, Screen.height / 1080f);

        public static void OnGUI()
        {
            if (!IsOpen) return;

            // Capture a key for a shortcut.
            if (keyTarget != null && Event.current.type == EventType.KeyDown && Event.current.keyCode != KeyCode.None)
            {
                if (Event.current.keyCode != KeyCode.Escape) keyTarget.Value = Event.current.keyCode;
                keyTarget = null;
                Event.current.Use();
                Main.Save();
            }

            if (header == null)
            {
                header = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = 15 };
                header.normal.textColor = new Color(1f, 0.8f, 0.45f);
                swatch = new GUIStyle(GUI.skin.box);
                swatch.normal.background = Texture2D.whiteTexture;
            }

            var old = GUI.matrix;
            float s = Scale;
            GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1f));
            GUI.color = Color.white;
            window = GUILayout.Window(0x4247, window, DrawWindow, "Bryan's Goodies  v" + Main.Version);
            GUI.matrix = old;
        }

        static void DrawWindow(int id)
        {
            scroll = GUILayout.BeginScrollView(scroll);

            Section("General");
            KeyField("Menu key", Main.MenuKey);

            Section("Death");
            Toggle(KeepItems.Enabled, "Keep the whole inventory on death (no tombstone)");
            if (Gifts.ShowInMenu)
            {
                Section("Gifts");
                Toggle(Gifts.Enabled, "Surprise gift: a low-tier item every 5 to 15 min");
                GUILayout.Label("<size=11><i>Only for certain characters. The item is marked as crafted by the player.</i></size>");
            }

            Section("Chests");
            Toggle(QuickStack.Enabled, "Quick stack into nearby chests");
            KeyField("Quick stack key", QuickStack.Key);
            Slider("Quick stack radius", QuickStack.Radius, 2f, 30f, "0", " m");
            Toggle(QuickStack.KeepHotbar, "Never stack the hotbar (1st row)");
            KeyField("Favorite: hold + click", QuickStack.FavModifier);
            GUILayout.Label("<size=11><i>Favorites (golden star) are never stacked away, even with the chest button.</i></size>");
            Toggle(QuickStack.Debug, "Log quick stacking (log file)");
            Toggle(CraftFromContainers.Enabled, "Craft / build using the contents of nearby chests");
            Slider("Crafting radius", CraftFromContainers.Radius, 5f, 50f, "0", " m");
            GUILayout.Label("<size=11><i>A chest opened by another player is ignored. Ownership is only requested when opening the workbench, when clicking Craft and while building.</i></size>");

            Section("Console");
            Toggle(CallCommand.Enabled, "/call command (call C# methods)");
            Toggle(ClaudeBridge.Enabled, "/claude command (ask Claude for anything)");
            Toggle(ClaudeBridge.Host, "This machine relays /claude requests to Claude (host)");
            GUILayout.Label("<size=11>Policy (host, edit in MelonPreferences.cfg, Claude_Policy): " + ClaudeBridge.Policy.Value + "</size>");
            GUILayout.Label("<size=11><i>Requests go to the host's UserData\\BryansGoodies\\claude\\inbox.jsonl; answers come back from the outbox folder.</i></size>");

            Section("Compass");
            Toggle(Compass.Enabled, "Show the compass");
            KeyField("Show/hide", Compass.ToggleKey);
            Slider("Angle covered", Compass.Fov, 90f, 360f, "0", " deg");
            Slider("Width", Compass.WidthFrac, 0.15f, 0.9f, "0.00", "");
            Slider("Top margin", Compass.TopMargin, 0f, 200f, "0", " px");
            Slider("Icon size", Compass.IconSize, 12f, 60f, "0", " px");
            Slider("Max distance (0 = unlimited)", Compass.MaxDistance, 0f, 5000f, "0", " m");
            GUILayout.Label("Markers shown on the compass:");
            Toggle(Compass.HideChecked, "Hide checked (crossed-out) markers");
            for (int i = 0; i < Compass.Filters.Length; i += 2)
            {
                GUILayout.BeginHorizontal();
                for (int j = i; j < i + 2 && j < Compass.Filters.Length; j++)
                {
                    var f = Compass.Filters[j];
                    bool shown = Compass.IsShown(f.types);
                    bool v = GUILayout.Toggle(shown, " " + f.label, GUILayout.Width(200));
                    if (v != shown) Compass.SetShown(f.types, v);
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.Label("<size=11><i>Types hidden on the in-game map are hidden on the compass too.</i></size>");

            Section("Player color");
            Toggle(PlayerColors.Enabled, "Enable player colors");
            if (PlayerColors.Enabled.Value) DrawColors();

            GUILayout.EndScrollView();

            GUILayout.Space(6);
            if (GUILayout.Button("Close")) Toggle();
            GUI.DragWindow(new Rect(0, 0, 10000, 22));
        }

        static void DrawColors()
        {
            var current = PlayerColors.ParseColor(PlayerColors.MyColor.Value);
            if (rgbFromPref)
            {
                var c = current ?? Color.white;
                r = c.r; g = c.g; b = c.b;
                rgbFromPref = false;
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label("My color:", GUILayout.Width(90));
            var prev = GUI.backgroundColor;
            GUI.backgroundColor = current ?? Color.clear;
            GUILayout.Box("", swatch, GUILayout.Width(60), GUILayout.Height(20));
            GUI.backgroundColor = prev;
            GUILayout.Label(current.HasValue ? PlayerColors.MyColor.Value : "none");
            GUILayout.EndHorizontal();

            // Palette.
            int perRow = 6;
            for (int i = 0; i < PlayerColors.Palette.Length; i += perRow)
            {
                GUILayout.BeginHorizontal();
                for (int j = i; j < Math.Min(i + perRow, PlayerColors.Palette.Length); j++)
                {
                    var (name, col) = PlayerColors.Palette[j];
                    GUI.backgroundColor = col;
                    if (GUILayout.Button(new GUIContent("", name), swatch, GUILayout.Width(58), GUILayout.Height(26)))
                    {
                        PlayerColors.SetMyColor(col);
                        rgbFromPref = true;
                    }
                }
                GUI.backgroundColor = prev;
                GUILayout.EndHorizontal();
            }

            // Fine RGB.
            GUILayout.Space(4);
            float or = r, og = g, ob = b;
            r = ColorSlider("R", r);
            g = ColorSlider("G", g);
            b = ColorSlider("B", b);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Apply RGB")) PlayerColors.SetMyColor(new Color(r, g, b));
            if (GUILayout.Button("No color")) { PlayerColors.SetMyColor(null); rgbFromPref = true; }
            GUILayout.EndHorizontal();

            GUILayout.Space(4);
            Toggle(PlayerColors.OnNames, "Names above heads");
            Toggle(PlayerColors.OnChat, "Names in chat");
            Toggle(PlayerColors.OnMap, "Map, minimap and compass");
            Toggle(PlayerColors.OnArmor, "Tint worn equipment");
            if (PlayerColors.OnArmor.Value) Slider("Tint strength", PlayerColors.ArmorStrength, 0f, 1f, "0.00", "");
            GUILayout.Label("<size=11><i>Friends need the mod to see the colors. Nothing to install on the server.</i></size>");
        }

        // ---------- Widgets ----------

        static void Section(string title)
        {
            GUILayout.Space(8);
            GUILayout.Label(title, header);
        }

        static void Toggle(MelonPreferences_Entry<bool> e, string label)
        {
            bool v = GUILayout.Toggle(e.Value, " " + label);
            if (v != e.Value) { e.Value = v; Main.Save(); }
        }

        static void Slider(string label, MelonPreferences_Entry<float> e, float min, float max, string fmt, string unit)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(190));
            float v = GUILayout.HorizontalSlider(e.Value, min, max, GUILayout.Width(150));
            GUILayout.Label(v.ToString(fmt) + unit, GUILayout.Width(70));
            GUILayout.EndHorizontal();
            if (!Mathf.Approximately(v, e.Value)) e.Value = v;
        }

        static float ColorSlider(string label, float v)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(20));
            v = GUILayout.HorizontalSlider(v, 0f, 1f, GUILayout.Width(300));
            GUILayout.Label(Mathf.RoundToInt(v * 255).ToString(), GUILayout.Width(40));
            GUILayout.EndHorizontal();
            return v;
        }

        static void KeyField(string label, MelonPreferences_Entry<KeyCode> e)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(190));
            string txt = keyTarget == e ? "Press a key..." : e.Value.ToString();
            if (GUILayout.Button(txt, GUILayout.Width(200))) keyTarget = e;
            GUILayout.EndHorizontal();
        }

        // ---------- Free mouse + character frozen while the menu is open ----------

        [HarmonyPatch(typeof(GameCamera), nameof(GameCamera.UpdateMouseCapture))]
        static class MouseCapture_Patch
        {
            static void Postfix()
            {
                if (!IsOpen) return;
                ZCursor.LockState = CursorLockMode.None;
                ZCursor.Show();
            }
        }

        [HarmonyPatch(typeof(PlayerController), "TakeInput")]
        static class PlayerControllerInput_Patch
        {
            static void Postfix(ref bool __result) { if (IsOpen) __result = false; }
        }

        [HarmonyPatch(typeof(Player), "TakeInput")]
        static class PlayerInput_Patch
        {
            static void Postfix(ref bool __result) { if (IsOpen) __result = false; }
        }
    }
}
