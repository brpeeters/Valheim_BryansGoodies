using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HarmonyLib;
using MelonLoader;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BryansGoodies
{
    // Search field above the recipe list (Craft and Upgrade tabs): only the recipes whose name (in the game's
    // language) contains the typed text are listed. Case and accents are ignored ("epee" finds "Épée").
    //
    // The filter is applied in UpdateRecipeList, on the list of recipes the game is about to display: selection,
    // sorting, scrolling and greying stay vanilla. Typing rebuilds the list (UpdateCraftingPanel).
    // The field takes the top of the list's place; the list and its scrollbar are shortened by as much.
    // The search is cleared when the inventory closes.
    public static class CraftSearch
    {
        public static MelonPreferences_Entry<bool> Enabled;

        const string FieldName = UiField.Prefix + "CraftSearch";
        const float Gap = 4f;

        static string query = "";
        static bool broken;
        static TMP_InputField field;
        static InventoryGui owner;
        static RectTransform list, bar;
        static Vector2 listOffsetMax, barOffsetMax;
        static float shift;

        static readonly AccessTools.FieldRef<InventoryGui, float> ListBaseSize = AccessTools.FieldRefAccess<InventoryGui, float>("m_recipeListBaseSize");
        static readonly System.Reflection.MethodInfo UpdateCraftingPanel = AccessTools.Method(typeof(InventoryGui), "UpdateCraftingPanel");

        public static void Init(MelonPreferences_Category cat)
        {
            Enabled = Prefs.Entry(cat, "CraftSearch_Enabled", true, "Search field above the recipe list");
        }

        // Hot reload: the field belongs to this version, the next one rebuilds it with the same text.
        public static void Export(Dictionary<string, object> h)
        {
            h["craftsearch"] = query;
            RemoveField();
        }

        public static void Import(Dictionary<string, object> h)
        {
            if (h.TryGetValue("craftsearch", out var o) && o is string q) query = q;
        }

        static bool Active => Enabled.Value && !broken && query.Length > 0;

        static string Fold(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s.Normalize(NormalizationForm.FormD))
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        static bool Matches(Recipe r, string q) =>
            r != null && r.m_item != null && Fold(Localization.instance.Localize(r.m_item.m_itemData.m_shared.m_name)).Contains(q);

        static void Refresh()
        {
            if (owner != null && InventoryGui.IsVisible()) UpdateCraftingPanel.Invoke(owner, new object[] { false });
        }

        // ---------- The field ----------

        static void EnsureField(InventoryGui gui)
        {
            if (field != null) return;
            owner = gui;
            var scroll = gui.m_recipeListRoot.GetComponentInParent<ScrollRect>();
            list = scroll != null ? (RectTransform)scroll.transform : (RectTransform)gui.m_recipeListRoot.parent;
            var parent = list.parent;
            // Leftover of a version that did not clean up.
            var old = parent.Find(FieldName);
            if (old != null) Object.Destroy(old.gameObject);
            listOffsetMax = list.offsetMax;
            // The scrollbar is shortened too when it sits next to the list (same parent), not when it is inside it.
            bar = gui.m_recipeListScroll != null && gui.m_recipeListScroll.transform.parent == parent ? (RectTransform)gui.m_recipeListScroll.transform : null;
            if (bar != null) barOffsetMax = bar.offsetMax;
            Main.Log.Msg($"Craft search: list '{list.name}' in '{parent.name}', scrollbar {(bar != null ? "beside" : "inside or elsewhere")}, list height {list.rect.height:0}");

            var label = gui.m_recipeElementPrefab.GetComponentInChildren<TMP_Text>(true);
            field = UiField.Create(FieldName, parent, label, TextAlignmentOptions.Left);
            field.characterLimit = 30;
            field.text = query;
            ((TMP_Text)field.placeholder).text = "Search...";
            field.onValueChanged.AddListener(s =>
            {
                query = Fold(s.Trim());
                Refresh();
            });
            var tip = field.gameObject.AddComponent<UITooltip>();
            tip.m_text = "Filter the recipes by name";

            // Top of the list's place, whatever its anchors.
            float h = gui.m_recipeListSpace;
            shift = h + Gap;
            var rt = (RectTransform)field.transform;
            rt.anchorMin = new Vector2(list.anchorMin.x, list.anchorMax.y);
            rt.anchorMax = list.anchorMax;
            rt.pivot = new Vector2(0.5f, 1f);
            rt.offsetMin = new Vector2(list.offsetMin.x, listOffsetMax.y - h);
            rt.offsetMax = listOffsetMax;
            list.offsetMax = listOffsetMax - new Vector2(0f, shift);
            if (bar != null) bar.offsetMax = barOffsetMax - new Vector2(0f, shift);
            ListBaseSize(gui) -= shift; // the list's minimum height, read by the game at startup
            field.gameObject.SetActive(true);
        }

        static void RemoveField()
        {
            if (field == null) return;
            if (list != null) list.offsetMax = listOffsetMax;
            if (bar != null) bar.offsetMax = barOffsetMax;
            if (owner != null) ListBaseSize(owner) += shift;
            Object.Destroy(field.gameObject);
            field = null; list = null; bar = null; shift = 0f;
        }

        // ---------- Patches ----------

        [HarmonyPatch(typeof(InventoryGui), "UpdateRecipeList")]
        static class UpdateRecipeList_Patch
        {
            // recipes: the game's temporary list, refilled before each call.
            static void Prefix(List<Recipe> recipes)
            {
                if (!Active) return;
                string q = query;
                recipes.RemoveAll(r => !Matches(r, q));
            }
        }

        [HarmonyPatch(typeof(InventoryGui), "UpdateRecipe")]
        static class UpdateRecipe_Patch
        {
            static void Postfix(InventoryGui __instance)
            {
                try
                {
                    if (!Enabled.Value || broken)
                    {
                        if (field != null) { RemoveField(); query = ""; Refresh(); }
                        return;
                    }
                    EnsureField(__instance);
                }
                catch (System.Exception e) { Main.Log.Warning($"Craft search field disabled: {e.Message}"); broken = true; RemoveField(); }
            }
        }

        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Hide))]
        static class Hide_Patch
        {
            static void Postfix()
            {
                if (field != null && field.text.Length > 0) field.SetTextWithoutNotify("");
                query = "";
            }
        }
    }
}
