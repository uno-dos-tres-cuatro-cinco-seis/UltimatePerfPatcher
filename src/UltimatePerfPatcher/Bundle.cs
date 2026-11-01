using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace UltimatePerfPatcher
{
    /// <summary>A file inside a .NET single-file bundle.</summary>
    internal sealed class BundleEntry
    {
        public string Path;
        public byte Kind;       // 1 Assembly, 2 NativeBinary, 3 DepsJson, 4 RuntimeConfigJson, 5 Symbols
        public byte[] Data;
    }

    /// <summary>
    /// Reads and writes .NET 5 single-file bundles (manifest format 2.0) without executing them.
    /// </summary>
    internal sealed class Bundle
    {
        private static readonly byte[] Signature =
        {
            0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38, 0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32,
            0x13, 0xf5, 0xb9, 0xe6, 0xef, 0xae, 0x33, 0x18, 0xee, 0x3b, 0x2d, 0xce, 0x24, 0xb3, 0x6a, 0xae
        };

        private const int AssemblyAlignment = 4096;

        public byte[] Host;            // the apphost bytes in front of the first bundled file
        public int HeaderOffsetPosition;
        public long Flags;
        public List<BundleEntry> Entries = new List<BundleEntry>();

        public static Bundle Read(byte[] file)
        {
            int sig = IndexOf(file, Signature);
            if (sig < 8)
                throw new PatchException("This file is not a .NET single-file bundle.");
            long header = BitConverter.ToInt64(file, sig - 8);
            if (header <= 0 || header >= file.Length)
                throw new PatchException("This file is not a .NET single-file bundle (empty header).");

            var r = new BinaryReader(new MemoryStream(file), Encoding.UTF8);
            r.BaseStream.Position = header;
            uint major = r.ReadUInt32(), minor = r.ReadUInt32();
            int count = r.ReadInt32();
            if (major != 2)
                throw new PatchException($"Unsupported bundle format {major}.{minor} (only the .NET 5 format 2.x is supported).");
            r.ReadString();                                  // bundle id
            r.ReadInt64(); r.ReadInt64(); r.ReadInt64(); r.ReadInt64();   // deps.json / runtimeconfig.json location
            var bundle = new Bundle { HeaderOffsetPosition = sig - 8, Flags = r.ReadInt64() };

            long firstOffset = long.MaxValue;
            for (int i = 0; i < count; i++)
            {
                long offset = r.ReadInt64(), size = r.ReadInt64();
                byte kind = r.ReadByte();
                string path = r.ReadString();
                var data = new byte[size];
                Buffer.BlockCopy(file, checked((int)offset), data, 0, checked((int)size));
                bundle.Entries.Add(new BundleEntry { Path = path, Kind = kind, Data = data });
                firstOffset = Math.Min(firstOffset, offset);
            }
            bundle.Host = new byte[firstOffset];
            Buffer.BlockCopy(file, 0, bundle.Host, 0, (int)firstOffset);
            return bundle;
        }

        public BundleEntry Find(string path) => Entries.Find(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Writes the bundle. The Authenticode certificate table is dropped (any content change invalidates
        /// it) and the apphost is marked LARGE_ADDRESS_AWARE.
        /// </summary>
        public byte[] Write()
        {
            var host = (byte[])Host.Clone();
            int pe = BitConverter.ToInt32(host, 0x3c);
            int opt = pe + 24;
            bool pe32 = BitConverter.ToUInt16(host, opt) == 0x10b;
            int dataDirectories = opt + (pe32 ? 96 : 112);
            Array.Clear(host, dataDirectories + 4 * 8, 8);              // IMAGE_DIRECTORY_ENTRY_SECURITY
            Array.Clear(host, opt + 64, 4);                              // CheckSum
            ushort characteristics = BitConverter.ToUInt16(host, pe + 22);
            BitConverter.GetBytes((ushort)(characteristics | 0x20)).CopyTo(host, pe + 22);   // LARGE_ADDRESS_AWARE

            var ms = new MemoryStream();
            var w = new BinaryWriter(ms, Encoding.UTF8);
            w.Write(host);
            var offsets = new long[Entries.Count];
            long depsOffset = 0, depsSize = 0, configOffset = 0, configSize = 0;
            for (int i = 0; i < Entries.Count; i++)
            {
                var e = Entries[i];
                if (e.Kind == 1 && ms.Position % AssemblyAlignment != 0)
                    w.Write(new byte[AssemblyAlignment - ms.Position % AssemblyAlignment]);
                offsets[i] = ms.Position;
                w.Write(e.Data);
                if (e.Kind == 3) { depsOffset = offsets[i]; depsSize = e.Data.Length; }
                if (e.Kind == 4) { configOffset = offsets[i]; configSize = e.Data.Length; }
            }
            long header = ms.Position;
            w.Write(2u); w.Write(0u); w.Write(Entries.Count);
            w.Write(NewBundleId());
            w.Write(depsOffset); w.Write(depsSize); w.Write(configOffset); w.Write(configSize); w.Write(Flags);
            for (int i = 0; i < Entries.Count; i++)
            {
                w.Write(offsets[i]); w.Write((long)Entries[i].Data.Length); w.Write(Entries[i].Kind); w.Write(Entries[i].Path);
            }
            w.Flush();
            var result = ms.ToArray();
            BitConverter.GetBytes(header).CopyTo(result, HeaderOffsetPosition);
            return result;
        }

        private static string NewBundleId()
        {
            var bytes = new byte[21];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return Convert.ToBase64String(bytes).Substring(0, 28) + "=";
        }

        private static int IndexOf(byte[] haystack, byte[] needle)
        {
            for (int i = 0; i <= haystack.Length - needle.Length; i++)
            {
                if (haystack[i] != needle[0]) continue;
                int j = 1;
                while (j < needle.Length && haystack[i + j] == needle[j]) j++;
                if (j == needle.Length) return i;
            }
            return -1;
        }
    }

    internal sealed class PatchException : Exception
    {
        public PatchException(string message) : base(message) { }
    }
}
