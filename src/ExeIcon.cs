using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace Hd2Qd.HellForge
{
    /// <summary>A program's own icon, read from its file as an .ico with every size it has: the
    /// first icon group among its resources, as Explorer shows it. The plugin draws HellForge's
    /// icon this way, from the player's own HellForge.exe, and ships none of it: that icon is
    /// HellForge's (GPL-3.0), this plugin is MIT, and HellForge's look on our button is
    /// HellForge's own copy, not one we pass around.</summary>
    static class ExeIcon
    {
        const uint AsDataFile = 0x2, AsImageResource = 0x20;
        static readonly IntPtr RtIcon = (IntPtr)3, RtGroupIcon = (IntPtr)14;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr LoadLibraryEx(string file, IntPtr reserved, uint flags);

        [DllImport("kernel32.dll")]
        static extern bool FreeLibrary(IntPtr module);

        delegate bool EnumNames(IntPtr module, IntPtr type, IntPtr name, IntPtr param);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern bool EnumResourceNames(IntPtr module, IntPtr type, EnumNames each, IntPtr param);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);

        [DllImport("kernel32.dll")]
        static extern IntPtr LoadResource(IntPtr module, IntPtr res);

        [DllImport("kernel32.dll")]
        static extern IntPtr LockResource(IntPtr data);

        [DllImport("kernel32.dll")]
        static extern uint SizeofResource(IntPtr module, IntPtr res);

        static readonly object gate = new object();
        static (string File, long Length, DateTime Time, byte[] Ico) last;

        /// <summary>The .ico of file's first icon group, or null (no such file, no icon, not a
        /// program). Read again only when the file changes.</summary>
        public static byte[] Read(string file)
        {
            try
            {
                var f = new FileInfo(file);
                if (!f.Exists) return null;
                lock (gate)
                {
                    if (string.Equals(last.File, f.FullName, StringComparison.OrdinalIgnoreCase) && last.Length == f.Length && last.Time == f.LastWriteTimeUtc)
                        return last.Ico;
                    var ico = ReadNow(f.FullName);
                    last = (f.FullName, f.Length, f.LastWriteTimeUtc, ico);
                    return ico;
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is Win32Exception || e is ArgumentException)
            {
                return null;
            }
        }

        static byte[] ReadNow(string file)
        {
            var module = LoadLibraryEx(file, IntPtr.Zero, AsDataFile | AsImageResource);
            if (module == IntPtr.Zero) return null;
            try
            {
                IntPtr group = IntPtr.Zero;
                bool named = false;
                EnumResourceNames(module, RtGroupIcon, (m, t, name, p) =>
                {
                    // a name is an id (below 0x10000) or a string the module owns: keep it while the
                    // module is loaded, which it is until FreeLibrary below
                    group = name;
                    named = true;
                    return false;                                             // the first one
                }, IntPtr.Zero);
                if (!named) return null;
                var dir = Bytes(module, group, RtGroupIcon);
                // GRPICONDIR: reserved, type 1, count; then per image 14 bytes, the last two its RT_ICON id
                if (dir == null || dir.Length < 6 || BitConverter.ToUInt16(dir, 2) != 1) return null;
                int count = BitConverter.ToUInt16(dir, 4);
                if (count == 0 || dir.Length < 6 + 14 * count) return null;
                var images = new List<(byte[] Entry, byte[] Data)>();
                for (int i = 0; i < count; i++)
                {
                    var at = 6 + 14 * i;
                    var id = BitConverter.ToUInt16(dir, at + 12);
                    var data = Bytes(module, (IntPtr)id, RtIcon);
                    if (data == null) continue;
                    var entry = new byte[12];
                    Array.Copy(dir, at, entry, 0, 12);                       // width, height, colours, reserved, planes, bits, size
                    Array.Copy(BitConverter.GetBytes((uint)data.Length), 0, entry, 8, 4);
                    images.Add((entry, data));
                }
                if (images.Count == 0) return null;
                using (var o = new MemoryStream())
                {
                    var w = new BinaryWriter(o);
                    w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)images.Count);
                    var offset = 6 + 16 * images.Count;
                    foreach (var (entry, data) in images)
                    {
                        w.Write(entry);
                        w.Write((uint)offset);
                        offset += data.Length;
                    }
                    foreach (var (_, data) in images) w.Write(data);
                    w.Flush();
                    return o.ToArray();
                }
            }
            finally { FreeLibrary(module); }
        }

        static byte[] Bytes(IntPtr module, IntPtr name, IntPtr type)
        {
            var res = FindResource(module, name, type);
            if (res == IntPtr.Zero) return null;
            var size = SizeofResource(module, res);
            var handle = LoadResource(module, res);
            var p = handle == IntPtr.Zero ? IntPtr.Zero : LockResource(handle);
            if (p == IntPtr.Zero || size == 0) return null;
            var b = new byte[size];
            Marshal.Copy(p, b, 0, (int)size);
            return b;
        }
    }
}
