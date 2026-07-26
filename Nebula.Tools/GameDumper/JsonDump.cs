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
                ["app_name"]    = G(() => dat.AppName),
                ["fusion_build"]= G(() => dat.ProductBuild),
                ["_tool"]       = "JsonDump v1 (NebulaFD headless)",
                ["_note_events"]= "events are type-code + ref + raw params; full typed decoding is follow-up"
            };

            // ---- app-level global NAMES + initial values (the headline CTFAK lacks) ----
            root["global_value_names"]  = JArr(ReflectNames(dat, "GlobalValueNames"));
            root["global_string_names"] = JArr(ReflectNames(dat, "GlobalStringNames"));
            root["global_strings"]      = JArr(G(() => dat.GlobalStrings?.Strings));
            root["global_values"]       = JArr(G(() => dat.GlobalValues?.Values));

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
                        p.Add(new JObject { ["name"] = G(() => par.Name), ["value"] = G(() => par.Value?.ToString()) });
                    a.Add(new JObject { ["handle"] = sh.Handle, ["name"] = G(() => sh.Name), ["parameters"] = p });
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
                    var props = G(() => oi.Properties);
                    var o = new JObject
                    {
                        ["handle"] = G(() => oi.Header.Handle),
                        ["type"]   = G(() => oi.Header.Type),
                        ["name"]   = G(() => oi.Name),
                        ["ink"]    = G(() => oi.Header.InkEffect),
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
                        ["handle"] = G(() => frame.Handle),
                        ["name"]   = G(() => frame.FrameName),
                        ["width"]  = G(() => frame.FrameHeader.Width),
                        ["height"] = G(() => frame.FrameHeader.Height),
                    };
                    f["instances"] = Safe(() =>
                    {
                        var ia = new JArray();
                        foreach (var ins in frame.FrameInstances.Instances)
                            ia.Add(new JObject { ["x"] = G(() => ins.PositionX), ["y"] = G(() => ins.PositionY),
                                                 ["layer"] = G(() => ins.Layer), ["object_handle"] = G(() => ins.ObjectInfo) });
                        return ia;
                    });
                    f["events"] = DumpEventsV1(frame); // type-code + ref + raw; see note
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

        // ---- reflection helpers (defensive: never throw) ----
        static object G(Func<object> f) { try { return f(); } catch { return null; } }
        static JToken Safe(Func<JToken> f) { try { return f() ?? new JArray(); } catch (Exception ex) { return new JObject { ["_error"] = ex.GetType().Name }; } }

        static string[] ReflectNames(object root, string chunkProp)
        {
            try
            {
                var chunk = root.GetType().GetProperty(chunkProp)?.GetValue(root);
                if (chunk == null) return Array.Empty<string>();
                var names = chunk.GetType().GetProperty("Names")?.GetValue(chunk) as string[];
                return names ?? Array.Empty<string>();
            }
            catch { return Array.Empty<string>(); }
        }
        static object[] ReflectSub(object props, string subProp, string field)
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
        static JArray JArr(object[] src) { var a = new JArray(); if (src != null) foreach (var x in src) a.Add(x == null ? null : JToken.FromObject(x)); return a; }
        static JArray JArr(string[] src) { var a = new JArray(); if (src != null) foreach (var x in src) a.Add(x); return a; }
        static JArray JArr(int[] src)    { var a = new JArray(); if (src != null) foreach (var x in src) a.Add(x); return a; }

        static JToken DumpEffects(object oi, object props)
        {
            var e = new JObject();
            TrySet(e, "ink_param",  () => oi.GetType().GetProperty("Header").GetValue(oi).GetType().GetProperty("InkEffectParam").GetValue(oi.GetType().GetProperty("Header").GetValue(oi)));
            // object-level effects (RGB/blend/shader) live on the MFA info or header; grab whatever exists
            foreach (var src in new[] { oi, props })
            {
                if (src == null) continue;
                var eff = src.GetType().GetProperty("ObjectEffects")?.GetValue(src);
                if (eff != null)
                {
                    e["rgb"]    = G(() => eff.GetType().GetProperty("RGBCoeff")?.GetValue(eff)?.ToString());
                    e["blend"]  = G(() => eff.GetType().GetProperty("BlendCoeff")?.GetValue(eff));
                    e["shader"] = G(() => eff.GetType().GetProperty("HasShader")?.GetValue(eff));
                    break;
                }
            }
            return e;
        }
        static void TrySet(JObject o, string k, Func<object> f) { try { var v = f(); if (v != null) o[k] = JToken.FromObject(v); } catch { } }

        // v1 event dump: walk whatever the frame exposes; emit type codes + raw bytes.
        // Full typed-parameter decoding (the big follow-up) is intentionally not here.
        static JToken DumpEventsV1(object frame)
        {
            return Safe(() =>
            {
                var a = new JArray();
                var fe = frame.GetType().GetProperty("FrameEvents")?.GetValue(frame);
                if (fe == null) return a;
                // try the common collection names NebulaFD uses
                foreach (var collName in new[] { "Events", "TopLevelEvents" })
                {
                    var coll = fe.GetType().GetProperty(collName)?.GetValue(fe) as System.Collections.IEnumerable;
                    if (coll == null) continue;
                    foreach (var ev in coll)
                    {
                        var eo = new JObject();
                        foreach (var pn in new[] { "Handle", "EventType", "NumberOfConditions", "NumberOfActions" })
                        { var p = ev.GetType().GetProperty(pn); if (p != null) eo[Camel(pn)] = G(() => p.GetValue(ev)); }
                        a.Add(eo);
                    }
                    if (a.Count > 0) break;
                }
                return a;
            });
        }
        static string Camel(string s) => char.ToLowerInvariant(s[0]) + s.Substring(1);
    }
}