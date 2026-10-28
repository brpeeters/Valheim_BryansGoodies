using System.Collections.Generic;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace BryansGoodies
{
    // Skyrim-style compass at the top of the screen: cardinal points + map markers.
    public static class Compass
    {
        static readonly AccessTools.FieldRef<Minimap, List<Minimap.PinData>> PinsRef =
            AccessTools.FieldRefAccess<Minimap, List<Minimap.PinData>>("m_pins");
        static readonly AccessTools.FieldRef<Minimap, bool[]> VisibleTypesRef =
            AccessTools.FieldRefAccess<Minimap, bool[]>("m_visibleIconTypes");

        static readonly string[] Cardinals = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };

        public static MelonPreferences_Entry<bool> Enabled;
        public static MelonPreferences_Entry<KeyCode> ToggleKey;
        public static MelonPreferences_Entry<float> Fov, WidthFrac, TopMargin, IconSize, MaxDistance;
        public static MelonPreferences_Entry<bool> HideChecked;
        public static MelonPreferences_Entry<string> HiddenPins; // hidden marker types, comma-separated

        // Filter: each map marker type can be hidden on the compass (F7 menu).
        public static readonly (string label, Minimap.PinType[] types)[] Filters =
        {
            ("Campfire", new[] { Minimap.PinType.Icon0 }),
            ("House", new[] { Minimap.PinType.Icon1 }),
            ("Hammer", new[] { Minimap.PinType.Icon2 }),
            ("Dot", new[] { Minimap.PinType.Icon3 }),
            ("Portal", new[] { Minimap.PinType.Icon4 }),
            ("Tombstone", new[] { Minimap.PinType.Death }),
            ("Bed", new[] { Minimap.PinType.Bed }),
            ("Players", new[] { Minimap.PinType.Player }),
            ("Shouts and pings", new[] { Minimap.PinType.Shout, Minimap.PinType.Ping }),
            ("Bosses and altars", new[] { Minimap.PinType.Boss }),
            ("Events", new[] { Minimap.PinType.RandomEvent }),
            ("Hildir", new[] { Minimap.PinType.Hildir1, Minimap.PinType.Hildir2, Minimap.PinType.Hildir3 }),
            ("Memorial", new[] { Minimap.PinType.Memorial }),
        };
        static HashSet<Minimap.PinType> hidden;

        static HashSet<Minimap.PinType> Hidden()
        {
            if (hidden != null) return hidden;
            hidden = new HashSet<Minimap.PinType>();
            foreach (var part in (HiddenPins.Value ?? "").Split(','))
                if (System.Enum.TryParse(part.Trim(), out Minimap.PinType t)) hidden.Add(t);
            return hidden;
        }

        public static bool IsShown(Minimap.PinType[] types)
        {
            foreach (var t in types) if (Hidden().Contains(t)) return false;
            return true;
        }

        public static void SetShown(Minimap.PinType[] types, bool shown)
        {
            var h = Hidden();
            foreach (var t in types) { if (shown) h.Remove(t); else h.Add(t); }
            HiddenPins.Value = string.Join(",", h);
            Main.Save();
        }

        static Texture2D barTex;
        static GUIStyle cardStyle, smallStyle, labelStyle;

        public static void Init(MelonPreferences_Category cat)
        {
            Enabled = Prefs.Entry(cat, "Compass_Enabled", true, "Show the compass");
            ToggleKey = Prefs.Entry(cat, "Compass_ToggleKey", KeyCode.F8, "Key to show/hide the compass");
            Fov = Prefs.Entry(cat, "Compass_FieldOfView", 180f, "Angle covered by the bar (degrees)");
            WidthFrac = Prefs.Entry(cat, "Compass_WidthFraction", 0.38f, "Bar width (fraction of the screen)");
            TopMargin = Prefs.Entry(cat, "Compass_TopMargin", 12f, "Top margin (pixels at 1080p)");
            IconSize = Prefs.Entry(cat, "Compass_IconSize", 26f, "Icon size (pixels at 1080p)");
            MaxDistance = Prefs.Entry(cat, "Compass_MaxDistance", 0f, "Hide markers farther than this (m), 0 = unlimited");
            HideChecked = Prefs.Entry(cat, "Compass_HideChecked", false, "Hide checked (crossed-out) markers on the compass");
            HiddenPins = Prefs.Entry(cat, "Compass_HiddenPins", "", "Marker types hidden on the compass (e.g. Death,Ping)");
        }

        public static void HandleKeys()
        {
            if (ZInput.GetKeyDown(ToggleKey.Value, false))
            {
                Enabled.Value = !Enabled.Value;
                Main.Save();
            }
        }

        static bool ShouldDraw()
        {
            if (!Enabled.Value || Player.m_localPlayer == null || Minimap.instance == null) return false;
            if (Minimap.instance.m_mode == Minimap.MapMode.Large) return false;
            if (Hud.IsUserHidden() || InventoryGui.IsVisible() || Menu.IsVisible() || Game.IsPaused()) return false;
            return true;
        }

        static void InitGui()
        {
            if (barTex != null) return;
            // Dark band that fades out at the edges.
            barTex = new Texture2D(256, 1, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            for (int x = 0; x < 256; x++)
            {
                float a = Mathf.Sin(x / 255f * Mathf.PI);
                barTex.SetPixel(x, 0, new Color(0f, 0f, 0f, 0.55f * a * a));
            }
            barTex.Apply();
            cardStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            smallStyle = new GUIStyle(cardStyle) { fontStyle = FontStyle.Normal };
            labelStyle = new GUIStyle(cardStyle) { fontStyle = FontStyle.Normal };
        }

        public static void OnGUI()
        {
            if (Event.current.type != EventType.Repaint || !ShouldDraw()) return;
            InitGui();

            var cam = GameCamera.instance != null ? GameCamera.instance.transform : Camera.main?.transform;
            if (cam == null) return;
            Vector3 fwd = cam.forward;
            float heading = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg; // 0 = north (+Z), 90 = east (+X)

            float s = Screen.height / 1080f;
            float w = Screen.width * WidthFrac.Value;
            float h = 34f * s;
            float cx = Screen.width / 2f;
            float top = TopMargin.Value * s;
            float half = Fov.Value / 2f;
            var white = Texture2D.whiteTexture;

            GUI.color = Color.white;
            GUI.DrawTexture(new Rect(cx - w / 2f, top, w, h), barTex);

            // Tick marks every 15 degrees, letters every 45.
            cardStyle.fontSize = Mathf.RoundToInt(20 * s);
            smallStyle.fontSize = Mathf.RoundToInt(14 * s);
            for (int deg = 0; deg < 360; deg += 15)
            {
                float d = Mathf.DeltaAngle(heading, deg);
                if (Mathf.Abs(d) > half) continue;
                float x = cx + d / half * (w / 2f);
                float alpha = EdgeFade(d, half);
                if (deg % 45 == 0)
                {
                    GUI.color = new Color(1f, 1f, 1f, alpha);
                    Shadowed(new Rect(x - 30 * s, top, 60 * s, h), Cardinals[deg / 45], deg % 90 == 0 ? cardStyle : smallStyle);
                }
                else
                {
                    GUI.color = new Color(1f, 1f, 1f, 0.6f * alpha);
                    GUI.DrawTexture(new Rect(x - 1 * s, top + h * 0.35f, 2 * s, h * 0.3f), white);
                }
            }

            // Map markers.
            var pins = PinsRef(Minimap.instance);
            var visible = VisibleTypesRef(Minimap.instance);
            Vector3 me = Player.m_localPlayer.transform.position;
            Minimap.PinData focus = null;
            float focusAngle = float.MaxValue, focusDist = 0f;
            float isz = IconSize.Value * s;

            if (pins != null)
            {
                foreach (var pin in pins)
                {
                    if (pin == null || pin.m_icon == null) continue;
                    if (pin.m_type == Minimap.PinType.EventArea) continue;
                    if (pin.m_type == Minimap.PinType.Player && pin.m_pos == Vector3.zero) continue;
                    if (visible != null && (int)pin.m_type < visible.Length && !visible[(int)pin.m_type]) continue;
                    if (Hidden().Contains(pin.m_type)) continue;
                    if (HideChecked.Value && pin.m_checked) continue;

                    Vector3 delta = pin.m_pos - me;
                    delta.y = 0f;
                    float dist = delta.magnitude;
                    if (MaxDistance.Value > 0f && dist > MaxDistance.Value) continue;
                    if (dist < 1f) continue;

                    float d = Mathf.DeltaAngle(heading, Mathf.Atan2(delta.x, delta.z) * Mathf.Rad2Deg);
                    if (Mathf.Abs(d) > half) continue;

                    float x = cx + d / half * (w / 2f);
                    float size = (pin.m_doubleSize ? isz * 1.3f : isz) * Mathf.Lerp(1.25f, 0.9f, Mathf.Clamp01(dist / 500f));
                    var icon = pin.m_icon;
                    // Colored player: icon with its hue replaced (not a color multiply).
                    if (pin.m_type == Minimap.PinType.Player && PlayerColors.TryGetMapColor(pin.m_name, out var pc))
                        icon = Recolor.Sprite(pin.m_icon, pc);
                    GUI.color = new Color(1f, 1f, 1f, EdgeFade(d, half) * (pin.m_checked ? 0.4f : 1f));
                    DrawSprite(new Rect(x - size / 2f, top + (h - size) / 2f, size, size), icon);

                    if (Mathf.Abs(d) < focusAngle) { focusAngle = Mathf.Abs(d); focus = pin; focusDist = dist; }
                }
            }

            // Center marker.
            GUI.color = new Color(1f, 0.85f, 0.5f, 0.9f);
            GUI.DrawTexture(new Rect(cx - 1 * s, top + h - 4 * s, 2 * s, 7 * s), white);

            // Name + distance of the marker being aimed at.
            if (focus != null && focusAngle < 6f)
            {
                labelStyle.fontSize = Mathf.RoundToInt(15 * s);
                string name = string.IsNullOrEmpty(focus.m_name) ? "" : Localization.instance.Localize(focus.m_name) + "  ";
                GUI.color = Color.white;
                if (focus.m_type == Minimap.PinType.Player && PlayerColors.TryGetMapColor(focus.m_name, out var pc)) GUI.color = pc;
                Shadowed(new Rect(cx - 200 * s, top + h + 2 * s, 400 * s, 22 * s), name + Mathf.RoundToInt(focusDist) + " m", labelStyle);
            }
            GUI.color = Color.white;
        }

        static float EdgeFade(float d, float half) => Mathf.Clamp01((1f - Mathf.Abs(d) / half) / 0.25f);

        static void DrawSprite(Rect r, Sprite sp)
        {
            var tex = sp.texture;
            if (tex == null) return;
            Rect tr = sp.textureRect;
            GUI.DrawTextureWithTexCoords(r, tex, new Rect(tr.x / tex.width, tr.y / tex.height, tr.width / tex.width, tr.height / tex.height));
        }

        static void Shadowed(Rect r, string text, GUIStyle st)
        {
            var c = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, c.a * 0.8f);
            GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), text, st);
            GUI.color = c;
            GUI.Label(r, text, st);
        }
    }
}
