using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace BryansGoodies
{
    // Text field built from code for the game's UI (no prefab of the game has a reusable one): dark background,
    // text styled like an existing label of the game. Returned inactive: place it, then activate it.
    // Its listeners belong to this assembly: the owner destroys it on hot reload.
    public static class UiField
    {
        public const string Prefix = "Wololo_";

        static int typingFrame = -1;
        static bool typing;

        // True while one of our fields has the keyboard (checked once per frame).
        public static bool Typing
        {
            get
            {
                if (typingFrame != Time.frameCount)
                {
                    typingFrame = Time.frameCount;
                    var sel = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
                    typing = sel != null && sel.name.StartsWith(Prefix) && sel.TryGetComponent(out TMP_InputField f) && f.isFocused;
                }
                return typing;
            }
        }

        // While typing, the game's bindings see no key (Tab or E would close the inventory, M open the map...).
        // The field itself reads the keyboard through Unity, not ZInput. Escape and Enter still go through.
        [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetButtonDown))]
        static class GetButtonDown_Patch { static bool Prefix(ref bool __result) => Pass(false, ref __result); }

        [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetButton))]
        static class GetButton_Patch { static bool Prefix(ref bool __result) => Pass(false, ref __result); }

        [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetKeyDown))]
        static class GetKeyDown_Patch { static bool Prefix(KeyCode key, ref bool __result) => Pass(Exempt(key), ref __result); }

        [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetKey))]
        static class GetKey_Patch { static bool Prefix(KeyCode key, ref bool __result) => Pass(Exempt(key), ref __result); }

        static bool Exempt(KeyCode key) => key == KeyCode.Escape || key == KeyCode.Return || key == KeyCode.KeypadEnter;

        // Harmony prefix: true runs the game's method, false answers "not pressed".
        static bool Pass(bool exempt, ref bool result)
        {
            if (!Typing || exempt) return true;
            result = false;
            return false;
        }

        public static TMP_InputField Create(string name, Transform parent, TMP_Text style, TextAlignmentOptions align, float fontScale = 1f)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.SetActive(false);
            var rt = go.GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            go.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.55f);

            var area = new GameObject("Text Area", typeof(RectTransform), typeof(RectMask2D)).GetComponent<RectTransform>();
            area.SetParent(rt, false);
            area.anchorMin = Vector2.zero; area.anchorMax = Vector2.one;
            area.offsetMin = new Vector2(6, 2); area.offsetMax = new Vector2(-6, -2);

            var text = NewText("Text", area, style, align, fontScale);
            var placeholder = NewText("Placeholder", area, style, align, fontScale);
            placeholder.fontStyle = FontStyles.Italic;
            placeholder.color = new Color(1f, 1f, 1f, 0.35f);

            var field = go.AddComponent<TMP_InputField>();
            field.textViewport = area;
            field.textComponent = text;
            field.placeholder = placeholder;
            return field;
        }

        static TextMeshProUGUI NewText(string name, RectTransform parent, TMP_Text style, TextAlignmentOptions align, float fontScale)
        {
            var text = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI)).GetComponent<TextMeshProUGUI>();
            text.rectTransform.SetParent(parent, false);
            text.rectTransform.anchorMin = Vector2.zero; text.rectTransform.anchorMax = Vector2.one;
            text.rectTransform.offsetMin = text.rectTransform.offsetMax = Vector2.zero;
            if (style != null) { text.font = style.font; text.fontSize = style.fontSize * fontScale; text.color = style.color; }
            text.alignment = align;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            return text;
        }
    }
}
