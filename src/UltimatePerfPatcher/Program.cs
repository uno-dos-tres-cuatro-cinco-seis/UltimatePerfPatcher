using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace UltimatePerfPatcher
{
    internal static class Program
    {
        private const string AppExe = "8BitDo Ultimate Software V2.exe";
        private const string AppDll = "8BitDo Ultimate Software V2.dll";

        private static int Main(string[] args)
        {
            // dnlib and the helper assembly are embedded so the patcher is a single file
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                var name = new AssemblyName(e.Name).Name + ".dll";
                using (var stream = typeof(Program).Assembly.GetManifestResourceStream(name))
                {
                    if (stream == null) return null;
                    var bytes = new byte[stream.Length];
                    stream.Read(bytes, 0, bytes.Length);
                    return Assembly.Load(bytes);
                }
            };
            bool interactive = args.Length == 0;
            int code;
            try
            {
                code = Run(args);
            }
            catch (PatchException ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("ERROR: " + ex.Message);
                Console.ResetColor();
                code = 1;
            }
            if (interactive)
            {
                Console.WriteLine();
                Console.WriteLine("Press Enter to close.");
                Console.ReadLine();
            }
            return code;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]   // keeps dnlib types out of Main until the resolver is set
        private static int Run(string[] args)
        {
            Console.WriteLine("UltimatePerfPatcher " + typeof(Program).Assembly.GetName().Version.ToString(3) +
                              " - unofficial performance patch for 8BitDo Ultimate Software V2");
            Console.WriteLine();
            if (args.Any(a => a == "-h" || a == "--help" || a == "/?"))
            {
                Console.WriteLine("usage: UltimatePerfPatcher.exe [path\\to\\8BitDo Ultimate Software V2.exe] [output.exe]");
                Console.WriteLine("Without arguments the app is looked for next to the patcher and in the current folder.");
                Console.WriteLine("The original file is never modified; the result is written next to it as \"... (perf).exe\".");
                return 0;
            }

            string input = args.Length > 0 ? args[0] : FindApp();
            if (input == null || !File.Exists(input))
                throw new PatchException($"\"{AppExe}\" not found. Put the patcher into the app folder or drag the EXE onto the patcher.");
            input = Path.GetFullPath(input);
            string output = args.Length > 1 ? Path.GetFullPath(args[1])
                : Path.Combine(Path.GetDirectoryName(input), Path.GetFileNameWithoutExtension(input) + " (perf).exe");
            if (string.Equals(input, output, StringComparison.OrdinalIgnoreCase))
                throw new PatchException("Output must not overwrite the original EXE.");

            Console.WriteLine("Input : " + input);
            var file = File.ReadAllBytes(input);
            Console.WriteLine("SHA256: " + Hex(SHA256.Create().ComputeHash(file)));
            var bundle = Bundle.Read(file);
            var app = bundle.Find(AppDll) ?? throw new PatchException($"{AppDll} is not part of this bundle.");
            var deps = bundle.Entries.Single(e => e.Kind == 3);
            if (bundle.Find("AdvancePerf.dll") != null)
                throw new PatchException("This EXE is already patched. Run the patcher on the original EXE.");
            Console.WriteLine($"Bundle: {bundle.Entries.Count} files, app assembly {app.Data.Length / 1048576.0:0.0} MB");
            Console.WriteLine();

            Console.WriteLine("Patching:");
            var perf = ReadResource("AdvancePerf.dll");
            var patched = AssemblyPatcher.Patch(app.Data, perf, Console.WriteLine);
            app.Data = patched;
            bundle.Entries.Insert(bundle.Entries.IndexOf(app) + 1, new BundleEntry { Path = "AdvancePerf.dll", Kind = 1, Data = perf });
            deps.Data = AddToDeps(deps.Data);
            Console.WriteLine("  deps.json: AdvancePerf 1.0.0 registered");
            Console.WriteLine("  apphost: LARGE_ADDRESS_AWARE set, certificate table removed");

            var result = bundle.Write();
            Verify(result, bundle);
            var temp = output + ".tmp";
            File.WriteAllBytes(temp, result);
            if (File.Exists(output)) File.Delete(output);
            File.Move(temp, output);

            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("Done: " + output);
            Console.ResetColor();
            Console.WriteLine("Start the app with this EXE. It must stay in the app folder (it uses the files next to it).");
            return 0;
        }

        private static string FindApp()
        {
            foreach (var dir in new[] { AppDomain.CurrentDomain.BaseDirectory, Environment.CurrentDirectory })
            {
                var candidate = Path.Combine(dir, AppExe);
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        /// <summary>Registers AdvancePerf.dll in deps.json; the host only loads bundled assemblies listed there.</summary>
        private static byte[] AddToDeps(byte[] json)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var text = Encoding.UTF8.GetString(json).TrimStart('﻿');
            var root = serializer.Deserialize<Dictionary<string, object>>(text);
            var targets = (Dictionary<string, object>)root["targets"];
            var target = (Dictionary<string, object>)targets.First(t => t.Key.Contains("/win-")).Value;
            var appEntry = (Dictionary<string, object>)target.First(t => t.Key.StartsWith("8BitDo Ultimate Software V2/")).Value;
            if (!appEntry.ContainsKey("dependencies")) appEntry["dependencies"] = new Dictionary<string, object>();
            ((Dictionary<string, object>)appEntry["dependencies"])["AdvancePerf"] = "1.0.0";
            target["AdvancePerf/1.0.0"] = new Dictionary<string, object>
            {
                ["runtime"] = new Dictionary<string, object>
                {
                    ["AdvancePerf.dll"] = new Dictionary<string, object> { ["assemblyVersion"] = "1.0.0.0", ["fileVersion"] = "1.0.0.0" }
                }
            };
            ((Dictionary<string, object>)root["libraries"])["AdvancePerf/1.0.0"] =
                new Dictionary<string, object> { ["type"] = "project", ["serviceable"] = false, ["sha512"] = "" };
            return Encoding.UTF8.GetBytes(serializer.Serialize(root));
        }

        /// <summary>Re-reads the written bundle and compares every file with what was meant to be written.</summary>
        private static void Verify(byte[] written, Bundle expected)
        {
            var check = Bundle.Read(written);
            if (check.Entries.Count != expected.Entries.Count)
                throw new PatchException("Verification failed: file count differs.");
            for (int i = 0; i < check.Entries.Count; i++)
            {
                var a = check.Entries[i]; var b = expected.Entries[i];
                if (a.Path != b.Path || a.Kind != b.Kind || !a.Data.SequenceEqual(b.Data))
                    throw new PatchException("Verification failed at " + b.Path);
            }
            Console.WriteLine($"  verified: {check.Entries.Count} bundled files read back identical");
        }

        private static byte[] ReadResource(string name)
        {
            using (var s = typeof(Program).Assembly.GetManifestResourceStream(name))
            {
                var ms = new MemoryStream();
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }

        private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
    }
}
