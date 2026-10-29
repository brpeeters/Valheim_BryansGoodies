using System.Collections.Generic;
using HarmonyLib;
using MelonLoader;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace BryansGoodies
{
    // Craft several items in one go: a number field to the left of the "Craft" button.
    //
    // Relies on the game's own multi-craft (Shift + Craft = x5): while the field is above 1, the game sees a "touch
    // multi-craft" with m_multiCraftAmount = the field. Requirements shown x N, button greyed if something is missing,
    // inventory space checked for N, resources consumed and skill raised for N: all vanilla code.
    // m_multiCraftAmount / m_multiCraftDuration are only changed during UpdateRecipe and OnCraftPressed, then restored:
    // the game's values never stay modified (hot reload, disabling the option).
    // Duration: the vanilla multi-craft pace (6 s for 5 = 1.2 s per item), never shorter than a single craft.
    // Upgrades (an existing item selected, or the upgrader station) are not affected: the field is hidden.
    public static class CraftQuantity
    {
        public static MelonPreferences_Entry<bool> Enabled;

        const string FieldName = UiField.Prefix + "CraftQty";
        const int Max = 999;

        static bool broken;       // the field could not be built: option off until the next version
        static int wanted = 1;    // value of the field
        static int batch;         // amount of the craft in progress (0: none, or a vanilla craft)
        static TMP_InputField field;
        static RectTransform button;
        static Vector2 buttonOffsetMin;

        static readonly AccessTools.FieldRef<InventoryGui, bool> TouchMulti = AccessTools.FieldRefAccess<InventoryGui, bool>("m_touchMultiCrafting");
        static readonly AccessTools.FieldRef<InventoryGui, bool> MultiCrafting = AccessTools.FieldRefAccess<InventoryGui, bool>("m_multiCrafting");
        static readonly AccessTools.FieldRef<InventoryGui, float> CraftTimer = AccessTools.FieldRefAccess<InventoryGui, float>("m_craftTimer");
        static readonly System.Reflection.FieldInfo SelectedRecipe = AccessTools.Field(typeof(InventoryGui), "m_selectedRecipe");
        static System.Reflection.PropertyInfo pairRecipe, pairItem;

        public static void Init(MelonPreferences_Category cat)
        {
            Enabled = Prefs.Entry(cat, "CraftQty_Enabled", true, "Number field next to the Craft button");
        }

        // Hot reload: the field belongs to this version (its listener is in this assembly), it is destroyed
        // and rebuilt by the next one. The craft in progress keeps its amount.
        public static void Export(Dictionary<string, object> h)
        {
            h["craftqty"] = new object[] { wanted, batch };
            RemoveField();
        }

        public static void Import(Dictionary<string, object> h)
        {
            if (!h.TryGetValue("craftqty", out var o) || !(o is object[] a)) return;
            wanted = (int)a[0]; batch = (int)a[1];
        }

        // Crafting a new item (not an upgrade), with the option on.
        static bool Eligible(InventoryGui gui)
        {
            if (!Enabled.Value || broken || Player.m_localPlayer == null) return false;
            var station = Player.m_localPlayer.GetCurrentCraftingStation();
            if (station != null && station.m_upgrader) return false;
            object pair = SelectedRecipe.GetValue(gui);
            if (pairRecipe == null)
            {
                pairRecipe = AccessTools.Property(pair.GetType(), "Recipe");
                pairItem = AccessTools.Property(pair.GetType(), "ItemData");
            }
            return pairRecipe.GetValue(pair) as Recipe != null && pairItem.GetValue(pair) == null;
        }

        // ---------- The field ----------

        static void EnsureField(InventoryGui gui)
        {
            if (field != null) return;
            button = gui.m_craftButton.GetComponent<RectTransform>();
            var parent = button.parent;
            // Leftover of a version that did not clean up: destroy it, the button may still be shrunk.
            var old = parent.Find(FieldName);
            if (old != null) Object.Destroy(old.gameObject);
            buttonOffsetMin = button.offsetMin;

            var label = gui.m_craftButton.GetComponentInChildren<TMP_Text>(true);
            field = UiField.Create(FieldName, parent, label, TextAlignmentOptions.Center);
            var go = field.gameObject;
            field.contentType = TMP_InputField.ContentType.IntegerNumber;
            field.characterLimit = 3;
            field.text = wanted.ToString();
            field.onValueChanged.AddListener(s => wanted = int.TryParse(s, out int n) ? Mathf.Clamp(n, 1, Max) : 1);
            field.onEndEdit.AddListener(_ => field.SetTextWithoutNotify(wanted.ToString()));
            var tip = go.AddComponent<UITooltip>();
            tip.m_text = "Number of items to craft";
            PlaceField();
            go.SetActive(true);
        }

        static void RemoveField()
        {
            if (button != null) button.offsetMin = buttonOffsetMin;
            if (field != null) Object.Destroy(field.gameObject);
            field = null; button = null;
        }

        // Field on the left part of the button's place, the button keeps the rest (works whatever its anchors).
        static void PlaceField()
        {
            var rt = (RectTransform)field.transform;
            rt.anchorMin = button.anchorMin;
            rt.anchorMax = new Vector2(button.anchorMin.x, button.anchorMax.y);
            rt.pivot = new Vector2(0f, 0.5f);
            rt.offsetMin = buttonOffsetMin;
            rt.offsetMax = new Vector2(buttonOffsetMin.x + FieldWidth, button.offsetMax.y);
        }

        static float FieldWidth => button.rect.height * 1.6f;

        static void Layout(bool show)
        {
            if (field.gameObject.activeSelf != show) field.gameObject.SetActive(show);
            var target = show ? buttonOffsetMin + new Vector2(FieldWidth + 6f, 0f) : buttonOffsetMin;
            if (button.offsetMin != target) button.offsetMin = target;
        }

        // ---------- Amount handed to the vanilla multi-craft ----------

        struct Saved { public int amount; public float duration; public bool touch, set; }

        static Saved Apply(InventoryGui gui, int amount)
        {
            var s = new Saved { amount = gui.m_multiCraftAmount, duration = gui.m_multiCraftDuration, touch = TouchMulti(gui), set = true };
            float perItem = s.duration / Mathf.Max(1, s.amount);
            gui.m_multiCraftAmount = amount;
            gui.m_multiCraftDuration = Mathf.Max(gui.m_craftDuration, perItem * amount);
            return s;
        }

        static void Restore(InventoryGui gui, Saved s)
        {
            if (!s.set) return;
            gui.m_multiCraftAmount = s.amount;
            gui.m_multiCraftDuration = s.duration;
        }

        [HarmonyPatch(typeof(InventoryGui), "UpdateRecipe")]
        static class UpdateRecipe_Patch
        {
            static void Prefix(InventoryGui __instance, out Saved __state)
            {
                __state = default;
                bool crafting = CraftTimer(__instance) >= 0f;
                if (crafting)
                {
                    // Craft in progress: its amount, chosen at the click (duration and DoCrafting read it).
                    if (batch > 1 && MultiCrafting(__instance)) __state = Apply(__instance, batch);
                    return;
                }
                batch = 0;
                if (wanted > 1 && Eligible(__instance))
                {
                    __state = Apply(__instance, wanted);
                    TouchMulti(__instance) = true; // shown as a multi-craft: "Craft x N", requirements x N
                }
            }

            static void Finalizer(InventoryGui __instance, Saved __state)
            {
                Restore(__instance, __state);
                if (__state.set) TouchMulti(__instance) = __state.touch;
                try
                {
                    if (!Enabled.Value || broken) { if (field != null) RemoveField(); return; }
                    EnsureField(__instance);
                    Layout(Eligible(__instance) && CraftTimer(__instance) < 0f);
                }
                catch (System.Exception e) { Main.Log.Warning($"Craft quantity field disabled: {e.Message}"); broken = true; RemoveField(); }
            }
        }

        [HarmonyPatch(typeof(InventoryGui), "OnCraftPressed")]
        static class OnCraftPressed_Patch
        {
            static void Prefix(InventoryGui __instance, out Saved __state)
            {
                __state = default;
                if (wanted > 1 && Eligible(__instance))
                {
                    __state = Apply(__instance, wanted);
                    TouchMulti(__instance) = true; // the game resets it itself
                }
            }

            static void Finalizer(InventoryGui __instance, Saved __state)
            {
                batch = __state.set && MultiCrafting(__instance) ? wanted : 0;
                Restore(__instance, __state);
            }
        }
    }
}
