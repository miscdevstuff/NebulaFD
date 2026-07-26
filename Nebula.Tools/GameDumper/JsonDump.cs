using Nebula.Core;
using Nebula.Core.Memory;
using Nebula.Core.Utilities;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Nebula.Tools.GameDumper
{
    /// <summary>
    /// v3: deep object-graph serializer with corrected leaf handling.
    /// Fixes vs v2: (1) primitives/strings serialize at ANY depth (leaf-order bug),
    /// (2) collection cap raised so big frames' events aren't truncated at 500,
    /// (3) object effects + shader refs captured by deep-dumping the Header
    ///     (effects are flat on Header, not a nested object),
    /// (4) shader bank(s) captured by name-substring discovery (no hardcoded names),
    /// (5) Parent back-reference skipped to prevent bloat,
    /// (6) single reader-tagged output file (no dup, no EXE/MFA clobber).
    /// </summary>
    public class JsonDump : INebulaTool
    {
        public string Name => "JSON Dump";

        const int MaxDepth = 10;
        const int MaxCollectionItems = 1_000_000;

        static readonly HashSet<string> SkipNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "ChunkName", "ChunkID", "ChunkSize", "ChunkData",
            "Log", "Logger",
            "Parent", // back-reference to the owning events/frame: pure nav link, causes bloat/circular noise
        };

        static readonly HashSet<Type> OpaqueTypes = new()
        {
            typeof(System.IO.Stream),
            typeof(System.IO.BinaryReader),
            typeof(System.IO.BinaryWriter),
            typeof(System.Drawing.Bitmap),
            typeof(System.Drawing.Image),
            typeof(System.Drawing.Graphics),
            typeof(ByteReader),
            typeof(ByteWriter),
        };

        readonly ConditionalWeakTable<object, object?> _visited = new();

        public void Execute()
        {
            var dat = NebulaCore.PackageData;
            var root = new JObject
            {
                ["_tool"] = "JsonDump v3 (deep object-graph, leaf-corrected)",
                ["_note"] = "Names for global/alterable values are absent because compiled EXEs " +
                            "(and MFA derived from them) don't store them (editor-only metadata). " +
                            "Effects live flat on each object Header; shaders captured by name discovery.",
                ["app_name"] = dat.AppName,
                ["fusion_build"] = dat.ProductBuild,
            };

            // ---- summary (music/font = 0 is CORRECT here: BGM + TTFs are external files) ----
            root["summary"] = Safe(() => new JObject
            {
                ["frame_count"] = dat.Frames.Count,
                ["object_def_count"] = dat.FrameItems.Items.Count,
                ["image_bank_count"] = dat.ImageBank.Images.Count,
                ["sound_count"] = dat.SoundBank.Sounds.Count,
                ["music_count"] = dat.MusicBank.Music.Count,
                ["font_count"] = dat.TrueTypeFontBank.Fonts.Count,
                ["extension_count"] = dat.Extensions.Exts.Count,
            });

            // ---- extensions ----
            root["extensions"] = SafeArray(() =>
            {
                var a = new JArray();
                foreach (var kv in dat.Extensions.Exts)
                    a.Add(new JObject
                    {
                        ["handle"] = kv.Key,
                        ["name"] = kv.Value.Name,
                        ["file"] = kv.Value.FileName,
                        ["magic"] = kv.Value.MagicNumber,
                    });
                return a;
            });

            // ---- shaders: capture EVERY PackageData member whose name contains "Shader"
            //      (ShaderBank, and any DX9/other bank, by substring -> no hardcoded names) ----
            root["shader_related"] = Safe(() =>
            {
                var node = new JObject();
                foreach (var f in dat.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
                    if (f.Name.IndexOf("Shader", StringComparison.OrdinalIgnoreCase) >= 0)
                        node[f.Name] = DeepDump(f.GetValue(dat), 6);
                foreach (var p in dat.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    if (p.Name.IndexOf("Shader", StringComparison.OrdinalIgnoreCase) >= 0
                        && p.CanRead && p.GetIndexParameters().Length == 0)
                        node[p.Name] = DeepDump(p.GetValue(dat), 6);
                return node;
            });

            // ---- globals: data now survives (leaf fix); names genuinely absent ----
            root["global_values"]  = DeepDump(GetFieldOrProp(dat, "GlobalValues"), 4);
            root["global_strings"] = DeepDump(GetFieldOrProp(dat, "GlobalStrings"), 4);
            root["global_flags"]   = DeepDump(GetFieldOrProp(dat, "GlobalFlags"), 4);

            // ---- object definitions: name + full Header (effects + shader refs) + alterables ----
            root["objects"] = SafeArray(() =>
            {
                var a = new JArray();
                foreach (var kv in dat.FrameItems.Items)
                {
                    var oi = kv.Value;
                    var header = GetFieldOrProp(oi, "Header");
                    var obj = new JObject
                    {
                        ["name"] = oi.Name,
                        ["handle"] = JT(() => GetFieldOrProp(header, "Handle")),
                        ["type"] = JT(() => GetFieldOrProp(header, "Type")),
                        // Header carries InkEffect, InkEffectParam, RGBCoeff, BlendCoeff,
                        // Shader (shared def, with Name) and ShaderParameters (per-object values).
                        ["header"] = DeepDump(header, 8),
                        ["alterable"] = DumpAlterables(oi),
                    };
                    a.Add(obj);
                }
                return a;
            });

            // ---- frames: name/size + instances (deep) + events (deep) + frame-level effects ----
            root["frames"] = SafeArray(() =>
            {
                var a = new JArray();
                foreach (var frame in dat.Frames)
                {
                    var fhdr = GetFieldOrProp(frame, "FrameHeader");
                    var f = new JObject
                    {
                        ["handle"] = JT(() => frame.Handle),
                        ["name"] = frame.FrameName,
                        ["width"] = JT(() => GetFieldOrProp(fhdr, "Width")),
                        ["height"] = JT(() => GetFieldOrProp(fhdr, "Height")),
                    };

                    f["instances"] = SafeArray(() =>
                    {
                        var ia = new JArray();
                        var coll = GetFieldOrProp(GetFieldOrProp(frame, "FrameInstances"), "Instances") as IEnumerable;
                        if (coll != null) foreach (var ins in coll) ia.Add(DeepDump(ins, 4));
                        return ia;
                    });

                    f["events"] = DumpFrameEvents(frame);
                    f["frame_effects"] = DeepDump(GetFieldOrProp(frame, "FrameEffects"), 6);
                    f["layers"] = DeepDump(GetFieldOrProp(frame, "Layers"), 6);
                    f["frame_shader_settings"] = DeepDump(GetFieldOrProp(frame, "FrameShaderSettings"), 6);
                    a.Add(f);
                }
                return a;
            });

            // ---- write output: ONCE, named by reader so EXE and MFA reads don't clobber ----
            string readerTag = (NebulaCore.CurrentReader?.Name ?? "unknown").Replace(' ', '_');
            string dir = "Dumps\\" + Utilities.ClearName(dat.AppName) + "\\";
            Directory.CreateDirectory(dir);
            string path = dir + "game_model_" + readerTag + ".json";
            string json = root.ToString(Formatting.Indented);
            File.WriteAllText(path, json);
            Console.WriteLine($"[JsonDump] wrote {path} ({json.Length / 1024} KB)");
        }

        // ================================================================
        //  DEEP DUMP  (leaf-corrected: primitives/strings survive any depth)
        // ================================================================
        JToken DeepDump(object? obj, int depth)
        {
            if (obj == null) return JValue.CreateNull();
            var type = obj.GetType();

            // --- leaves FIRST, regardless of depth ---
            var underlying = Nullable.GetUnderlyingType(type);
            if (underlying != null)
                return JToken.FromObject(obj);
            if (type.IsPrimitive || type == typeof(string) || type == typeof(decimal))
                return JToken.FromObject(obj);
            if (type.IsEnum)
                return new JValue(obj.ToString());
            if (OpaqueTypes.Contains(type) || OpaqueTypes.Any(t => t.IsAssignableFrom(type)))
                return new JValue($"<{type.Name}>");

            // --- circular guard (reference types only) ---
            if (!type.IsValueType)
            {
                if (_visited.TryGetValue(obj, out _))
                    return new JValue($"<circular: {type.Name}>");
                _visited.AddOrUpdate(obj, null);
            }

            // --- depth cap applies ONLY to complex types now ---
            if (depth <= 0)
                return new JValue($"<max depth: {type.Name}>");

            if (type.IsArray)
            {
                var arr = (Array)obj;
                var ja = new JArray();
                int count = Math.Min(arr.Length, MaxCollectionItems);
                for (int i = 0; i < count; i++) ja.Add(DeepDump(arr.GetValue(i), depth - 1));
                if (arr.Length > MaxCollectionItems) ja.Add(new JValue($"<... {arr.Length - MaxCollectionItems} more>"));
                return ja;
            }
            if (obj is IDictionary dict)
            {
                var jo = new JObject();
                int count = 0;
                foreach (DictionaryEntry entry in dict)
                {
                    if (count++ >= MaxCollectionItems) { jo["_truncated"] = $"<{dict.Count - MaxCollectionItems} more>"; break; }
                    string key = (entry.Key?.ToString() ?? "null").Replace(".", "_").Replace("/", "_");
                    jo[key] = DeepDump(entry.Value, depth - 1);
                }
                return jo;
            }
            if (obj is IEnumerable enumerable && type != typeof(string))
            {
                var ja = new JArray();
                int count = 0;
                foreach (var item in enumerable)
                {
                    if (count++ >= MaxCollectionItems) { ja.Add(new JValue("<... truncated>")); break; }
                    ja.Add(DeepDump(item, depth - 1));
                }
                return ja;
            }

            // --- complex object: public fields + properties ---
            var result = new JObject { ["_type"] = type.Name };
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (SkipNames.Contains(field.Name)) continue;
                if (field.FieldType == typeof(ByteReader) || field.FieldType == typeof(ByteWriter)) continue;
                try { result[field.Name] = DeepDump(field.GetValue(obj), depth - 1); }
                catch { result[field.Name] = new JValue("<error>"); }
            }
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (SkipNames.Contains(prop.Name)) continue;
                if (prop.GetIndexParameters().Length > 0 || !prop.CanRead) continue;
                if (type.GetField(prop.Name, BindingFlags.Public | BindingFlags.Instance) != null) continue;
                try { result[prop.Name] = DeepDump(prop.GetValue(obj), depth - 1); }
                catch { result[prop.Name] = new JValue("<error>"); }
            }
            return result;
        }

        // ================================================================
        JToken DumpFrameEvents(object frame)
        {
            object? events = GetFieldOrProp(frame, "Events")
                          ?? GetFieldOrProp(frame, "FrameEvents")
                          ?? GetFieldOrProp(frame, "events");
            if (events == null) return new JArray();
            foreach (var name in new[] { "Events", "EventList", "Items", "TopLevelEvents", "EventGroups" })
            {
                var coll = GetFieldOrProp(events, name);
                if (coll is IEnumerable enumerable && coll is not string)
                {
                    var a = new JArray();
                    int count = 0;
                    foreach (var ev in enumerable)
                    {
                        if (count++ >= MaxCollectionItems) break;
                        a.Add(DeepDump(ev, MaxDepth));
                    }
                    return a;
                }
            }
            return DeepDump(events, MaxDepth);
        }

        JToken DumpAlterables(object oi)
        {
            var alt = new JObject();
            var props = GetFieldOrProp(oi, "Properties") ?? oi;
            foreach (var (jsonKey, typeNames) in new[] {
                ("values",  new[] { "ObjectAlterableValues", "AlterableValues", "AltValues" }),
                ("strings", new[] { "ObjectAlterableStrings", "AlterableStrings", "AltStrings" }),
                ("flags",   new[] { "ObjectAlterableFlags", "AlterableFlags", "AltFlags" }),
            })
            {
                object? found = null;
                foreach (var n in typeNames) { found = GetFieldOrProp(props, n); if (found != null) break; }
                alt[jsonKey] = found != null ? DeepDump(found, 4) : new JArray();
            }
            return alt;
        }

        // ================================================================
        static object? GetFieldOrProp(object? obj, string name)
        {
            if (obj == null) return null;
            var type = obj.GetType();
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (field != null) return field.GetValue(obj);
            var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (prop != null && prop.CanRead && prop.GetIndexParameters().Length == 0)
                return prop.GetValue(obj);
            return null;
        }

        static JToken JT(Func<object?> f)
        {
            try { var v = f(); return v != null ? JToken.FromObject(v) : JValue.CreateNull(); }
            catch { return JValue.CreateNull(); }
        }

        static JToken SafeArray(Func<JArray> f)
        {
            try { return f(); }
            catch (Exception ex) { return new JObject { ["_error"] = ex.GetType().Name + ": " + ex.Message }; }
        }

        static JToken Safe(Func<JToken> f)
        {
            try { return f(); }
            catch (Exception ex) { return new JObject { ["_error"] = ex.GetType().Name + ": " + ex.Message }; }
        }
    }
}