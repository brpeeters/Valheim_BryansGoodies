using System;
using System.Collections.Generic;
using MelonLoader;
using UnityEngine;

namespace BryansGoodies
{
    // Hidden surprise: for some characters, a low-tier item (weapon, armor, shield, cape...)
    // appears in the inventory once every 5 to 15 minutes, as if they had crafted it themselves.
    // The setting exists in the F7 menu, except for Thecovinette who does not see it.
    public static class Gifts
    {
        static readonly string[] Lucky = { "thecovinette", "overflow" };
        const string Hidden = "thecovinette";

        public static MelonPreferences_Entry<bool> Enabled;

        public static void Init(MelonPreferences_Category cat)
        {
            Enabled = Prefs.Entry(cat, "Gifts_Enabled", true, "Surprise gift (low-tier item every 5 to 15 min, some characters)");
        }

        // The setting stays hidden in the menu for the character concerned.
        public static bool ShowInMenu =>
            Player.m_localPlayer == null || !string.Equals(Player.m_localPlayer.GetPlayerName(), Hidden, StringComparison.OrdinalIgnoreCase);

        // Early-game materials: a recipe whose ingredients are all in here yields a "low-tier" item.
        static readonly HashSet<string> LowMaterials = new HashSet<string>
        {
            "Wood", "RoundLog", "FineWood", "Stone", "Flint", "Resin", "Coal", "Feathers", "BoneFragments",
            "LeatherScraps", "DeerHide", "TrollHide", "Raspberry", "Honey", "GreydwarfEye",
        };

        static readonly HashSet<ItemDrop.ItemData.ItemType> Gear = new HashSet<ItemDrop.ItemData.ItemType>
        {
            ItemDrop.ItemData.ItemType.OneHandedWeapon, ItemDrop.ItemData.ItemType.TwoHandedWeapon,
            ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft, ItemDrop.ItemData.ItemType.Bow,
            ItemDrop.ItemData.ItemType.Shield, ItemDrop.ItemData.ItemType.Helmet, ItemDrop.ItemData.ItemType.Chest,
            ItemDrop.ItemData.ItemType.Legs, ItemDrop.ItemData.ItemType.Shoulder,
        };

        static Player current;
        static float next;
        static List<string> pool;

        static bool IsLucky(Player p)
        {
            string n = p.GetPlayerName();
            foreach (var l in Lucky) if (string.Equals(n, l, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static float Delay() => UnityEngine.Random.Range(5f * 60f, 15f * 60f);

        // Hot reload: the countdown to the next gift continues instead of restarting.
        public static void Export(Dictionary<string, object> h) => h["gifts"] = new object[] { current, next };
        public static void Import(Dictionary<string, object> h)
        {
            if (!h.TryGetValue("gifts", out var o) || !(o is object[] g)) return;
            current = g[0] as Player; next = (float)g[1];
        }

        public static void Update()
        {
            var p = Player.m_localPlayer;
            if (p == null) { current = null; return; }
            if (p != current) { current = p; next = Time.time + Delay(); return; } // new session: start over
            if (Time.time < next) return;
            next = Time.time + Delay();
            if (!Enabled.Value || !IsLucky(p) || p.IsDead() || p.InCutscene() || p.IsTeleporting()) return;
            try { Give(p); }
            catch (Exception e) { Main.Log.Warning("Gift: " + e); }
        }

        static List<string> Pool()
        {
            if (pool != null && pool.Count > 0) return pool;
            var set = new HashSet<string>();
            foreach (var r in ObjectDB.instance.m_recipes)
            {
                if (r == null || !r.m_enabled || r.m_item == null || r.m_resources == null || r.m_resources.Length == 0) continue;
                if (!Gear.Contains(r.m_item.m_itemData.m_shared.m_itemType)) continue;
                bool low = true;
                foreach (var req in r.m_resources)
                    if (req.m_resItem == null || !LowMaterials.Contains(req.m_resItem.gameObject.name)) { low = false; break; }
                if (low) set.Add(r.m_item.gameObject.name);
            }
            pool = new List<string>(set);
            return pool;
        }

        static void Give(Player p)
        {
            var names = Pool();
            if (names.Count == 0) return;
            string name = names[UnityEngine.Random.Range(0, names.Count)];
            int quality = UnityEngine.Random.value < 0.25f ? 2 : 1;
            long id = p.GetPlayerID();
            string crafter = p.GetPlayerName();

            var item = p.GetInventory().AddItem(name, 1, quality, 0, id, crafter, false);
            if (item != null)
            {
                p.Message(MessageHud.MessageType.TopLeft, "$msg_added " + item.m_shared.m_name, 1, item.GetIcon());
                return;
            }
            // Inventory full: the item drops at the player's feet.
            var prefab = ObjectDB.instance.GetItemPrefab(name);
            if (prefab == null) return;
            var go = UnityEngine.Object.Instantiate(prefab, p.transform.position + p.transform.forward * 0.8f + Vector3.up, Quaternion.identity);
            var drop = go.GetComponent<ItemDrop>();
            if (drop == null) return;
            drop.SetQuality(quality);
            drop.m_itemData.m_crafterID = id;
            drop.m_itemData.m_crafterName = crafter;
            drop.m_itemData.m_durability = drop.m_itemData.GetMaxDurability();
            var nv = go.GetComponent<ZNetView>();
            if (nv != null && nv.IsValid()) ItemDrop.SaveToZDO(drop.m_itemData, nv.GetZDO());
        }
    }
}
