using HarmonyLib;
using MelonLoader;

namespace BryansGoodies
{
    // Nothing is lost on death: Player.OnDeath() calls CreateTombStone(), which empties the inventory
    // into a tombstone. We skip that call (equivalent to DeathTweaks KeepAllItems=true).
    public static class KeepItems
    {
        public static MelonPreferences_Entry<bool> Enabled;

        public static void Init(MelonPreferences_Category cat)
        {
            Enabled = Prefs.Entry(cat, "KeepItems_Enabled", true, "Keep the whole inventory on death");
        }

        [HarmonyPatch(typeof(Player), nameof(Player.CreateTombStone))]
        static class CreateTombStone_Patch
        {
            static bool Prefix() => !Enabled.Value;
        }
    }
}
