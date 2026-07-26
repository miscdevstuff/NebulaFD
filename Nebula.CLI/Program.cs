using Nebula.Core.FileReaders;
using Nebula.Core.Memory;
using Nebula.Core.Utilities;
using Spectre.Console;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime;
using System.Text;

namespace Nebula
{
    internal class Program
    {
        static Stopwatch readStopwatch = new Stopwatch();
        static ByteReader? fileReader;

        // ---- headless automation state (only used when args are present) ----
        static bool   Headless;
        static string? ArgPath;
        static string? ForceType;     // exe | ccn | mfa | apk | anm | agmi | zip | ipa
        static string? ToolName;      // exact INebulaTool.Name, or null
        static bool   ListTools;
        //static bool   CloseOnFinish;

        static async Task Main(string[] args)
        {
            var processModule = Process.GetCurrentProcess().MainModule;
            if (processModule != null)
            {
                var pathToExe = processModule.FileName;
                var pathToContentRoot = Path.GetDirectoryName(pathToExe);
                Directory.SetCurrentDirectory(pathToContentRoot!);
            }

            NebulaCore.Init();
            Logger.Save();
            Console.InputEncoding = Encoding.UTF8;
            Console.OutputEncoding = Encoding.UTF8;

            ParseArgs(args);
            if (Headless) HeadlessMain();   // no AnsiConsole cosmetic calls -> safe under redirection/CI
            else          SpectreMain();    // original interactive TUI, unchanged
        }

        // ------------------------------------------------------------------
        //  HEADLESS PATH  (-path -forcetype -tool -listtools) // -closeonfinish
        // ------------------------------------------------------------------
        static void ParseArgs(string[] args)
        {
            Headless = args.Any(a => a.StartsWith("-"));
            for (int i = 0; i < args.Length; i++)
            {
                string Next() => (i + 1 < args.Length) ? args[++i] : "";
                switch (args[i].ToLowerInvariant())
                {
                    case "-path":          ArgPath = Next(); break;
                    case "-forcetype":     ForceType = Next().ToLowerInvariant(); break;
                    case "-tool":          ToolName = Next(); break;
                    case "-listtools":     ListTools = true; break;
                    //case "-closeonfinish": CloseOnFinish = true; break;
                }
            }
        }

        static void HeadlessMain()
        {
            // 1) file path
            if (string.IsNullOrWhiteSpace(ArgPath) || !File.Exists(ArgPath))
            { Console.Error.WriteLine("ERROR: -path missing or file not found: " + (ArgPath ?? "(null)")); Environment.Exit(2); }
            NebulaCore.FilePath = ArgPath;

            // 2) reader: explicit token, else extension auto-detect (same classes the TUI uses)
            if (!string.IsNullOrWhiteSpace(ForceType)) SetReaderFromToken(ForceType);
            else                                       AutoDetectReader();
            if (NebulaCore.CurrentReader == null)
            { Console.Error.WriteLine("ERROR: no reader for -forcetype '" + ForceType + "'"); Environment.Exit(3); }

            // 3) read package DIRECTLY (no AnsiConsole.Status spinner -> no console handle needed)
            readStopwatch.Restart();
            Console.WriteLine($"[headless] reading with \"{NebulaCore.CurrentReader.Name}\": {ArgPath}");
            fileReader = new ByteReader(NebulaCore.FilePath, FileMode.Open);
            NebulaCore.CurrentReader.LoadGame(fileReader!, NebulaCore.FilePath);
            fileReader!.Dispose();
            readStopwatch.Stop();
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(); GC.WaitForPendingFinalizers();
            Console.WriteLine($"[headless] read finished in {readStopwatch.Elapsed.TotalSeconds:F2}s");

            // 4) discover or run a tool
            var tools = BuildTools();
            if (ListTools)
            {
                Console.WriteLine("[headless] available tools:");
                foreach (var t in tools) Console.WriteLine("  - " + t.Name);
                Environment.Exit(0);
            }
            if (!string.IsNullOrWhiteSpace(ToolName))
            {
                var tool = tools.FirstOrDefault(t => t.Name == ToolName);
                if (tool == null)
                { Console.Error.WriteLine("ERROR: no tool named '" + ToolName + "'. Use -listtools."); Environment.Exit(4); }
                Console.WriteLine($"[headless] executing tool: {tool.Name}");
                var sw = Stopwatch.StartNew();
                try { tool.Execute(); }
                catch (Exception ex) { Console.WriteLine($"[headless] TOOL '{tool.Name}' THREW:\n{ex}"); }
                sw.Stop();
                Console.WriteLine($"[headless] tool '{tool.Name}' finished in {sw.Elapsed.TotalSeconds:F2}s");
            }
            else
            {
                Console.WriteLine("[headless] no -tool given (read-only pass).");
            }
            Environment.Exit(0); // headless always exits; never falls into the interactive menu
        }

