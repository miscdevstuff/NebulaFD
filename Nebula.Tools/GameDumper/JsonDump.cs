using Nebula.Core;
using Nebula.Core.Utilities;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Reflection;

namespace Nebula.Tools.GameDumper
{
    // Headless-friendly structured dump of whatever NebulaFD parsed.
    // Reads ONLY from NebulaCore.PackageData (already loaded). Uncertain fields
    // are pulled via reflection + try/catch so one bad member never kills the dump.
    // v1: names + effects + named alt-values are the delivered win; deep typed-event
    // parameter decoding is intentionally left as an incremental follow-up.
    public class JsonDump : INebulaTool
    {
        public string Name => "JSON Dump";

        public void Execute()
        {
            var dat = NebulaCore.PackageData;
            var root = new JObject
            {
                ["app_name"]     = JT(() => dat.AppName),
                ["fusion_build"] = JT(() => dat.ProductBuild),
                ["_tool"]        = "JsonDump v1 (NebulaFD headless)",
                ["_note_events"] = "events are type-code + ref + raw params; full typed decoding is follow-up"
            };

            // ---- app-level global NAMES + initial values (the headline CTFAK lacks) ----
            root["global_value_names"]  = JArr(ReflectNames(dat, "GlobalValueNames"));
            root["global_string_names"] = JArr(ReflectNames(dat, "GlobalStringNames"));
            root["global_strings"]      = JArr(ReflectSub(dat, "GlobalStrings", "Strings"));
            root["global_values"]       = JArr(ReflectSub(dat, "GlobalValues", "Values"));

            // ---- extensions + shaders (with parameters) ----
            root["extensions"] = Safe(() =>
            {
                var a = new JArray();
                foreach (var kv in dat.Extensions.Exts)
                    a.Add(new JObject { ["handle"] = kv.Key, ["name"] = kv.Value.Name,
                                        ["file"] = kv.Value.FileName, ["magic"] = kv.Value.MagicNumber });
                return a;
            });
            root["shaders"] = Safe(() =>
            {
                var a = new JArray();
                foreach (var sh in dat.ShaderBank.Shaders.Values)
                {
                    var p = new JArray();
                    if (sh.Parameters != null) foreach (var par in sh.Parameters)
                        p.Add(new JObject { ["name"] = JT(() => par.Name), ["value"] = JT(() => par.Value.ToString()) });
                    a.Add(new JObject { ["handle"] = sh.Handle, ["name"] = JT(() => sh.Name), ["parameters"] = p });
                }
                return a;
            });

            // ---- object DEFINITIONS: name/type/ink/effects + NAMED alt values/strings/flags ----
            root["objects"] = Safe(() =>
            {
                var a = new JArray();
                foreach (var kv in dat.FrameItems.Items)
                {
                    var oi = kv.Value;
                    var props = SafeObj(() => oi.Properties);
                    var o = new JObject
                    {
                        ["handle"] = JT(() => oi.Header.Handle),
                        ["type"]   = JT(() => oi.Header.Type),
                        ["name"]   = JT(() => oi.Name),
                        ["ink"]    = JT(() => oi.Header.InkEffect),
                    };
                    o["effects"] = DumpEffects(oi, props);
                    o["alt_value_names"]  = JArr(ReflectSub(props, "ObjectAlterableValues", "Names"));
                    o["alt_values"]       = JArr(ReflectSub(props, "ObjectAlterableValues", "AlterableValues"));
                    o["alt_string_names"] = JArr(ReflectSub(props, "ObjectAlterableStrings", "Names"));
                    o["alt_flag_names"]   = JArr(ReflectSub(props, "ObjectAlterableFlags", "Names"));
                    a.Add(o);
                }
                return a;
            });

            // ---- frames: name/size + placed instances (handle/pos/layer) + events (v1) ----
            root["frames"] = Safe(() =>
            {
                var a = new JArray();
                foreach (var frame in dat.Frames)
                {
                    var f = new JObject
                    {
                        ["handle"] = JT(() => frame.Handle),
                        ["name"]   = JT(() => frame.FrameName),
                        ["width"]  = JT(() => frame.FrameHeader.Width),
                        ["height"] = JT(() => frame.FrameHeader.Height),
                    };
                    f["instances"] = Safe(() =>
                    {
                        var ia = new JArray();
                        foreach (var ins in frame.FrameInstances.Instances)
                            ia.Add(new JObject { ["x"] = JT(() => ins.PositionX), ["y"] = JT(() => ins.PositionY),
                                                 ["layer"] = JT(() => ins.Layer), ["object_handle"] = JT(() => ins.ObjectInfo) });
                        return ia;
                    });
                    f["events"] = DumpEventsV1(frame);
                    a.Add(f);
                }
                return a;
            });

            string dir = "Dumps\\" + Utilities.ClearName(dat.AppName) + "\\";
            Directory.CreateDirectory(dir);
            string path = dir + "game_model.json";
            File.WriteAllText(path, root.ToString(Formatting.Indented));
            File.WriteAllText("game_model.json", root.ToString(Formatting.Indented)); // top-level copy for easy find
            Console.WriteLine($"[JsonDump] wrote {path}");
        }

