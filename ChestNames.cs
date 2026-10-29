using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace BryansGoodies
{
    // Named chests: Shift + E on a chest asks for a name (the game's sign input box). The name is stored in the
    // chest's network data, so every player sees it, and replaces "Chest" in the hover text and the window title.
    // An empty name brings back the game's name. Like a sign, writing the name makes us the chest's owner: refused
    // while another player has it open.
    public static class ChestNames
    {
        public static MelonPreferences_Entry<bool> Enabled;

        const string Key = "BG_ChestName";
        const int MaxLength = 32;

        static readonly AccessTools.FieldRef<Container, ZNetView> NView = AccessTools.FieldRefAccess<Container, ZNetView>("m_nview");
        static readonly AccessTools.FieldRef<InventoryGui, Container> CurrentContainer = AccessTools.FieldRefAccess<InventoryGui, Container>("m_currentContainer");
        static readonly System.Reflection.MethodInfo CheckAccessMethod = AccessTools.Method(typeof(Container), "CheckAccess");

        public static void Init(MelonPreferences_Category cat)
        {
            Enabled = Prefs.Entry(cat, "ChestNames_Enabled", true, "Rename chests (Shift + E), the name replaces \"Chest\" on hover");
        }

        // The chest's custom name, or null.
        public static string Get(Container c)
        {
            if (c == null) return null;
            var nv = NView(c);
            if (nv == null || !nv.IsValid()) return null;
            string name = nv.GetZDO().GetString(Key);
            return string.IsNullOrEmpty(name) ? null : name;
        }

        static bool Applies(Container c) => Enabled.Value && c != null && c.GetComponent<TombStone>() == null;

        // Receives the text typed in the game's input box.
        class Receiver : TextReceiver
        {
            readonly Container chest;
            public Receiver(Container c) { chest = c; }
            public string GetText() => Get(chest) ?? "";
            public void SetText(string text) => Rename(chest, text);
        }

        static void Rename(Container c, string text)
        {
            var p = Player.m_localPlayer;
            if (c == null || p == null) return;
            var nv = NView(c);
            if (nv == null || !nv.IsValid()) return;
            if (!nv.IsOwner() && nv.GetZDO().GetInt(ZDOVars.s_inUse) == 1) { p.Message(MessageHud.MessageType.Center, "$msg_inuse"); return; }
            text = (text ?? "").RemoveRichTextTags().Replace("\n", " ").Replace("\t", " ").Trim();
            if (text.Length > MaxLength) text = text.Substring(0, MaxLength);
            nv.ClaimOwnership();
            nv.GetZDO().Set(Key, text);
        }

        static bool CanRename(Container c, Humanoid character)
        {
            if (c.m_checkGuardStone && !PrivateArea.CheckAccess(c.transform.position)) return false;
            long id = Game.instance.GetPlayerProfile().GetPlayerID();
            if (!(bool)CheckAccessMethod.Invoke(c, new object[] { id })) { character.Message(MessageHud.MessageType.Center, "$msg_cantopen"); return false; }
            return true;
        }

        // Shift + E: rename instead of opening.
        [HarmonyPatch(typeof(Container), nameof(Container.Interact))]
        static class Interact_Patch
        {
            static bool Prefix(Container __instance, Humanoid character, bool hold, bool alt, ref bool __result)
            {
                if (!alt || hold || !Applies(__instance) || character != Player.m_localPlayer || TextInput.instance == null) return true;
                __result = true;
                if (CanRename(__instance, character))
                    TextInput.instance.RequestText(new Receiver(__instance), "Chest name", MaxLength);
                return false;
            }
        }

        // The custom name replaces the game's name at the start of the hover text, and the rename hint is added.
        [HarmonyPatch(typeof(Container), nameof(Container.GetHoverText))]
        static class HoverText_Patch
        {
            static void Postfix(Container __instance, ref string __result)
            {
                if (!Applies(__instance) || string.IsNullOrEmpty(__result)) return;
                string name = Get(__instance);
                if (name != null)
                {
                    string vanilla = Localization.instance.Localize(__instance.m_name);
                    if (__result.StartsWith(vanilla)) __result = name + __result.Substring(vanilla.Length);
                }
                if (__result.Contains("\n["))
                    __result += Localization.instance.Localize("\n[<color=yellow><b>$KEY_AltPlace + $KEY_Use</b></color>] Rename");
            }
        }

        [HarmonyPatch(typeof(Container), nameof(Container.GetHoverName))]
        static class HoverName_Patch
        {
            static void Postfix(Container __instance, ref string __result)
            {
                if (Applies(__instance) && Get(__instance) is string name) __result = name;
            }
        }

        // Title of the open chest window.
        [HarmonyPatch(typeof(InventoryGui), "UpdateContainer")]
        static class UpdateContainer_Patch
        {
            static void Postfix(InventoryGui __instance)
            {
                var c = CurrentContainer(__instance);
                if (Applies(c) && Get(c) is string name && __instance.m_containerName != null) __instance.m_containerName.text = name;
            }
        }
    }
}
