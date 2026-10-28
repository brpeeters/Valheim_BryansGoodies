using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using MelonLoader;
using UnityEngine;

namespace BryansGoodies
{
    public static class CallCommand
    {
        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static MelonPreferences_Entry<bool> Enabled;

        public static void Init(MelonPreferences_Category cat)
        {
            Enabled = Prefs.Entry(cat, "Call_Enabled", true, "/call command (call C# methods)");
        }

        public static void Register()
        {
            new Terminal.ConsoleCommand("call",
                "[Type.member.method] [args...] : calls a C# method / reads or writes a field (e.g. call me.SetHealth 50)",
                args => Run(args));
        }

        // Used by the Claude bridge: "call <expression> [args...]" without a console context.
        public static string Eval(string line)
        {
            var tokens = Tokenize(line);
            if (tokens.Count == 0) throw new Exception("empty call");
            var result = Execute(tokens[0], tokens.Skip(1).ToArray(), out bool isVoid);
            return isVoid ? "OK" : Format(result);
        }

        public static void Run(Terminal.ConsoleEventArgs args)
        {
            var ctx = args.Context;
            if (!Enabled.Value) { ctx.AddString("/call is disabled (Bryan's Goodies menu)."); return; }
            var tokens = Tokenize(args.ArgsAll);
            if (tokens.Count == 0)
            {
                ctx.AddString("Usage: call Type.member[.member...] [args...]");
                ctx.AddString("  call me.SetHealth 50            (me = local player)");
                ctx.AddString("  call EnvMan.instance.m_debugTimeOfDay true");
                ctx.AddString("  call me.GetInventory().GetAllItems");
                return;
            }
            try
            {
                var result = Execute(tokens[0], tokens.Skip(1).ToArray(), out bool isVoid);
                ctx.AddString(isVoid ? "OK" : "=> " + Format(result));
            }
            catch (Exception e)
            {
                while (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
                ctx.AddString("<color=red>Error: " + e.GetType().Name + ": " + e.Message + "</color>");
                MelonLogger.Warning(e.ToString());
            }
        }

        static object Execute(string path, string[] args, out bool isVoid)
        {
            isVoid = false;
            var parts = path.Split('.');
            int i;
            object target;
            Type type;

            if (parts[0].Equals("me", StringComparison.OrdinalIgnoreCase))
            {
                target = Player.m_localPlayer ?? throw new Exception("no local player");
                type = target.GetType();
                i = 1;
            }
            else
            {
                // Longest prefix that matches a type (allows UnityEngine.Time.timeScale).
                type = null; i = 0;
                for (int n = parts.Length - 1; n >= 1 && type == null; n--)
                {
                    type = FindType(string.Join(".", parts, 0, n));
                    if (type != null) i = n;
                }
                if (type == null) throw new Exception("no type found in '" + path + "'");
                target = null;
            }
            if (i >= parts.Length) return target;

            for (; i < parts.Length; i++)
            {
                string name = parts[i];
                bool forceCall = name.EndsWith("()");
                if (forceCall) name = name.Substring(0, name.Length - 2);
                bool last = i == parts.Length - 1;
                string[] a = last ? args : new string[0];

                var methods = type.GetMethods(All).Where(m => m.Name == name && !m.IsGenericMethodDefinition).ToArray();
                if (methods.Length > 0 && (forceCall || last || !HasMember(type, name)))
                {
                    var m = PickOverload(methods, a, out object[] conv);
                    if (!m.IsStatic && target == null) throw new Exception(name + " is not static: an instance is required");
                    var r = m.Invoke(m.IsStatic ? null : target, conv);
                    if (last) { isVoid = m.ReturnType == typeof(void); return r; }
                    target = r; type = r?.GetType() ?? m.ReturnType;
                    if (r == null) throw new Exception(name + "() returned null");
                    continue;
                }

                var f = type.GetField(name, All);
                var p = f == null ? type.GetProperty(name, All) : null;
                if (f == null && p == null) throw new Exception("member '" + name + "' not found on " + type.FullName);
                Type mt = f != null ? f.FieldType : p.PropertyType;
                bool isStatic = f != null ? f.IsStatic : (p.GetGetMethod(true) ?? p.GetSetMethod(true)).IsStatic;
                if (!isStatic && target == null) throw new Exception(name + " is not static: an instance is required");
                object inst = isStatic ? null : target;

                if (last && a.Length > 0)
                {
                    object v = Convert(a.Length == 1 ? a[0] : string.Join(" ", a), mt);
                    if (f != null) f.SetValue(inst, v); else p.SetValue(inst, v, null);
                    return f != null ? f.GetValue(inst) : p.GetValue(inst, null);
                }
                object val = f != null ? f.GetValue(inst) : p.GetValue(inst, null);
                if (last) return val;
                if (val == null) throw new Exception(name + " is null");
                target = val; type = val.GetType();
            }
            return target;
        }

        static bool HasMember(Type t, string name) => t.GetField(name, All) != null || t.GetProperty(name, All) != null;

        static MethodInfo PickOverload(MethodInfo[] methods, string[] args, out object[] conv)
        {
            Exception lastErr = null;
            foreach (var m in methods.OrderBy(m => m.GetParameters().Length))
            {
                var ps = m.GetParameters();
                if (args.Length > ps.Length || ps.Count(p => !p.IsOptional) > args.Length) continue;
                try
                {
                    conv = new object[ps.Length];
                    for (int k = 0; k < ps.Length; k++)
                        conv[k] = k < args.Length ? Convert(args[k], ps[k].ParameterType) : ps[k].DefaultValue;
                    return m;
                }
                catch (Exception e) { lastErr = e; }
            }
            var sigs = string.Join(" | ", methods.Select(m => m.Name + "(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")"));
            throw new Exception("no overload matches " + args.Length + " argument(s)" + (lastErr != null ? " (" + lastErr.Message + ")" : "") + ". Signatures: " + sigs);
        }

        static object Convert(string s, Type t)
        {
            if (t.IsByRef) t = t.GetElementType();
            if (s == "null" && !t.IsValueType) return null;
            if (s.Equals("me", StringComparison.OrdinalIgnoreCase) && t.IsAssignableFrom(typeof(Player))) return Player.m_localPlayer;
            var u = Nullable.GetUnderlyingType(t);
            if (u != null) t = u;
            if (t == typeof(string) || t == typeof(object)) return s;
            if (t.IsEnum) return Enum.Parse(t, s, true);
            if (t == typeof(bool)) return s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase);
            if (t == typeof(Vector3) || t == typeof(Vector2) || t == typeof(Quaternion) || t == typeof(Color))
            {
                var f = s.Split(',').Select(x => float.Parse(x.Trim(), Inv)).ToArray();
                if (t == typeof(Vector3)) return new Vector3(f[0], f[1], f[2]);
                if (t == typeof(Vector2)) return new Vector2(f[0], f[1]);
                if (t == typeof(Quaternion)) return Quaternion.Euler(f[0], f[1], f[2]);
                return new Color(f[0], f[1], f[2], f.Length > 3 ? f[3] : 1f);
            }
            if (t == typeof(GameObject) || t == typeof(UnityEngine.Object)) return ZNetScene.instance?.GetPrefab(s) ?? throw new Exception("prefab '" + s + "' not found");
            if (t == typeof(ItemDrop.ItemData))
                return ObjectDB.instance?.GetItemPrefab(s)?.GetComponent<ItemDrop>()?.m_itemData.Clone() ?? throw new Exception("item '" + s + "' not found");
            if (typeof(IConvertible).IsAssignableFrom(t)) return System.Convert.ChangeType(s, t, Inv);
            throw new Exception("conversion to " + t.Name + " not supported");
        }

        static readonly Dictionary<string, Type> TypeCache = new Dictionary<string, Type>();

        static Type FindType(string name)
        {
            if (TypeCache.TryGetValue(name, out var cached)) return cached;
            Type found = null;
            var asms = AppDomain.CurrentDomain.GetAssemblies()
                .OrderBy(a => a.GetName().Name == "assembly_valheim" ? 0 : a.GetName().Name.StartsWith("UnityEngine") ? 1 : 2);
            foreach (var a in asms)
            {
                Type[] types;
                try { types = a.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types.Where(x => x != null).ToArray(); } catch { continue; }
                found = types.FirstOrDefault(t => t.FullName == name)
                     ?? types.FirstOrDefault(t => t.FullName != null && t.FullName.Replace('+', '.') == name)
                     ?? (name.Contains('.') ? null : types.FirstOrDefault(t => t.Name == name && !t.IsNested));
                if (found != null) break;
            }
            TypeCache[name] = found;
            return found;
        }

        static string Format(object o)
        {
            if (o == null) return "null";
            if (o is string s) return "\"" + s + "\"";
            if (o is UnityEngine.Object uo) return uo ? uo.name + " (" + o.GetType().Name + ")" : "null (destroyed)";
            if (o is ItemDrop.ItemData item) return item.m_shared.m_name + " x" + item.m_stack + (item.m_dropPrefab ? " [" + item.m_dropPrefab.name + "]" : "");
            if (o is IFormattable fm) return fm.ToString(null, Inv);
            if (o is IEnumerable en)
            {
                var sb = new StringBuilder();
                int n = 0;
                foreach (var x in en)
                {
                    if (n == 30) { sb.Append("\n  ..."); break; }
                    sb.Append("\n  [").Append(n++).Append("] ").Append(Format(x));
                }
                return o.GetType().Name + " (" + n + (n == 30 ? "+" : "") + ")" + sb;
            }
            return o.ToString();
        }

        static List<string> Tokenize(string s)
        {
            var list = new List<string>();
            var cur = new StringBuilder();
            bool q = false, any = false;
            foreach (char c in s ?? "")
            {
                if (c == '"') { q = !q; any = true; continue; }
                if (c == ' ' && !q) { if (any) list.Add(cur.ToString()); cur.Length = 0; any = false; continue; }
                cur.Append(c); any = true;
            }
            if (any) list.Add(cur.ToString());
            return list;
        }
    }
}


