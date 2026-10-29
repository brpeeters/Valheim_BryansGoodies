using System;
using System.Collections.Generic;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace BryansGoodies
{
    // Mod payload: bundles the small home-made mods, each one toggleable from the in-game menu (F7 by default).
    //
    // This assembly (BryansGoodies.Core.dll) is NOT a MelonMod: it is loaded (and hot-reloaded) by the loader
    // (BryansGoodies.dll in Mods), which installs it into UserData\BryansGoodies\ and updates it from GitHub.
    // The loader calls these through reflection: Version, Start(), Stop(), Update(), OnGUI(), Busy().
    // The version lives in Version.g.cs, generated at build time from <ModVersion> in the csproj.
    public static partial class Main
    {
        public static readonly MelonLogger.Instance Log = new MelonLogger.Instance("Bryan's Goodies");
        public static MelonPreferences_Category Cat;
        public static MelonPreferences_Entry<KeyCode> MenuKey;
        static HarmonyLib.Harmony harmony;

        public static void Start() => StartWithState(null);

        // handoff: the state exported by the previous version's StopWithState() (null on a cold start).
        // Separate names (not overloads) so that older loaders, which look up Start()/Stop() by name, still work.
        public static void StartWithState(object handoff)
        {
            Cat = MelonPreferences.CreateCategory("BryansGoodies", "Bryan's Goodies");
            MenuKey = Prefs.Entry(Cat, "MenuKey", KeyCode.F7, "Configuration menu key");

            KeepItems.Init(Cat);
            CallCommand.Init(Cat);
            Compass.Init(Cat);
            PlayerColors.Init(Cat);
            QuickStack.Init(Cat);
            CraftFromContainers.Init(Cat);
            CraftQuantity.Init(Cat);
            CraftSearch.Init(Cat);
            ChestNames.Init(Cat);
            Gifts.Init(Cat);
            Updater.Init();
            ClaudeBridge.Init(Cat);

            harmony = new HarmonyLib.Harmony("bryansgoodies.payload");
            harmony.PatchAll(typeof(Main).Assembly);

            // Console commands: the game's table is static and overwrites a command with the same name,
            // so we can (re)register at any time, including after a hot reload.
            CallCommand.Register();
            ClaudeBridge.Register();
            PlayerColors.RegisterCommand();

            // The loader always passes a dictionary: its services under "host", and, after a hot reload, the state
            // exported by the previous version. The state is made of named entries holding only shared types (game
            // objects, numbers, Unity structs), never a type from this assembly. Each Import reads the entries it knows
            // (and may convert older formats, see h["from"]); a failing entry is skipped, it never prevents the start.
            var h = handoff as Dictionary<string, object> ?? new Dictionary<string, object>();
            Updater.Start(h.TryGetValue("host", out var hostObj) ? hostObj as Dictionary<string, object> : null);
            if (h.TryGetValue("from", out var f) && f is string from)
            {
                Take("containers", () => Containers.Import(h));
                Take("quickstack", () => QuickStack.Import(h));
                Take("crafting", () => CraftFromContainers.Import(h));
                Take("craftqty", () => CraftQuantity.Import(h));
                Take("craftsearch", () => CraftSearch.Import(h));
                Take("menu", () => ConfigMenu.Import(h));
                Take("colors", () => PlayerColors.Import(h));
                Take("gifts", () => Gifts.Import(h));
                Take("updater", () => Updater.Import(h));
                Take("claude", () => ClaudeBridge.Import(h));
                Log.Msg($"Version {Version} started, state taken over from {from}. Menu: {MenuKey.Value}");
            }
            else Log.Msg($"Version {Version} started. Menu: {MenuKey.Value}");
        }

        static void Take(string what, Action import)
        {
            try { import(); }
            catch (Exception e) { Log.Warning($"State handoff: '{what}' could not be taken over, skipped ({e.GetType().Name}: {e.Message})"); }
        }

        // Hot reload: exports the live state for the next version, then removes every patch of this version.
        // Stop() and the next Start() run in the same frame, from the loader's update loop, never in the middle of a
        // game method: no frame is ever rendered without the patches, so nothing needs to block the swap.
        // The network and console handlers are replaced by the next version when it registers itself.
        public static void Stop() => StopWithState();

        public static object StopWithState()
        {
            var h = new Dictionary<string, object> { ["from"] = Version };
            Give("containers", () => Containers.Export(h));
            Give("quickstack", () => QuickStack.Export(h));
            Give("crafting", () => CraftFromContainers.Export(h));
            Give("craftqty", () => CraftQuantity.Export(h));
            Give("craftsearch", () => CraftSearch.Export(h));
            Give("menu", () => ConfigMenu.Export(h));
            Give("colors", () => PlayerColors.Export(h));
            Give("gifts", () => Gifts.Export(h));
            Give("updater", () => Updater.Export(h));
            Give("claude", () => ClaudeBridge.Export(h));
            Give("updater.notes", () => Updater.ExportNotes(h));
            harmony?.UnpatchSelf();
            harmony = null;
            Log.Msg($"Version {Version} stopped, state handed over.");
            return h;
        }

        static void Give(string what, Action export)
        {
            try { export(); }
            catch (Exception e) { Log.Warning($"State handoff: '{what}' could not be exported ({e.GetType().Name}: {e.Message})"); }
        }


        public static void Update()
        {
            bool typing = Console.IsVisible() || (Chat.instance != null && Chat.instance.HasFocus()) || TextInput.IsVisible() || UiField.Typing;
            if (!typing && !ConfigMenu.WaitingForKey)
            {
                if (ZInput.GetKeyDown(MenuKey.Value, false)) ConfigMenu.Toggle();
                Compass.HandleKeys();
                QuickStack.HandleKeys();
            }
            PlayerColors.Update();
            Containers.Update();
            CraftFromContainers.Update();
            Gifts.Update();
            Updater.Update();
            ClaudeBridge.Update();
        }

        public static void OnGUI()
        {
            Compass.OnGUI();
            ConfigMenu.OnGUI();
            Updater.OnGUI();
        }

        public static void Save() => MelonPreferences.Save();
    }

    // Settings: reuses the entry if it already exists (hot reload: the category outlives the code).
    public static class Prefs
    {
        public static MelonPreferences_Entry<T> Entry<T>(MelonPreferences_Category cat, string id, T defaultValue, string description = null)
        {
            var existing = cat.GetEntry<T>(id);
            if (existing != null) return existing;
            return cat.CreateEntry(id, defaultValue, null, description);
        }
    }
}