        // ---- helpers (defensive: never throw) ----

        /// <summary>Returns a JToken from a lambda; null/exception → JValue.Null.</summary>
        static JToken JT(Func<object?> f)
        {
            try { var v = f(); return v != null ? JToken.FromObject(v) : JValue.CreateNull(); }
            catch { return JValue.CreateNull(); }
        }

        /// <summary>Returns a raw object from a lambda; null/exception → null.</summary>
        static object? SafeObj(Func<object?> f)
        {
            try { return f(); } catch { return null; }
        }

        /// <summary>Returns a JToken from a lambda that already produces JToken; exception → error object.</summary>
        static JToken Safe(Func<JToken> f)
        {
            try { return f(); }
            catch (Exception ex) { return new JObject { ["_error"] = ex.GetType().Name }; }
        }

        static string[] ReflectNames(object? root, string chunkProp)
        {
            try
            {
                if (root == null) return Array.Empty<string>();
                var chunk = root.GetType().GetProperty(chunkProp)?.GetValue(root);
                if (chunk == null) return Array.Empty<string>();
                var names = chunk.GetType().GetProperty("Names")?.GetValue(chunk) as string[];
                return names ?? Array.Empty<string>();
            }
            catch { return Array.Empty<string>(); }
        }

        static object[] ReflectSub(object? props, string subProp, string field)
        {
            try
            {
                if (props == null) return Array.Empty<object>();
                var sub = props.GetType().GetProperty(subProp)?.GetValue(props);
                if (sub == null) return Array.Empty<object>();
                var val = sub.GetType().GetProperty(field)?.GetValue(sub);
                if (val is Array arr) { var l = new List<object>(); foreach (var x in arr) l.Add(x); return l.ToArray(); }
                return Array.Empty<object>();
            }
            catch { return Array.Empty<object>(); }
        }

        static JArray JArr(object[]? src)
        {
            var a = new JArray();
            if (src != null) foreach (var x in src) a.Add(x != null ? JToken.FromObject(x) : JValue.CreateNull());
            return a;
        }
        static JArray JArr(string[]? src)
        {
            var a = new JArray();
            if (src != null) foreach (var x in src) a.Add(x != null ? new JValue(x) : JValue.CreateNull());
            return a;
        }
        static JArray JArr(int[]? src)
        {
            var a = new JArray();
            if (src != null) foreach (var x in src) a.Add(new JValue(x));
            return a;
        }

        static JToken DumpEffects(object oi, object? props)
        {
            var e = new JObject();
            TrySet(e, "ink_param", () =>
            {
                var header = oi.GetType().GetProperty("Header")?.GetValue(oi);
                return header?.GetType().GetProperty("InkEffectParam")?.GetValue(header);
            });
            foreach (var src in new object?[] { oi, props })
            {
                if (src == null) continue;
                var eff = src.GetType().GetProperty("ObjectEffects")?.GetValue(src);
                if (eff != null)
                {
                    e["rgb"]    = JT(() => eff.GetType().GetProperty("RGBCoeff")?.GetValue(eff)?.ToString());
                    e["blend"]  = JT(() => eff.GetType().GetProperty("BlendCoeff")?.GetValue(eff));
                    e["shader"] = JT(() => eff.GetType().GetProperty("HasShader")?.GetValue(eff));
                    break;
                }
            }
            return e;
        }

        static void TrySet(JObject o, string k, Func<object?> f)
        {
            try { var v = f(); if (v != null) o[k] = JToken.FromObject(v); } catch { }
        }

        // v1 event dump: walk whatever the frame exposes; emit type codes + raw bytes.
        // Full typed-parameter decoding (the big follow-up) is intentionally not here.
        static JToken DumpEventsV1(object frame)
        {
            return Safe(() =>
            {
                var a = new JArray();
                var fe = frame.GetType().GetProperty("FrameEvents")?.GetValue(frame);
                if (fe == null) return a;
                foreach (var collName in new[] { "Events", "TopLevelEvents" })
                {
                    var coll = fe.GetType().GetProperty(collName)?.GetValue(fe) as System.Collections.IEnumerable;
                    if (coll == null) continue;
                    foreach (var ev in coll)
                    {
                        var eo = new JObject();
                        foreach (var pn in new[] { "Handle", "EventType", "NumberOfConditions", "NumberOfActions" })
                        {
                            var p = ev.GetType().GetProperty(pn);
                            if (p != null) eo[ToCamel(pn)] = JT(() => p.GetValue(ev));
                        }
                        a.Add(eo);
                    }
                    if (a.Count > 0) break;
                }
                return a;
            });
        }

        static string ToCamel(string s) => char.ToLowerInvariant(s[0]) + s.Substring(1);
    }
}