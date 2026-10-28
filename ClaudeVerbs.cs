using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using UnityEngine;

namespace BryansGoodies
{
    // Selector + verb language for the /claude scripts (see ClaudeBridge.cs for the plumbing).
    //
    // A selector names things in the world; every verb below takes the same selector words, in any order:
    //   categories : pieces (built), items (loose on the ground), creatures, players, nature (trees, rocks, bushes), all
    //   zone       : r <m> (horizontal radius, default 15), at <x> <y> <z> | here (center, default: the requester),
    //                y <min> <max>, cone <deg> (in front of the requester), box <x1> <z1> <x2> <z2>
    //   pick       : like <name part>, nearest [n], look (what the requester is looking at), id <zdo id> [<zdo id>...]
    // Anything else is a verb argument (e.g. force 60).
    //
    // Reading verbs (result sent back to the host, claude\info.jsonl):
    //   scan <selector>            one line per thing: category name id x y z rot, then health/support (pieces), level/tamed (creatures), stack/quality (items)
    //   prefabs like <name part>   searches the game's prefab names (up to 60)
    //   info                       the requester: health, stamina, equipment, effects
    //   where                      the requester: position, biome, ground, time of day, weather
//   known [like <part>]        what the requester has unlocked: recipes (items, pieces) and discovered materials
    // Acting verbs on a selection (a category is required; "all" must be explicit):
    //   remove <selector> [drop]   pieces vanish without giving materials back (add "drop" to get them), the rest is destroyed
    //   kill <selector>            creatures
    //   heal <selector>            creatures and players
    //   tame <selector>            creatures that can be tamed
    //   push <selector> force <n>  creatures and loose objects are thrown away from the requester (or from "at")
    // Generic (reflection), for anything not covered above:
//   on <selector> <Component.Method|Method> [args...]   calls a method on each selected thing (first component that has it); result reported
//   set <selector> <Component.field|field> <value>      writes a field on each selected thing
// Requester and world:
    //   fly on|off, god on|off, time <0..1|normal> (local time of day, 0.5 = noon)
    public static class ClaudeVerbs
    {
        class Thing
        {
            public string cat; public GameObject go; public ZNetView nv; public float dist;
            public string Name => go.name.Replace("(Clone)", "");
            public string Id => nv != null && nv.IsValid() ? nv.GetZDO().m_uid.ToString() : "-";
        }

        class Selector
        {
            public HashSet<string> cats = new HashSet<string>();
            public float r = 15f; public Vector3 center; public bool centerSet;
            public float yMin = float.MinValue, yMax = float.MaxValue;
            public float cone = -1f; public bool hasBox; public float x1, z1, x2, z2;
            public string like; public int nearest = -1; public bool look; public List<string> ids = new List<string>();
            public List<string> rest = new List<string>();
        }

        static readonly string[] Cats = { "pieces", "items", "creatures", "players", "nature", "all" };

        static Selector Parse(string[] a, Player p)
        {
            var s = new Selector { center = p.transform.position };
            for (int i = 0; i < a.Length; i++)
            {
                string t = a[i].ToLowerInvariant();
                if (Array.IndexOf(Cats, t) >= 0) s.cats.Add(t);
                else if (t == "r" && i + 1 < a.Length) { s.r = Pf(a[++i]); }
                else if (t == "at" && i + 3 < a.Length) { s.center = new Vector3(Pf(a[i + 1]), Pf(a[i + 2]), Pf(a[i + 3])); s.centerSet = true; i += 3; }
                else if (t == "here") { s.center = p.transform.position; }
                else if (t == "y" && i + 2 < a.Length) { s.yMin = Pf(a[i + 1]); s.yMax = Pf(a[i + 2]); i += 2; }
                else if (t == "cone" && i + 1 < a.Length) { s.cone = Pf(a[++i]); }
                else if (t == "box" && i + 4 < a.Length) { s.hasBox = true; s.x1 = Pf(a[i + 1]); s.z1 = Pf(a[i + 2]); s.x2 = Pf(a[i + 3]); s.z2 = Pf(a[i + 4]); i += 4; }
                else if (t == "like" && i + 1 < a.Length) { s.like = a[++i]; }
                else if (t == "nearest") { s.nearest = i + 1 < a.Length && int.TryParse(a[i + 1], out var n) ? Math.Max(1, n) : 1; if (i + 1 < a.Length && int.TryParse(a[i + 1], out _)) i++; }
                else if (t == "look") s.look = true;
                else if (t == "id") { while (i + 1 < a.Length && a[i + 1].Contains(":")) s.ids.Add(a[++i]); }
                else s.rest.Add(a[i]);
            }
            return s;
        }

