using HarmonyLib;
using MelonLoader;

namespace BryansGoodies
{
    // No skill loss on death: Player.OnDeath() calls Skills.OnDeath() on a hard death, which lowers every skill
    // by the death factor. We skip that call. The DeathSkillsReset world modifier (Skills.Clear()) is left alone.
    public static class NoSkillDrain
    {
        public static MelonPreferences_Entry<bool> Enabled;

        public static void Init(MelonPreferences_Category cat)
        {
            Enabled = Prefs.Entry(cat, "NoSkillDrain_Enabled", true, "Keep skill levels on death");
        }

        [HarmonyPatch(typeof(Skills), nameof(Skills.OnDeath))]
        static class OnDeath_Patch
        {
            static bool Prefix() => !Enabled.Value;
        }
    }
}
