using System.Collections.Generic;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace BryansGoodies
{
    // Craft / build with the ingredients of nearby chests.
    //
    // Display ("have / required", recipes greyed out or not): the player's inventory count includes the contents,
    // as synchronized, of all nearby chests that are accessible and not opened by another player.
    // It does not depend on who owns the chest, so it stays stable when several players share the same workbench.
    //
    // Consumption: we never remove anything from a chest we are not the network owner of at that moment.
    // Ownership is requested (vanilla path, refused if another player has the chest open) only:
    //  - when opening the workbench and on the "Craft" / "Upgrade" click (the response arrives during the
    //    progress bar, before consumption);
    //  - when entering build mode and after each piece placed.
    // Right before consuming (end of crafting, placing a piece), the count switches to strict mode:
    // owned chests only, so we never craft with a chest we cannot remove from.
    public static class CraftFromContainers
    {
        public static MelonPreferences_Entry<bool> Enabled;
        public static MelonPreferences_Entry<float> Radius;

        [System.ThreadStatic] static int scope;
        static bool strict, wasPlacing;
        static List<Container> cached;
        static int cachedFrame = -1;
        static bool cachedStrict;

        public static void Init(MelonPreferences_Category cat)
        {
            Enabled = Prefs.Entry(cat, "Craft_Enabled", true, "Craft with the ingredients of nearby chests");
            Radius = Prefs.Entry(cat, "Craft_Radius", 20f, "Radius (m)");
        }

        public static void Update()
        {
            var p = Player.m_localPlayer;
            if (p == null) { wasPlacing = false; return; }
            bool placing = Enabled.Value && p.InPlaceMode();
            if (placing && !wasPlacing) Claim("build mode");
            wasPlacing = placing;
        }

        // Hot reload: avoid a spurious "build mode" claim right after the swap.
        public static void Export(Dictionary<string, object> h) => h["craft.wasPlacing"] = wasPlacing;
        public static void Import(Dictionary<string, object> h) { if (h.TryGetValue("craft.wasPlacing", out var o) && o is bool b) wasPlacing = b; }

        static void Claim(string why)
        {
            var p = Player.m_localPlayer;
            if (!Enabled.Value || p == null) return;
            QuickStack.Log($"[Crafting] requesting ownership of nearby chests ({why})");
            Containers.ClaimNearby(p.transform.position, Radius.Value, force: true);
        }

        static bool Active => scope > 0 && Enabled.Value && Player.m_localPlayer != null;

        static bool IsPlayerInv(Inventory inv) => Player.m_localPlayer != null && inv == Player.m_localPlayer.GetInventory();

        // Counted chests, recomputed once per frame: all accessible chests (display),
        // or only the ones we own (strict mode, right before consuming; always in build mode,
        // where placing consumes without delay).
        static List<Container> Sources()
        {
            bool s = strict || (Player.m_localPlayer != null && Player.m_localPlayer.InPlaceMode());
            if (cachedFrame != Time.frameCount || cached == null || cachedStrict != s)
            {
                cachedFrame = Time.frameCount;
                cachedStrict = s;
                cached = s ? Containers.Owned(Player.m_localPlayer.transform.position, Radius.Value)
                           : Containers.Nearby(Player.m_localPlayer.transform.position, Radius.Value);
                if (!s) cached.RemoveAll(c => c == null || Containers.InUseByOther(c));
            }
            if (s) cached.RemoveAll(c => c == null || !Containers.GetNView(c).IsOwner());
            return cached;
        }

        // Chests we can remove from: owned only, whatever the mode.
        static List<Container> Writable() =>
            Containers.Owned(Player.m_localPlayer.transform.position, Radius.Value);

        // ---------- Moments where ownership is requested, and strict mode around consumption ----------

        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Show), typeof(Container), typeof(int))]
        static class Show_Patch
        {
            static void Postfix(Container container)
            {
                var p = Player.m_localPlayer;
                if (container == null && p != null && p.GetCurrentCraftingStation() != null) Claim("workbench opened");
            }
        }

        [HarmonyPatch(typeof(InventoryGui), "OnCraftPressed")]
        static class OnCraftPressed_Patch
        {
            static void Prefix() => Claim("Craft / Upgrade click");
        }

        // End of the progress bar: final check + consumption, with owned chests only.
        [HarmonyPatch(typeof(InventoryGui), "DoCrafting")]
        static class DoCrafting_Patch
        {
            static void Prefix() => strict = true;
            static void Finalizer() => strict = false;
        }

        [HarmonyPatch(typeof(Player), nameof(Player.TryPlacePiece))]
        static class TryPlacePiece_Patch
        {
            static void Postfix(bool __result) { if (__result) Claim("piece placed"); }
        }

        // ---------- Scope: game methods where the extended count applies ----------

        static void Enter() => scope++;
        static void Exit() { if (scope > 0) scope--; }

        [HarmonyPatch(typeof(Player), "HaveRequirementItems")]
        static class HaveRequirementItems_Patch
        {
            // "Only one ingredient of your choice" recipes: the game then takes the item directly from the inventory,
            // we do not extend them. "Discover" mode: no extension either (chests do not unlock anything).
            static void Prefix(Recipe piece, bool discover, out bool __state)
            {
                __state = !discover && !piece.m_requireOnlyOneIngredient;
                if (__state) Enter();
            }
            static void Finalizer(bool __state) { if (__state) Exit(); }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.HaveRequirements), typeof(Piece), typeof(Player.RequirementMode))]
        static class HaveRequirementsPiece_Patch
        {
            static void Prefix(Player.RequirementMode mode, out bool __state)
            {
                __state = mode != Player.RequirementMode.IsKnown;
                if (__state) Enter();
            }
            static void Finalizer(bool __state) { if (__state) Exit(); }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.ConsumeResources))]
        static class ConsumeResources_Patch
        {
            static void Prefix() => Enter();
            static void Finalizer() => Exit();
        }

        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.SetupRequirement))]
        static class SetupRequirement_Patch
        {
            static void Prefix() => Enter();
            static void Finalizer() => Exit();
        }

        // ---------- Player inventory extended to the chests ----------

        [HarmonyPatch(typeof(Inventory), nameof(Inventory.CountItems))]
        static class CountItems_Patch
        {
            static void Postfix(Inventory __instance, string name, int quality, bool matchWorldLevel, ref int __result)
            {
                if (!Active || name == null || !IsPlayerInv(__instance)) return;
                scope--; // avoid recursion
                try
                {
                    foreach (var c in Sources()) __result += c.GetInventory().CountItems(name, quality, matchWorldLevel);
                }
                finally { scope++; }
            }
        }

        [HarmonyPatch(typeof(Inventory), nameof(Inventory.HaveItem), typeof(string), typeof(bool))]
        static class HaveItem_Patch
        {
            static void Postfix(Inventory __instance, string name, bool matchWorldLevel, ref bool __result)
            {
                if (__result || !Active || !IsPlayerInv(__instance)) return;
                scope--;
                try
                {
                    foreach (var c in Sources())
                        if (c.GetInventory().HaveItem(name, matchWorldLevel)) { __result = true; break; }
                }
                finally { scope++; }
            }
        }

        [HarmonyPatch(typeof(Inventory), nameof(Inventory.RemoveItem), typeof(string), typeof(int), typeof(int), typeof(bool))]
        static class RemoveItem_Patch
        {
            static void Prefix(Inventory __instance, string name, ref int amount, int itemQuality, bool worldLevelBased)
            {
                if (!Active || !IsPlayerInv(__instance)) return;
                scope--;
                try
                {
                    // We let the game remove what the player carries; the rest comes from the chests.
                    int own = CountOwn(__instance, name, itemQuality, worldLevelBased);
                    int missing = amount - own;
                    if (missing <= 0) return;
                    amount = own;
                    foreach (var c in Writable())
                    {
                        if (missing <= 0) break;
                        var inv = c.GetInventory();
                        int have = CountOwn(inv, name, itemQuality, worldLevelBased);
                        if (have <= 0) continue;
                        int take = Mathf.Min(have, missing);
                        inv.RemoveItem(name, take, itemQuality, worldLevelBased); // owner: save + network sync
                        missing -= take;
                    }
                }
                finally { scope++; }
            }

            static int CountOwn(Inventory inv, string name, int quality, bool worldLevelBased)
            {
                int n = 0;
                foreach (var item in inv.GetAllItems())
                    if (item.m_shared.m_name == name && (quality < 0 || item.m_quality == quality) &&
                        (!worldLevelBased || item.m_worldLevel >= Game.m_worldLevel))
                        n += item.m_stack;
                return n;
            }
        }
    }
}