        static void SetReaderFromToken(string tok)
        {
            NebulaCore.CurrentReader = tok switch
            {
                "exe"  => new EXEFileReader(),
                "ccn"  => new CCNFileReader(),
                "mfa"  => new MFAFileReader(),
                "apk"  => new APKFileReader(),
                "anm"  => new ANMFileReader(),
                "agmi" or "tmp" => new AGMIFileReader(),
                "zip"  or "open"=> new OpenFileReader(),
                "ipa"  => new IPAFileReader(),
                _ => null!
            };
        }

        // same reflection the TUI's SelectTool uses, so -tool/-listtools see the identical set
        static List<INebulaTool> BuildTools()
        {
            var tools = new List<INebulaTool>();
            Directory.CreateDirectory("Tools");
            foreach (var item in Directory.GetFiles("Tools", "*.dll"))
            {
                var asm = Assembly.LoadFrom(Path.GetFullPath(item));
                foreach (var tt in asm.GetTypes())
                    if (tt.GetInterface(typeof(INebulaTool).FullName) != null)
                        tools.Add((INebulaTool)Activator.CreateInstance(tt)!);
            }
            return tools;
        }

        // ------------------------------------------------------------------
        //  ORIGINAL INTERACTIVE TUI  (unchanged — double-click still works)
        // ------------------------------------------------------------------
        static void SpectreMain()
        {
            WaitForFile();
            DebugDumper.Clean();
            SelectReader();
            ReadPackage();
            SelectTool();
        }

        static void WaitForFile()
        {
            AnsiConsole.Clear();
            AnsiConsole.Write(NebulaCore.ConsoleFiglet);
            AnsiConsole.Write(NebulaCore.ConsoleRule);
            AnsiConsole.MarkupLine($"[{NebulaCore.ColorRules[1]}]File Path:[/]");
            string path = Console.ReadLine().Trim().Trim('"');
            if (File.Exists(path)) NebulaCore.FilePath = path;
            else WaitForFile();
        }

        static void SelectReader()
        {
            AnsiConsole.Clear();
            AnsiConsole.Write(NebulaCore.ConsoleFiglet);
            AnsiConsole.Write(NebulaCore.ConsoleRule);
            var fileReaders = from t in Assembly.GetAssembly(typeof(NebulaCore)).GetTypes()
                              where t.GetInterfaces().Contains(typeof(IFileReader))
                                 && t.GetConstructor(Type.EmptyTypes) != null
                              select Activator.CreateInstance(t) as IFileReader;
            List<string> fileReaderNames = new() { $"[{NebulaCore.ColorRules[3]}]Auto-Detect[/]" };
            foreach (IFileReader fileReader in fileReaders)
                fileReaderNames.Add($"[{NebulaCore.ColorRules[3]}]{fileReader.Name}[/]");
            string? selectedReader = AnsiConsole.Prompt(new SelectionPrompt<string>()
                .Title($"[{NebulaCore.ColorRules[1]}]Select a file-reader.[/]")
                .AddChoices(fileReaderNames)
                .HighlightStyle(NebulaCore.ColorRules[4]));
            NebulaCore.CurrentReader = null;
            foreach (IFileReader fileReader in fileReaders)
                if ($"[{NebulaCore.ColorRules[3]}]{fileReader.Name}[/]" == selectedReader)
                { NebulaCore.CurrentReader = fileReader; break; }
            AnsiConsole.Clear();
            AnsiConsole.Write(NebulaCore.ConsoleFiglet);
            AnsiConsole.Write(NebulaCore.ConsoleRule);
            AnsiConsole.Status().Spinner(Spinner.Known.Dots2).Start("Loading file", ctx =>
            { fileReader = new ByteReader(NebulaCore.FilePath, FileMode.Open); });
            if (NebulaCore.CurrentReader == null) AutoDetectReader();
        }

        static void AutoDetectReader()
        {
            string ext = Path.GetExtension(NebulaCore.FilePath).ToLower();
            switch (ext)
            {
                default:
                    NebulaCore.CurrentReader = new CCNFileReader();
                    ((CCNFileReader)NebulaCore.CurrentReader).CheckUnpacked(fileReader!);
                    break;
                case ".exe": NebulaCore.CurrentReader = new EXEFileReader(); break;
                case ".mfa": NebulaCore.CurrentReader = new MFAFileReader(); break;
                case ".anm": NebulaCore.CurrentReader = new ANMFileReader(); break;
                case ".tmp": NebulaCore.CurrentReader = new AGMIFileReader(); break;
                case ".zip": NebulaCore.CurrentReader = new OpenFileReader(); break;
                case ".apk": NebulaCore.CurrentReader = new APKFileReader(); break;
                case ".ipa": NebulaCore.CurrentReader = new IPAFileReader(); break;
                case ".gam":
                    if (File.Exists(Path.Combine(Path.GetDirectoryName(NebulaCore.FilePath), Path.GetFileNameWithoutExtension(NebulaCore.FilePath) + ".img")))
                        NebulaCore.CurrentReader = new KNPFileReader();
                    else NebulaCore.CurrentReader = new CCNFileReader();
                    break;
            }
        }

