using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Resources;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;

namespace UltimatePerfPatcher
{
    /// <summary>Applies the IL and resource patches to "8BitDo Ultimate Software V2.dll".</summary>
    internal static class AssemblyPatcher
    {
        public static byte[] Patch(byte[] appDll, byte[] advancePerfDll, Action<string> log)
        {
            var module = ModuleDefMD.Load(appDll);
            var perfType = ModuleDefMD.Load(advancePerfDll).Find("AdvancePerf.PerfHooks", false);
            IMethod Import(string name) => module.Import(perfType.FindMethod(name));

            // 1. language lookup no longer rewrites user.config for every UI string
            Redirect(module, "AdvanceSuper.Data.LanguageTools", "IsChineseSimple",
                (p, m) => m.Name == "Save" && m.DeclaringType.Name.EndsWith("SettingsBase"), Import("SaveSettingsOnce"), 2, log);
            // 2. global keyboard hook only while the main window is active
            Redirect(module, "AdvanceSuper.MainWindow", ".ctor",
                (p, m) => m.Name == "StartReadKey", Import("GateKeyboardHook"), 1, log);
            // 3. device polling backs off while the window is hidden or minimized (first Sleep(1000) = loop head)
            Redirect(module, "AdvanceSuper.MainWindow", "animation", IsSleep1000, Import("PollSleep"), 1, log);
            Redirect(module, "AdvanceSuper.Hid.Switch", "start", IsSleep1000, Import("PollSleep"), 1, log);
            // 4. oversized images
            ShrinkHugeImages(module, log);

            var options = new ModuleWriterOptions(module);
            options.MetadataOptions.Flags |= MetadataFlags.PreserveAll;
            var ms = new MemoryStream();
            module.Write(ms, options);
            return ms.ToArray();
        }

        private static bool IsSleep1000(Instruction previous, IMethod m) =>
            m.FullName == "System.Void System.Threading.Thread::Sleep(System.Int32)"
            && previous.IsLdcI4() && previous.GetLdcI4Value() == 1000;

        /// <summary>Points matching call sites to a PerfHooks method; refuses to continue on unexpected IL.</summary>
        private static void Redirect(ModuleDef module, string typeName, string methodName,
            Func<Instruction, IMethod, bool> match, IMethod target, int expected, Action<string> log)
        {
            var type = module.Find(typeName, false)
                ?? throw new PatchException($"Type {typeName} not found - unsupported app version.");
            var method = type.Methods.SingleOrDefault(x => x.Name == methodName && x.HasBody)
                ?? throw new PatchException($"Method {typeName}::{methodName} not found - unsupported app version.");
            var body = method.Body.Instructions;
            var sites = new List<Instruction>();
            for (int i = 1; i < body.Count && sites.Count <= expected; i++)
            {
                if ((body[i].OpCode == OpCodes.Call || body[i].OpCode == OpCodes.Callvirt)
                    && body[i].Operand is IMethod m && match(body[i - 1], m))
                    sites.Add(body[i]);
            }
            if (sites.Count < expected)
                throw new PatchException($"{typeName}::{methodName}: expected {expected} call site(s), found {sites.Count} - unsupported app version.");
            foreach (var site in sites.Take(expected))
            {
                site.OpCode = OpCodes.Call;
                site.Operand = target;
            }
            log($"  {typeName.Replace("AdvanceSuper.", "")}.{methodName}: {expected} call(s) -> PerfHooks.{target.Name}");
        }

        /// <summary>
        /// Halves PNGs of 4000 px or wider inside the WPF resources and halves their DPI too, so WPF lays
        /// them out at exactly the same size (device-independent size = pixels * 96 / dpi).
        /// </summary>
        private static void ShrinkHugeImages(ModuleDef module, Action<string> log)
        {
            var res = module.Resources.OfType<EmbeddedResource>().SingleOrDefault(r => r.Name.EndsWith(".g.resources"))
                ?? throw new PatchException("WPF resources not found - unsupported app version.");
            var output = new MemoryStream();
            int shrunk = 0;
            using (var reader = new ResourceReader(res.CreateReader().AsStream()))
            using (var writer = new ResourceWriter(output))
            {
                var e = reader.GetEnumerator();
                while (e.MoveNext())
                {
                    var name = (string)e.Key;
                    reader.GetResourceData(name, out var typeName, out var data);
                    if (name.EndsWith(".png") && data.Length > 28 && PngWidth(data, 4) >= 4000)
                    {
                        var png = Shrink(data.Skip(4).ToArray(), out var before, out var after);   // stream payload = int32 length + bytes
                        data = BitConverter.GetBytes(png.Length).Concat(png).ToArray();
                        log($"  {name}: {before} -> {after}");
                        shrunk++;
                    }
                    writer.AddResourceData(name, typeName, data);
                }
                writer.Generate();
            }
            if (shrunk > 0)
                module.Resources[module.Resources.IndexOf(res)] = new EmbeddedResource(res.Name, output.ToArray(), res.Attributes);
            log($"  images shrunk: {shrunk}");
        }

        private static int PngWidth(byte[] data, int offset) =>
            data[offset] == 0x89 && data[offset + 1] == (byte)'P'
                ? (data[offset + 16] << 24) | (data[offset + 17] << 16) | (data[offset + 18] << 8) | data[offset + 19]
                : 0;

        private static byte[] Shrink(byte[] png, out string before, out string after)
        {
            using (var src = new Bitmap(new MemoryStream(png)))
            using (var dst = new Bitmap(src.Width / 2, src.Height / 2, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(dst))
                {
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    using (var attributes = new ImageAttributes())
                    {
                        attributes.SetWrapMode(WrapMode.TileFlipXY);
                        g.DrawImage(src, new Rectangle(0, 0, dst.Width, dst.Height), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, attributes);
                    }
                }
                dst.SetResolution(src.HorizontalResolution / 2, src.VerticalResolution / 2);
                before = $"{src.Width}x{src.Height}@{src.HorizontalResolution:0}dpi";
                after = $"{dst.Width}x{dst.Height}@{dst.HorizontalResolution:0}dpi";
                var ms = new MemoryStream();
                dst.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
        }
    }
}