        static string Category(ZNetView nv, GameObject go)
        {
            if (go.GetComponent<Player>() != null) return "players";
            if (go.GetComponent<Character>() != null) return "creatures";
            if (go.GetComponent<ItemDrop>() != null) return "items";
            if (go.GetComponent<WearNTear>() != null) return "pieces";
            if (go.GetComponent<TreeBase>() != null || go.GetComponent<TreeLog>() != null || go.GetComponent<MineRock>() != null ||
                go.GetComponent<MineRock5>() != null || go.GetComponent<Destructible>() != null) return "nature";
            return null;
        }

        static List<Thing> Select(Selector s, Player p, bool needCategory)
        {
            if (needCategory && s.cats.Count == 0 && !s.look && s.ids.Count == 0) throw new Exception("say what: pieces, items, creatures, players, nature or all");
            bool all = s.cats.Contains("all") || (s.cats.Count == 0);
            var fwd = p.transform.forward; fwd.y = 0f; fwd.Normalize();
            var res = new List<Thing>();
            GameObject lookRoot = null;
            if (s.look) { var h = p.GetHoverObject(); var hv = h != null ? h.GetComponentInParent<ZNetView>() : null; lookRoot = hv != null ? hv.gameObject : h; }
            foreach (var nv in UnityEngine.Object.FindObjectsByType<ZNetView>(FindObjectsSortMode.None))
            {
                if (nv == null || !nv.IsValid()) continue;
                var go = nv.gameObject;
                if (go == p.gameObject && !s.cats.Contains("players")) continue;
                string cat = Category(nv, go);
                if (cat == null) continue;
                if (!all && !s.cats.Contains(cat)) continue;
                if (s.look && go != lookRoot) continue;
                if (s.ids.Count > 0 && !s.ids.Contains(nv.GetZDO().m_uid.ToString())) continue;
                var pos = go.transform.position;
                if (pos.y < s.yMin || pos.y > s.yMax) continue;
                if (s.hasBox && (pos.x < Mathf.Min(s.x1, s.x2) || pos.x > Mathf.Max(s.x1, s.x2) || pos.z < Mathf.Min(s.z1, s.z2) || pos.z > Mathf.Max(s.z1, s.z2))) continue;
                var d = pos - s.center; d.y = 0f;
                if (!s.hasBox && d.magnitude > s.r) continue;
                if (s.cone > 0f && (d.magnitude < 0.01f || Vector3.Angle(fwd, d) > s.cone / 2f)) continue;
                string name = go.name.Replace("(Clone)", "");
                if (s.like != null && name.IndexOf(s.like, StringComparison.OrdinalIgnoreCase) < 0) continue;
                res.Add(new Thing { cat = cat, go = go, nv = nv, dist = d.magnitude });
            }
            res.Sort((x, y) => x.dist.CompareTo(y.dist));
            if (s.nearest > 0 && res.Count > s.nearest) res.RemoveRange(s.nearest, res.Count - s.nearest);
            return res;
        }

        // ---------- Entry point: returns false if the verb is not one of ours ----------