        static void ReadPackage()
        {
            AnsiConsole.Clear();
            AnsiConsole.Write(NebulaCore.ConsoleFiglet);
            AnsiConsole.Write(NebulaCore.ConsoleRule);
            readStopwatch.Restart();
            AnsiConsole.MarkupLine($"[{NebulaCore.ColorRules[1]}]Reading game as \"{NebulaCore.CurrentReader.Name}\"[/]");
#if !DEBUG
            try {
#endif
                NebulaCore.CurrentReader.LoadGame(fileReader!, NebulaCore.FilePath);
                fileReader!.Dispose();
#if !DEBUG
            } catch (Exception ex) {
                Logger.Log(NebulaCore.CurrentReader.GetType(), "ERROR: \n" + ex.Message + '\n' + ex.StackTrace);
                Logger.Save(); throw;
            }
#endif
            Logger.Save();
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(); GC.WaitForPendingFinalizers();
            readStopwatch.Stop();
        }

        static Stopwatch? toolStopwatch = null;
        static void SelectTool()
        {
            AnsiConsole.Clear();
            AnsiConsole.Write(NebulaCore.ConsoleFiglet);
            AnsiConsole.Write(NebulaCore.ConsoleRule);
            if (toolStopwatch == null)
                AnsiConsole.MarkupLine($"[{NebulaCore.ColorRules[1]}]Reading finished in {readStopwatch.Elapsed.TotalSeconds} seconds[/]");
            else
                AnsiConsole.MarkupLine($"[{NebulaCore.ColorRules[1]}]Tool(s) finished in {toolStopwatch.Elapsed.TotalSeconds} seconds[/]");
            List<INebulaTool> tools = new();
            List<string> toolNames = new() { $"[{NebulaCore.ColorRules[3]}]Quit[/]" };
            Directory.CreateDirectory("Tools");
            foreach (var item in Directory.GetFiles("Tools", "*.dll"))
            {
                var newAsm = Assembly.LoadFrom(Path.GetFullPath(item));
                foreach (var toolType in newAsm.GetTypes())
                    if (toolType.GetInterface(typeof(INebulaTool).FullName) != null)
                    {
                        INebulaTool tool = (INebulaTool)Activator.CreateInstance(toolType);
                        tools.Add(tool);
                        toolNames.Add($"[{NebulaCore.ColorRules[3]}]{tool.Name}[/]");
                    }
            }
            string mfaParserToolName = $"[{NebulaCore.ColorRules[3]}]MFA Parser[/]";
            if (toolNames.Contains(mfaParserToolName))
            { toolNames.Remove(mfaParserToolName); toolNames.Insert(1, mfaParserToolName); }
            List<string> selectedTasks = AnsiConsole.Prompt(
                new MultiSelectionPrompt<string>()
                    .Title($"[{NebulaCore.ColorRules[1]}]Select a task.[/]")
                    .InstructionsText($"[{NebulaCore.ColorRules[2]}](Press [{NebulaCore.ColorRules[1]}]<space>[/] to select a task, " +
                                      $"[{NebulaCore.ColorRules[1]}]<enter>[/] to execute)\n(Quit will always execute last.)[/]")
                    .AddChoices(toolNames)
                    .HighlightStyle(NebulaCore.ColorRules[4]));
            selectedTasks = selectedTasks.Select(str => Markup.Remove(str)).ToList();
            toolStopwatch = Stopwatch.StartNew();
            foreach (INebulaTool tool in tools)
                if (selectedTasks.Contains(tool.Name))
                {
#if !DEBUG
                    try {
#endif
                        tool.Execute();
#if !DEBUG
                    } catch (Exception ex) {
                        Logger.Log(tool.GetType(), "ERROR: \n" + ex.Message + '\n' + ex.StackTrace);
                        Logger.Save(); throw;
                    }
#endif
                    Logger.Save(); GC.Collect();
                }
            toolStopwatch.Stop();
            if (selectedTasks.Contains($"Quit")) Environment.Exit(0);
            else SelectTool();
        }
    }
}