using System;
using Microsoft.Win32;

namespace Hd2Qd.HellForge
{
    /// <summary>Whether Windows' system proxy is on (Internet Settings: ProxyEnable, or a proxy
    /// configuration script). HellForge's account sync fails while one is on: its capture proxy
    /// does not leave the system proxy out (upstream issue #346), so the player is told to switch
    /// it off in the proxy tool (Clash and the like) first.</summary>
    static class Proxy
    {
        /// <summary>Tests only: the answer instead of the registry's.</summary>
        public static Func<bool> Override;

        public static bool On()
        {
            if (Override != null) return Override();
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings"))
                {
                    if (k == null) return false;
                    var enable = k.GetValue("ProxyEnable");
                    var script = k.GetValue("AutoConfigURL") as string;
                    return (enable is int i && i != 0) || !string.IsNullOrWhiteSpace(script);
                }
            }
            catch (Exception e) when (e is System.Security.SecurityException || e is UnauthorizedAccessException) { return false; }
        }
    }
}