        public static bool TryExecute(Player p, string cmd, string[] a, Action<string> report)
        {
            switch (cmd)
            {
                case "scan": Scan(p, a, report); return true;
                case "prefabs": Prefabs(a, report); return true;
                case "known": Known(p, a, report); return true;
                case "info": Info(p, report); return true;
                case "where": Where(p, report); return true;
                case "remove":
                {
                    // Pieces never give their materials back (what Claude builds costs nothing), unless "drop" is given.
                    bool drop = Array.IndexOf(a, "drop") >= 0;
                    Act(p, Array.FindAll(a, x => x != "drop"), report, "remove", t =>
                    {
                        Own(t);
                        var w = t.go.GetComponent<WearNTear>(); var d = t.go.GetComponent<Destructible>();
                        if (w != null) w.Remove(!drop); else if (d != null) d.Destroy(); else t.nv.Destroy();
                    });
                    return true;
                }
                case "kill": Act(p, a, report, "kill", t => { var c = t.go.GetComponent<Character>() ?? throw new Exception("not a creature"); Own(t); c.SetHealth(0f); }); return true;
                case "heal": Act(p, a, report, "heal", t => { var c = t.go.GetComponent<Character>() ?? throw new Exception("not a creature"); Own(t); c.SetHealth(c.GetMaxHealth()); }); return true;
                case "tame": Act(p, a, report, "tame", t =>
                {
                    var tm = t.go.GetComponent<Tameable>() ?? throw new Exception("cannot be tamed");
                    Own(t);
                    var m = AccessTools.Method(typeof(Tameable), "Tame");
                    if (m != null) m.Invoke(tm, null);
                    else { t.nv.GetZDO().Set(ZDOVars.s_tamed, true); t.nv.GetZDO().Set(ZDOVars.s_tameTimeLeft, 0f); }
                }); return true;
                case "push":
                {
                    var s = Parse(a, p);
                    float force = 60f;
                    for (int i = 0; i < s.rest.Count; i++) if (s.rest[i] == "force" && i + 1 < s.rest.Count) force = Pf(s.rest[i + 1]);
                    int n = 0;
                    foreach (var t in Select(s, p, true))
                    {
                        var dir = t.go.transform.position - (s.centerSet ? s.center : p.transform.position); dir.y = 0f;
                        if (dir.magnitude < 0.01f) dir = p.transform.forward;
                        dir = (dir.normalized + Vector3.up * 0.6f).normalized;
                        Own(t);
                        var c = t.go.GetComponent<Character>();
                        var rb = t.go.GetComponentInChildren<Rigidbody>();
                        if (c != null) { c.ApplyPushback(dir, force); c.Stagger(-dir); n++; }
                        else if (rb != null && !rb.isKinematic) { rb.AddForce(dir * force * 8f, ForceMode.Impulse); n++; }
                    }
                    report($"push: {n} thing(s) pushed with force {force:0}");
                    return true;
                }
                case "on": // on <selector> <Component.Method|Method> [args...]: calls a method on each selected thing (reflection, result reported)
                {
                    var s = Parse(a, p);
                    if (s.rest.Count == 0) throw new Exception("on <selector> <method> [args]");
                    string member = s.rest[0]; var args = s.rest.GetRange(1, s.rest.Count - 1).ToArray();
                    int n = 0; var lines = new List<string>();
                    foreach (var t in Select(s, p, true))
                    {
                        try { Own(t); lines.Add(t.Name + " " + t.Id + ": " + Reflect.Invoke(t.go, member, args)); n++; }
                        catch (Exception e) { lines.Add(t.Name + " " + t.Id + ": failed: " + e.Message); }
                        if (lines.Count >= 60) { lines.Add("..."); break; }
                    }
                    report($"on {member}: {n} call(s)\n" + string.Join("\n", lines));
                    return true;
                }
                case "set": // set <selector> <Component.field|field> <value>: writes a field on each selected thing
                {
                    var s = Parse(a, p);
                    if (s.rest.Count < 2) throw new Exception("set <selector> <field> <value>");
                    string member = s.rest[0], value = string.Join(" ", s.rest.GetRange(1, s.rest.Count - 1));
                    int n = 0; var fails = new List<string>();
                    foreach (var t in Select(s, p, true))
                    {
                        try { Own(t); Reflect.SetField(t.go, member, value); n++; }
                        catch (Exception e) { fails.Add(t.Name + ": " + e.Message); }
                    }
                    report($"set {member} = {value}: {n} thing(s)" + (fails.Count > 0 ? ", failed: " + string.Join("; ", fails.GetRange(0, Math.Min(5, fails.Count))) : ""));
                    return true;
                }
                case "fly": { bool on = a.Length == 0 || a[0] != "off"; if (On(p, "m_debugFly") != on) p.ToggleDebugFly(); report("fly " + (on ? "on" : "off")); return true; }
                case "god": { bool on = a.Length == 0 || a[0] != "off"; p.SetGodMode(on); report("god " + (on ? "on" : "off")); return true; }
                case "time":
                {
                    if (EnvMan.instance == null) throw new Exception("no environment");
                    if (a.Length == 0 || a[0] == "normal") EnvMan.instance.m_debugTimeOfDay = false;
                    else { EnvMan.instance.m_debugTime = Mathf.Clamp01(Pf(a[0])); EnvMan.instance.m_debugTimeOfDay = true; }
                    report("time " + (EnvMan.instance.m_debugTimeOfDay ? EnvMan.instance.m_debugTime.ToString("0.00", CultureInfo.InvariantCulture) + " (local)" : "normal"));
                    return true;
                }
            }
            return false;
        }

