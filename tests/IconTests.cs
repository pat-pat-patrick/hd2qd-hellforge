using System;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;
using Hd2Qd.HellForge;
using Xunit;

namespace HellForge.Tests
{
    /// <summary>The title-bar button's icon is read from the player's HellForge.exe, never shipped
    /// (HellForge's icon is GPL-3.0; this plugin is MIT). The test assembly carries the plugin's
    /// own anvil as its Win32 icon, so a copy of it stands in for HellForge.exe.</summary>
    public class IconTests
    {
        static (int Width, int Height)[] Sizes(byte[] ico) =>
            new IconBitmapDecoder(new MemoryStream(ico), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames
                .Select(f => (f.PixelWidth, f.PixelHeight)).OrderBy(s => s.PixelWidth).ToArray();

        [Fact]
        public void A_programs_icon_is_read_with_every_size_it_has()
        {
            var dir = Path.Combine(Path.GetTempPath(), "hd2qd-hellforge-icon-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            try
            {
                var exe = Path.Combine(dir, "HellForge.exe");
                File.Copy(typeof(IconTests).Assembly.Location, exe);
                var ico = ExeIcon.Read(exe);
                Assert.NotNull(ico);
                Assert.Equal(new byte[] { 0, 0, 1, 0 }, ico.Take(4).ToArray());
                byte[] shipped;
                using (var s = typeof(Plugin).Assembly.GetManifestResourceStream("app.ico"))
                using (var m = new MemoryStream()) { s.CopyTo(m); shipped = m.ToArray(); }
                Assert.Equal(Sizes(shipped), Sizes(ico));                          // all of them, 16 to 256
                Assert.Same(ico, ExeIcon.Read(exe));                               // read once while the file is unchanged

                var none = Path.Combine(dir, "none.exe");                          // not a program: no icon, nothing thrown
                File.WriteAllText(none, "not a program");
                Assert.Null(ExeIcon.Read(none));
                Assert.Null(ExeIcon.Read(Path.Combine(dir, "missing.exe")));
            }
            finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
        }
    }
}
