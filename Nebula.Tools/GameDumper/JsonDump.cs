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
    /// Recursively serializes NebulaFD's in-memory PackageData model to JSON.
    /// Uses deep object-graph walking (not hardcoded property names) so it
    /// cannot miss data due to naming mismatches. Filters known-internal
    /// Chunk base-class fields to reduce noise.
    /// </summary>
    public class JsonDump : INebulaTool
    {
        public string Name => "JSON Dump";

        // ---- configuration ----
        const int MaxDepth = 5;
        const int MaxCollectionItems = 500;

        // Fields/properties to skip (Chunk base-class bookkeeping + stream objects)
        static readonly HashSet<string> SkipNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "ChunkName", "ChunkID", "ChunkSize", "ChunkData",
            "Log", "Logger",
        };

        // Types to never recurse into
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

        // Circular-reference guard
        readonly ConditionalWeakTable<object, object?> _visited = new();

        public void Execute()
        {
            var dat = NebulaCore.PackageData;
            var root = new JObject
            {
                ["_tool"] = "JsonDump v2 (deep object-graph serializer)",
                ["_note"] = "Recursively serialized from NebulaFD in-memory model. " +
                            "All public fields/properties are included. " +
                            "Global value/string names and alt-value names are empty " +
                            "because they don't exist in compiled EXEs (editor-only metadata).",
                ["app_name"] = dat.AppName,
                ["fusion_build"] = dat.ProductBuild,
            };

            // ---- top-level summary (quick-reference counts) ----
            root["summary"] = new JObject
            {
                ["frame_count"] = dat.Frames.Count,
                ["object_count"] = dat.FrameItems.Items.Count,
                ["image_count"] = dat.ImageBank.Images.Count,
                ["sound_count"] = dat.SoundBank.Sounds.Count,
                ["music_count"] = dat.MusicBank.Music.Count,
                ["font_count"] = dat.TrueTypeFontBank.Fonts.Count,
                ["shader_count"] = CountShaders(dat),
                ["extension_count"] = dat.Extensions.Exts.Count,
                ["packed_data_count"] = dat.PackData.Items.Length,
                ["binary_file_count"] = dat.BinaryFiles.Items.Count,
            };

            // ---- extensions (small, always useful) ----
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

            // ---- shaders (BOTH banks) ----
            root["shaders"] = SafeArray(() =>
            {
                var a = new JArray();
                DumpShaderBank(dat.ShaderBank, a, "ShaderBank");
                DumpDX9ShaderBank(dat, a);
                return a;
            });

            // ---- global values/strings (will be empty for EXE reads — that's correct) ----
            root["global_values"] = DeepDump(GetFieldOrProp(dat, "GlobalValues"), 2);
            root["global_strings"] = DeepDump(GetFieldOrProp(dat, "GlobalStrings"), 2);
            root["global_flags"] = DeepDump(GetFieldOrProp(dat, "GlobalFlags"), 2);

            // ---- objects (definitions: name, type, effects, alt-values) ----
            root["objects"] = SafeArray(() =>
            {
                var a = new JArray();
                foreach (var kv in dat.FrameItems.Items)
                {
                    var oi = kv.Value;
                    var obj = new JObject
                    {
                        ["handle"] = JT(() => GetFieldOrProp(GetFieldOrProp(oi, "Header"), "Handle")),
                        ["type"] = JT(() => GetFieldOrProp(GetFieldOrProp(oi, "Header"), "Type")),
                        ["name"] = oi.Name,
                        ["ink_effect"] = JT(() => GetFieldOrProp(GetFieldOrProp(oi, "Header"), "InkEffect")),
                        ["ink_param"] = JT(() => GetFieldOrProp(GetFieldOrProp(oi, "Header"), "InkEffectParam")),
                    };

                    // Object-level effects: try multiple known locations
                    obj["effects"] = DumpObjectEffects(oi);

                    // Alterable values/strings/flags: try multiple known locations
                    obj["alterable"] = DumpAlterables(oi);

                    a.Add(obj);
                }
                return a;
            });

            // ---- frames (name, size, instances, events — deep-dumped) ----
            root["frames"] = SafeArray(() =>
            {
                var a = new JArray();
                foreach (var frame in dat.Frames)
                {
                    var f = new JObject
                    {
                        ["handle"] = JT(() => frame.Handle),
                        ["name"] = frame.FrameName,
                        ["width"] = JT(() => GetFieldOrProp(frame, "FrameHeader") != null
                            ? GetFieldOrProp(GetFieldOrProp(frame, "FrameHeader"), "Width") : null),
                        ["height"] = JT(() => GetFieldOrProp(frame, "FrameHeader") != null
                            ? GetFieldOrProp(GetFieldOrProp(frame, "FrameHeader"), "Height") : null),
                    };

                    // Instances (placed objects in this frame)
                    f["instances"] = SafeArray(() =>
                    {
                        var ia = new JArray();
                        var instances = GetFieldOrProp(frame, "FrameInstances");
                        if (instances == null) return ia;
                        var coll = GetFieldOrProp(instances, "Instances") as IEnumerable;
                        if (coll == null) return ia;
                        foreach (var ins in coll)
                        {
                            if (ins == null) continue;
                            ia.Add(new JObject
                            {
                                ["x"] = JT(() => GetFieldOrProp(ins, "PositionX")),
                                ["y"] = JT(() => GetFieldOrProp(ins, "PositionY")),
                                ["layer"] = JT(() => GetFieldOrProp(ins, "Layer")),
                                ["object_handle"] = JT(() => GetFieldOrProp(ins, "ObjectInfo")),
                            });
                        }
                        return ia;
                    });

                    // Events: deep-dump the entire events object graph
                    f["events"] = DumpFrameEvents(frame);

                    // Frame effects, layers, shaders — deep-dump whatever exists
                    f["frame_effects"] = DeepDump(GetFieldOrProp(frame, "FrameEffects"), 3);
                    f["layers"] = DeepDump(GetFieldOrProp(frame, "Layers"), 3);
                    f["frame_shader_settings"] = DeepDump(GetFieldOrProp(frame, "FrameShaderSettings"), 3);

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
        //  DEEP DUMP — recursive object-graph serializer
        // ================================================================

        JToken DeepDump(object? obj, int depth)
        {
            if (obj == null) return JValue.CreateNull();
            if (depth <= 0) return new JValue($"<max depth: {obj.GetType().Name}>");

            var type = obj.GetType();

            // Primitives and strings
            if (type.IsPrimitive || type == typeof(string) || type == typeof(decimal))
                return JToken.FromObject(obj);

            // Enums
            if (type.IsEnum)
                return new JValue(obj.ToString());

            // Opaque types (Bitmap, Stream, ByteReader, etc.)
            if (OpaqueTypes.Contains(type) || OpaqueTypes.Any(t => t.IsAssignableFrom(type)))
                return new JValue($"<{type.Name}>");

            // Circular reference check
            if (!type.IsValueType)
            {
                if (_visited.TryGetValue(obj, out _))
                    return new JValue($"<circular: {type.Name}>");
                _visited.AddOrUpdate(obj, null);
            }

            // Arrays
            if (type.IsArray)
            {
                var arr = (Array)obj;
                var ja = new JArray();
                int count = Math.Min(arr.Length, MaxCollectionItems);
                for (int i = 0; i < count; i++)
                    ja.Add(DeepDump(arr.GetValue(i), depth - 1));
                if (arr.Length > MaxCollectionItems)
                    ja.Add(new JValue($"<... {arr.Length - MaxCollectionItems} more>"));
                return ja;
            }

            // Dictionaries
            if (obj is IDictionary dict)
            {
                var jo = new JObject();
                int count = 0;
                foreach (DictionaryEntry entry in dict)
                {
                    if (count++ >= MaxCollectionItems)
                    {
                        jo["_truncated"] = $"<{dict.Count - MaxCollectionItems} more>";
                        break;
                    }
                    string key = entry.Key?.ToString() ?? "null";
                    key = key.Replace(".", "_").Replace("/", "_");
                    jo[key] = DeepDump(entry.Value, depth - 1);
                }
                return jo;
            }

            // IEnumerable (List<T>, etc.) but not string
            if (obj is IEnumerable enumerable && type != typeof(string))
            {
                var ja = new JArray();
                int count = 0;
                foreach (var item in enumerable)
                {
                    if (count++ >= MaxCollectionItems)
                    {
                        ja.Add(new JValue($"<... truncated>"));
                        break;
                    }
                    ja.Add(DeepDump(item, depth - 1));
                }
                return ja;
            }

            // Complex object: dump all public fields + properties
            var result = new JObject();
            result["_type"] = type.Name;

            // Fields
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (SkipNames.Contains(field.Name)) continue;
                if (field.FieldType == typeof(ByteReader) || field.FieldType == typeof(ByteWriter)) continue;
                try { result[field.Name] = DeepDump(field.GetValue(obj), depth - 1); }
                catch { result[field.Name] = new JValue("<error>"); }
            }

            // Properties (skip indexers and write-only)
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (SkipNames.Contains(prop.Name)) continue;
                if (prop.GetIndexParameters().Length > 0) continue;
                if (!prop.CanRead) continue;
                if (type.GetField(prop.Name, BindingFlags.Public | BindingFlags.Instance) != null) continue;
                try { result[prop.Name] = DeepDump(prop.GetValue(obj), depth - 1); }
                catch { result[prop.Name] = new JValue("<error>"); }
            }

            return result;
        }

        // ================================================================
        //  SPECIALIZED DUMPERS (for known structures)
        // ================================================================

        JToken DumpFrameEvents(object frame)
        {
            object? events = GetFieldOrProp(frame, "Events")
                          ?? GetFieldOrProp(frame, "FrameEvents")
                          ?? GetFieldOrProp(frame, "events");

            if (events == null) return new JArray();

            // Try known collection names inside the events object
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

            // Fallback: deep-dump the entire events object
            return DeepDump(events, MaxDepth);
        }

        JToken DumpObjectEffects(object oi)
        {
            var e = new JObject();

            // EXE-read: ink effect is on Header directly — use reflection since oi is object
            var header = GetFieldOrProp(oi, "Header");
            e["ink_effect"] = JT(() => GetFieldOrProp(header, "InkEffect"));
            e["ink_param"] = JT(() => GetFieldOrProp(header, "InkEffectParam"));

            // MFA-read: MFAObjectEffects chunk stored on the object
            foreach (var loc in new[] { oi, GetFieldOrProp(oi, "Properties") })
            {
                if (loc == null) continue;
                var eff = GetFieldOrProp(loc, "ObjectEffects")
                       ?? GetFieldOrProp(loc, "Effects")
                       ?? GetFieldOrProp(loc, "MFAObjectEffects");
                if (eff != null)
                {
                    e["mfa_effects"] = DeepDump(eff, 3);
                    break;
                }
            }

            return e;
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
                foreach (var name in typeNames)
                {
                    found = GetFieldOrProp(props, name);
                    if (found != null) break;
                }
                alt[jsonKey] = found != null ? DeepDump(found, 3) : new JArray();
            }

            return alt;
        }

        void DumpShaderBank(object? bank, JArray target, string source)
        {
            if (bank == null) return;
            var shaders = GetFieldOrProp(bank, "Shaders");
            if (shaders is IDictionary dict)
            {
                foreach (DictionaryEntry entry in dict)
                {
                    var sh = entry.Value;
                    if (sh == null) continue;
                    var s = new JObject
                    {
                        ["_source"] = source,
                        ["handle"] = entry.Key?.ToString(),
                        ["name"] = GetFieldOrProp(sh, "Name")?.ToString(),
                    };
                    var parms = GetFieldOrProp(sh, "Parameters");
                    if (parms is IEnumerable pEnum && parms is not string)
                    {
                        var pa = new JArray();
                        foreach (var par in pEnum)
                        {
                            if (par == null) continue;
                            pa.Add(new JObject
                            {
                                ["name"] = GetFieldOrProp(par, "Name")?.ToString(),
                                ["value"] = GetFieldOrProp(par, "Value")?.ToString(),
                                ["type"] = GetFieldOrProp(par, "Type")?.ToString(),
                            });
                        }
                        s["parameters"] = pa;
                    }
                    target.Add(s);
                }
            }
        }

        void DumpDX9ShaderBank(object dat, JArray target)
        {
            var dx9 = GetFieldOrProp(dat, "DX9ShaderBank");
            if (dx9 != null)
                DumpShaderBank(dx9, target, "DX9ShaderBank");
        }

        int CountShaders(object dat)
        {
            int count = 0;
            var sb = GetFieldOrProp(dat, "ShaderBank");
            if (sb != null)
            {
                var shaders = GetFieldOrProp(sb, "Shaders");
                if (shaders is IDictionary d) count += d.Count;
            }
            var dx9 = GetFieldOrProp(dat, "DX9ShaderBank");
            if (dx9 != null)
            {
                var shaders = GetFieldOrProp(dx9, "Shaders");
                if (shaders is IDictionary d) count += d.Count;
            }
            return count;
        }

        // ================================================================
        //  HELPERS
        // ================================================================

        /// <summary>Try field first, then property. Returns null if neither exists.</summary>
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

        /// <summary>Returns a JToken from a lambda; null/exception → JValue.Null.</summary>
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
    }
}