        static bool On(Player p, string field) { var f = AccessTools.Field(typeof(Player), field); return f != null && (bool)f.GetValue(p); }
        static void Own(Thing t) { if (t.nv != null && t.nv.IsValid() && !t.nv.IsOwner()) t.nv.ClaimOwnership(); }

        static void Act(Player p, string[] a, Action<string> report, string verb, Action<Thing> act)
        {
            var s = Parse(a, p);
            int n = 0; var failed = new List<string>();
            foreach (var t in Select(s, p, true))
            {
                try { act(t); n++; }
                catch (Exception e) { failed.Add(t.Name + ": " + e.Message); }
            }
            report($"{verb}: {n} thing(s)" + (failed.Count > 0 ? ", failed " + failed.Count + ": " + string.Join("; ", failed.ToArray(), 0, Math.Min(5, failed.Count)) : ""));
        }

        // ---------- Reading ----------

        static void Scan(Player p, string[] a, Action<string> report)
        {
            var s = Parse(a, p);
            if (s.cats.Count == 0 && !s.look && s.ids.Count == 0) s.cats.Add("pieces"); // backward compatible default
            var things = Select(s, p, false);
            var pos = p.transform.position;
            report($"scan {string.Join(",", s.cats)} r {s.r:0} around {F(s.center.x)} {F(s.center.y)} {F(s.center.z)}; player at {F(pos.x)} {F(pos.y)} {F(pos.z)} facing {F(p.transform.forward.x)} {F(p.transform.forward.z)}; ground {F(GroundAt(s.center))}");
            const int cap = 1200; int shown = 0;
            foreach (var t in things)
            {
                if (shown++ >= cap) break;
                var tr = t.go.transform;
                string line = $"{t.cat} {t.Name} id {t.Id} {F(tr.position.x)} {F(tr.position.y)} {F(tr.position.z)} rot {tr.eulerAngles.y:0}";
                var w = t.go.GetComponent<WearNTear>();
                if (w != null) line += $" health {Mathf.RoundToInt(w.GetHealthPercentage() * 100)}% support {SupportText(w)}";
                var c = t.go.GetComponent<Character>();
                if (c != null) line += $" health {c.GetHealth():0}/{c.GetMaxHealth():0} level {c.GetLevel()}{(c.IsTamed() ? " tamed" : "")}{(c.IsBoss() ? " boss" : "")}";
                var it = t.go.GetComponent<ItemDrop>();
                if (it != null) line += $" stack {it.m_itemData.m_stack} quality {it.m_itemData.m_quality}";
                report(line);
            }
            report(shown > cap ? $"{things.Count} thing(s), {cap} shown: narrow the selector" : $"{things.Count} thing(s)");
        }

        static void Prefabs(string[] a, Action<string> report)
        {
            string like = null;
            for (int i = 0; i < a.Length; i++) if (a[i] == "like" && i + 1 < a.Length) like = a[i + 1]; else if (like == null) like = a[i];
            if (string.IsNullOrEmpty(like)) throw new Exception("prefabs like <name part>");
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            if (ZNetScene.instance != null) foreach (var g in ZNetScene.instance.m_prefabs) if (g != null && g.name.IndexOf(like, StringComparison.OrdinalIgnoreCase) >= 0) names.Add(g.name);
            if (ObjectDB.instance != null) foreach (var g in ObjectDB.instance.m_items) if (g != null && g.name.IndexOf(like, StringComparison.OrdinalIgnoreCase) >= 0) names.Add(g.name);
            int n = 0; var list = new List<string>();
            foreach (var nm in names) { if (n++ < 60) list.Add(nm); }
            report($"prefabs like {like}: {names.Count} match(es)" + (names.Count > 60 ? " (60 shown)" : "") + ": " + string.Join(", ", list));
        }

