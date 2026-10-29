using System;
using System.Collections.Generic;
using HarmonyLib;
using MelonLoader;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BryansGoodies
{
    // "Sort" button in the open chest window, next to "Take all" / "Stack all": merges partial stacks (same rule as
    // the game: name, quality, world level), then lays the items out from the top-left corner, grouped by type then
    // name. The chest is open, so we are its owner: the game saves it to the network as for any other change.
    // The button is a copy of "Stack all" without its gamepad shortcut, placed one step further along the same line.
    public static class ChestSort
    {
        public static MelonPreferences_Entry<bool> Enabled;

        const string ButtonName = UiField.Prefix + "ChestSort";

        static GameObject button;
        static bool broken; // the button could not be built: hidden until the next version

        static readonly AccessTools.FieldRef<InventoryGui, Container> CurrentContainer = AccessTools.FieldRefAccess<InventoryGui, Container>("m_currentContainer");
        static readonly AccessTools.FieldRef<InventoryGui, GameObject> DragGo = AccessTools.FieldRefAccess<InventoryGui, GameObject>("m_dragGo");
        static readonly System.Reflection.MethodInfo ChangedMethod = AccessTools.Method(typeof(Inventory), "Changed");

        public static void Init(MelonPreferences_Category cat)
        {
            Enabled = Prefs.Entry(cat, "ChestSort_Enabled", true, "Sort button in the chest window");
        }

        // Hot reload: the button's listener belongs to this version, the next one builds its own.
        public static void Export(Dictionary<string, object> h)
        {
            if (button != null) UnityEngine.Object.Destroy(button);
            button = null;
        }

        static void EnsureButton(InventoryGui gui)
        {
            if (button != null) return;
            var model = gui.m_stackAllButton;
            var parent = model.transform.parent;
            // Leftover of a version that did not clean up.
            var old = parent.Find(ButtonName);
            if (old != null) UnityEngine.Object.Destroy(old.gameObject);

            button = UnityEngine.Object.Instantiate(model.gameObject, parent);
            button.name = ButtonName;
            foreach (var pad in button.GetComponentsInChildren<UIGamePad>(true))
            {
                if (pad.m_hint != null) UnityEngine.Object.Destroy(pad.m_hint);
                UnityEngine.Object.Destroy(pad);
            }
            foreach (var loc in button.GetComponentsInChildren<Localize>(true)) UnityEngine.Object.Destroy(loc);
            var label = button.GetComponentInChildren<TMP_Text>(true);
            if (label != null) label.text = "Sort";

            var b = button.GetComponent<Button>();
            b.onClick = new Button.ButtonClickedEvent();
            b.onClick.AddListener(() => OnSort(gui));

            // One step further than "Stack all" is from "Take all" (same row or same column, whichever the layout uses).
            var rt = (RectTransform)button.transform;
            var stack = (RectTransform)model.transform;
            var take = (RectTransform)gui.m_takeAllButton.transform;
            Vector2 step = stack.anchoredPosition - take.anchoredPosition;
            if (take.parent != stack.parent || step.sqrMagnitude < 1f) step = new Vector2(0f, -stack.rect.height - 4f);
            rt.anchoredPosition = stack.anchoredPosition + step;
        }

        static void OnSort(InventoryGui gui)
        {
            var c = CurrentContainer(gui);
            if (c == null || !c.IsOwner() || DragGo(gui) != null || Player.m_localPlayer == null) return;
            try { Sort(c.GetInventory()); }
            catch (Exception e) { Main.Log.Warning("Chest sort: " + e); }
        }

        static void Sort(Inventory inv)
        {
            var items = inv.GetAllItems();
            var list = new List<ItemDrop.ItemData>(items);
            list.Sort(Compare);

            var merged = new List<ItemDrop.ItemData>();
            foreach (var item in list)
            {
                if (item.m_shared.m_maxStackSize > 1)
                    foreach (var m in merged)
                    {
                        if (item.m_stack <= 0) break;
                        if (!SameStack(m, item) || m.m_stack >= m.m_shared.m_maxStackSize) continue;
                        int move = Math.Min(item.m_stack, m.m_shared.m_maxStackSize - m.m_stack);
                        m.m_stack += move;
                        item.m_stack -= move;
                    }
                if (item.m_stack > 0) merged.Add(item);
            }

            int w = inv.GetWidth();
            for (int i = 0; i < merged.Count; i++) merged[i].m_gridPos = new Vector2i(i % w, i / w);
            items.Clear();
            items.AddRange(merged);
            ChangedMethod.Invoke(inv, new object[] { false, false }); // weight, then the chest saves itself
        }

        static bool SameStack(ItemDrop.ItemData a, ItemDrop.ItemData b) =>
            a.m_shared.m_name == b.m_shared.m_name && a.m_quality == b.m_quality && a.m_worldLevel == b.m_worldLevel;

        // By type, then displayed name, then best quality and biggest stack first.
        static int Compare(ItemDrop.ItemData a, ItemDrop.ItemData b)
        {
            int r = a.m_shared.m_itemType.CompareTo(b.m_shared.m_itemType);
            if (r == 0) r = string.Compare(Localization.instance.Localize(a.m_shared.m_name), Localization.instance.Localize(b.m_shared.m_name), StringComparison.CurrentCultureIgnoreCase);
            if (r == 0) r = b.m_quality.CompareTo(a.m_quality);
            if (r == 0) r = b.m_stack.CompareTo(a.m_stack);
            return r;
        }

        // Shown while a chest we own is open (not a tombstone), with the option on.
        [HarmonyPatch(typeof(InventoryGui), "UpdateContainer")]
        static class UpdateContainer_Patch
        {
            static void Postfix(InventoryGui __instance)
            {
                var c = CurrentContainer(__instance);
                bool show = Enabled.Value && !broken && c != null && c.IsOwner() && c.GetComponent<TombStone>() == null;
                if (!show) { if (button != null) button.SetActive(false); return; }
                try { EnsureButton(__instance); }
                catch (Exception e) { Main.Log.Warning("Chest sort button: " + e); broken = true; return; }
                button.SetActive(true);
                if (button.GetComponent<Button>() is Button b) b.interactable = true;
            }
        }
    }
}