        // What the player has unlocked: recipes (items and build pieces) and discovered materials.
        static readonly System.Reflection.FieldInfo KnownRecipes = AccessTools.Field(typeof(Player), "m_knownRecipes");
        static readonly System.Reflection.FieldInfo KnownMaterial = AccessTools.Field(typeof(Player), "m_knownMaterial");
        static void Known(Player p, string[] a, Action<string> report)
        {
            string like = null;
            for (int i = 0; i < a.Length; i++) if (a[i] == "like" && i + 1 < a.Length) like = a[i + 1];
            var recipes = KnownRecipes?.GetValue(p) as HashSet<string> ?? new HashSet<string>();
            var materials = KnownMaterial?.GetValue(p) as HashSet<string> ?? new HashSet<string>();
            List<string> Pick(HashSet<string> set)
            {
                var l = new List<string>();
                foreach (var n in set) if (like == null || n.IndexOf(like, StringComparison.OrdinalIgnoreCase) >= 0) l.Add(n.Replace("$item_", "").Replace("$piece_", "piece:"));
                l.Sort(StringComparer.OrdinalIgnoreCase);
                return l;
            }
            var r = Pick(recipes); var m = Pick(materials);
            report($"known recipes: {r.Count}{(like != null ? " like " + like : "")}: " + string.Join(", ", r.GetRange(0, Math.Min(r.Count, 120))) + (r.Count > 120 ? " ..." : ""));
            report($"known materials: {m.Count}: " + string.Join(", ", m.GetRange(0, Math.Min(m.Count, 120))) + (m.Count > 120 ? " ..." : ""));
        }

        static void Info(Player p, Action<string> report)
        {
            var inv = p.GetInventory();
            var eq = new List<string>();
            foreach (var it in inv.GetAllItems()) if (it.m_equipped) eq.Add(it.m_shared.m_name.Replace("$item_", "") + " q" + it.m_quality);
            var effects = new List<string>();
            foreach (var se in p.GetSEMan().GetStatusEffects()) effects.Add(se.m_name.Replace("$se_", "").Replace("$", ""));
            report($"info {p.GetPlayerName()}: health {p.GetHealth():0}/{p.GetMaxHealth():0} stamina {p.GetStamina():0} fly {(On(p, "m_debugFly") ? "on" : "off")} god {(On(p, "m_godMode") ? "on" : "off")}");
            report("equipped: " + (eq.Count > 0 ? string.Join(", ", eq) : "nothing"));
            report("effects: " + (effects.Count > 0 ? string.Join(", ", effects) : "none"));
            report($"inventory: {inv.NrOfItems()} slot(s) used of {inv.GetWidth() * inv.GetHeight()}");
        }

        static void Where(Player p, Action<string> report)
        {
            var pos = p.transform.position; var fwd = p.transform.forward;
            string env = EnvMan.instance != null ? EnvMan.instance.GetCurrentEnvironment()?.m_name ?? "?" : "?";
            float day = EnvMan.instance != null ? EnvMan.instance.GetDayFraction() : 0f;
            report($"where: {F(pos.x)} {F(pos.y)} {F(pos.z)} facing {F(fwd.x)} {F(fwd.z)} (yaw {Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg:0}) biome {p.GetCurrentBiome()} ground {F(GroundAt(pos))} day {(EnvMan.instance != null ? EnvMan.instance.GetDay() : 0)} time {day:0.00} weather {env}{(EnvMan.instance != null && EnvMan.instance.m_debugTimeOfDay ? " (local time forced)" : "")}");
        }

        // ---------- Reflection on a world object: "Component.Member" or "Member" (first component that has it) ----------

        static class Reflect
        {
            const System.Reflection.BindingFlags Any = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

            static IEnumerable<Component> Targets(GameObject go, ref string member)
            {
                int dot = member.IndexOf('.');
                if (dot > 0)
                {
                    string comp = member.Substring(0, dot); member = member.Substring(dot + 1);
                    var list = new List<Component>();
                    foreach (var c in go.GetComponentsInChildren<Component>(true)) if (c != null && c.GetType().Name.Equals(comp, StringComparison.OrdinalIgnoreCase)) list.Add(c);
                    if (list.Count == 0) throw new Exception("no component " + comp);
                    return list;
                }
                return go.GetComponents<Component>();
            }

            public static string Invoke(GameObject go, string member, string[] args)
            {
                foreach (var c in Targets(go, ref member))
                {
                    if (c == null) continue;
                    foreach (var m in c.GetType().GetMethods(Any))
                    {
                        if (!m.Name.Equals(member, StringComparison.OrdinalIgnoreCase) || m.GetParameters().Length != args.Length) continue;
                        var ps = m.GetParameters(); var vals = new object[ps.Length]; bool ok = true;
                        for (int i = 0; i < ps.Length && ok; i++) ok = TryConvert(args[i], ps[i].ParameterType, out vals[i]);
                        if (!ok) continue;
                        var r = m.Invoke(c, vals);
                        return m.ReturnType == typeof(void) ? "ok" : (r == null ? "null" : r.ToString());
                    }
                    var f = c.GetType().GetField(member, Any);
                    if (f != null && args.Length == 0) { var v = f.GetValue(c); return v == null ? "null" : v.ToString(); }
                }
                throw new Exception("no method or field '" + member + "' taking " + args.Length + " argument(s)");
            }

            public static void SetField(GameObject go, string member, string value)
            {
                foreach (var c in Targets(go, ref member))
                {
                    if (c == null) continue;
                    var f = c.GetType().GetField(member, Any);
                    if (f == null) continue;
                    if (!TryConvert(value, f.FieldType, out var v)) throw new Exception("cannot convert '" + value + "' to " + f.FieldType.Name);
                    f.SetValue(c, v);
                    return;
                }
                throw new Exception("no field '" + member + "'");
            }

            static bool TryConvert(string s, Type t, out object v)
            {
                v = null;
                try
                {
                    if (t == typeof(string)) { v = s; return true; }
                    if (t == typeof(bool)) { v = s == "true" || s == "1" || s == "on"; return true; }
                    if (t == typeof(int)) { v = int.Parse(s, CultureInfo.InvariantCulture); return true; }
                    if (t == typeof(long)) { v = long.Parse(s, CultureInfo.InvariantCulture); return true; }
                    if (t == typeof(float)) { v = float.Parse(s, CultureInfo.InvariantCulture); return true; }
                    if (t == typeof(double)) { v = double.Parse(s, CultureInfo.InvariantCulture); return true; }
                    if (t.IsEnum) { v = Enum.Parse(t, s, true); return true; }
                    if (t == typeof(Vector3)) { var p = s.Split(','); v = new Vector3(Pf(p[0]), Pf(p[1]), Pf(p[2])); return true; }
                    if (t == typeof(GameObject)) { v = ZNetScene.instance?.GetPrefab(s); return v != null; }
                    return false;
                }
                catch { return false; }
            }
        }

        // ---------- Shared helpers ----------

        static string F(float v) => v.ToString("0.0", CultureInfo.InvariantCulture);
        static float Pf(string s) => float.Parse(s, CultureInfo.InvariantCulture);
        static float GroundAt(Vector3 pos) => ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(pos, out float h) ? h : pos.y;

        static readonly System.Reflection.FieldInfo SupportField = AccessTools.Field(typeof(WearNTear), "m_support");
        static readonly System.Reflection.MethodInfo MaxSupportMethod = AccessTools.Method(typeof(WearNTear), "GetMaxSupport");
        // "current/max min <min>": the piece breaks when current <= min (10% of max for wood and stone, 1.3% for iron).
        static readonly System.Reflection.MethodInfo MaterialProps = AccessTools.Method(typeof(WearNTear), "GetMaterialProperties");
        static string SupportText(WearNTear w)
        {
            try
            {
                float sup = (float)SupportField.GetValue(w);
                var args = new object[] { 0f, 0f, 0f, 0f };
                MaterialProps.Invoke(w, args);
                return $"{sup:0}/{(float)args[0]:0} min {(float)args[1]:0}";
            }
            catch { return Mathf.RoundToInt(Support(w) * 100) + "%"; }
        }

        static float Support(WearNTear w)
        {
            try
            {
                float sup = (float)SupportField.GetValue(w);
                float max = MaxSupportMethod != null ? (float)MaxSupportMethod.Invoke(w, null) : 1f;
                return max > 0f ? Mathf.Clamp01(sup / max) : 1f;
            }
            catch { return 1f; }
        }
    }
}
