using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Hd2Qd.HellForge;

[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace HellForge.Tests
{
    /// <summary>A HellForge installation in %TEMP% and a command line that answers as
    /// HellForge.Cli.exe 0.4.0-beta does for the commands the plugin uses (the JSON shapes of
    /// external-launchers.md and of `mods`/`status` read on a real installation 2026-11-11).
    /// Pack ids follow CosmeticPacks.Parse; groups: a code mod is gameplay unless its manifest
    /// says per-player, a pack is cosmetic.</summary>
    sealed class FakeHellForge : IDisposable
    {
        public readonly string Root, Install, Home, Library;
        public bool Running;
        public readonly List<string[]> Calls = new List<string[]>();
        /// <summary>HellForge's state file (mods\hellforge-mods.json): its "disabled" and "order".</summary>
        string StateFile => Path.Combine(Mods, "hellforge-mods.json");
        Dictionary<string, object> State() => File.Exists(StateFile) ? Json.Obj(File.ReadAllText(StateFile)) ?? new Dictionary<string, object>() : new Dictionary<string, object> { ["format"] = 1L };
        void SaveState(Dictionary<string, object> d) => File.WriteAllText(StateFile, Json.Write(d));
        public HashSet<string> Disabled => new HashSet<string>((Json.List(State(), "disabled") ?? new List<object>()).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        /// <summary>The player switching a mod off on HellForge's Mods page.</summary>
        public void SwitchOff(string id)
        {
            var d = State();
            d["disabled"] = Disabled.Concat(new[] { id }).Distinct(StringComparer.OrdinalIgnoreCase).Cast<object>().ToList();
            SaveState(d);
        }
        /// <summary>The last mod-order, top first.</summary>
        public List<string> Order => (Json.List(State(), "order") ?? new List<object>()).OfType<string>().ToList();
        /// <summary>The CLI commands that changed the state, by name (the deploy should need none).</summary>
        public int StateCalls;

        public FakeHellForge()
        {
            Root = Path.Combine(Path.GetTempPath(), "hd2qd-hellforge-test-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Install = Path.Combine(Root, "HellForge");
            Home = Path.Combine(Root, "home");
            Library = Path.Combine(Root, "library");
            Directory.CreateDirectory(Path.Combine(Install, "mods"));
            Directory.CreateDirectory(Home);
            Directory.CreateDirectory(Library);
            File.WriteAllText(Path.Combine(Install, "hellforge-install.json"), "{\"schemaVersion\": 1, \"buildId\": \"25480438\"}");
            Hf.Runner = Answer;
        }

        public void Dispose()
        {
            Hf.Runner = null;
            try { Directory.Delete(Root, true); } catch (Exception) { }
        }

        public string Mods => Path.Combine(Install, "mods");

        Hf.Result Ok(object json) => new Hf.Result { Out = Json.Write(json) };
        static Hf.Result Fail(string why) => new Hf.Result { Code = 1, Err = "HellForge: " + why };

        Hf.Result Answer(string install, string[] args)
        {
            Calls.Add(args);
            string Arg(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
            switch (args[0])
            {
                case "status":
                    return Ok(new Dictionary<string, object>
                    {
                        ["install"] = Install, ["gameBuild"] = "25480438", ["hellforge"] = "0.4.0-beta (test)", ["engine"] = "0.1.47", ["modSdk"] = 16,
                        ["ready"] = new Dictionary<string, object> { ["gameCopy"] = true, ["sessionRunner"] = true },
                        ["settings"] = new Dictionary<string, object> { ["profile"] = "default", ["sandbox"] = true },
                        ["session"] = new Dictionary<string, object> { ["running"] = Running, ["lastRun"] = Path.Combine(Install, "logs", "sessions", "r1"), ["lastStatus"] = "observed" },
                    });
                case "mods":
                    return Ok(ModsJson());
                case "mod-remove":
                {
                    if (Running) return Fail("a session is running");
                    var m = List().FirstOrDefault(x => x.Id == Arg("--id"));
                    if (m == null) return Fail("no mod " + Arg("--id"));
                    if (m.Archive != null) File.Delete(m.Archive); else Directory.Delete(m.Folder, true);
                    return Ok(new Dictionary<string, object>());
                }
                case "build-source-mods":
                    if (Running) return Fail("a session is running");
                    File.WriteAllText(Arg("--progress"), "{\"stage\":\"tools\",\"done\":0,\"total\":1,\"mod\":\"Native\"}");
                    System.Threading.Thread.Sleep(700);                                   // long enough for the deploy to read it
                    File.WriteAllText(Arg("--progress"), "{\"stage\":\"done\",\"done\":1,\"total\":1,\"mod\":\"\"}");
                    return Ok(new Dictionary<string, object> { ["install"] = Install, ["sourceMods"] = 1L, ["built"] = 1L, ["problems"] = new List<object>() });
                case "mod-order":
                    if (Running) return Fail("a session is running");
                    StateCalls++;
                    { var d = State(); d["order"] = Arg("--ids").Split(',').Cast<object>().ToList(); SaveState(d); }
                    return Ok(new Dictionary<string, object>());
                case "mod-switch":
                    if (Running) return Fail("a session is running");
                    StateCalls++;
                    if (Arg("--on") == "no") SwitchOff(Arg("--id"));
                    else { var d = State(); d["disabled"] = Disabled.Where(x => !string.Equals(x, Arg("--id"), StringComparison.OrdinalIgnoreCase)).Cast<object>().ToList(); SaveState(d); }
                    return Ok(new Dictionary<string, object>());
            }
            return Fail("unknown command " + args[0]);
        }

        public sealed class M { public string Id, Name, Folder, Archive, Group; public List<string> Tags = new List<string>(); }

        /// <summary>What the last list left out, as HellForge's Mods.Load reports it.</summary>
        public readonly List<string> Problems = new List<string>();

        public List<M> List()
        {
            var list = new List<M>();
            Problems.Clear();
            foreach (var dir in Directory.GetDirectories(Mods).Where(d => !Path.GetFileName(d).StartsWith(".")))
                if (Read(dir, null, Problems) is M m) list.Add(m);
            foreach (var zip in Directory.GetFiles(Mods, "*.zip"))
            {
                var to = Path.Combine(Mods, ".unpacked", Path.GetFileNameWithoutExtension(zip) + "-" + new FileInfo(zip).LastWriteTimeUtc.Ticks);
                if (!Directory.Exists(to)) ZipFile.ExtractToDirectory(zip, to);
                if (Read(to, zip, Problems) is M m) list.Add(m);
            }
            return list;
        }

        static M Read(string dir, string archive, List<string> problems)
        {
            var code = Path.Combine(dir, "hellforge-mod.json");
            if (File.Exists(code))
            {
                var text = File.ReadAllText(code);
                var d = Json.Obj(text);
                bool perPlayer = Json.Str(d, "coop") == "per-player";
                // Mods.DecideTier: a mod with network field types cannot be per-player; it is left out with a problem
                if (perPlayer && text.Contains("networkFieldTypes"))
                {
                    problems.Add((archive != null ? Path.GetFileName(archive) : Path.GetFileName(dir)) + ": coop is per-player, but native.networkFieldTypes change the network for every player.");
                    return null;
                }
                var m = new M
                {
                    Id = Json.Str(d, "id"), Name = Json.Str(d, "name"), Folder = dir, Archive = archive, Tags = { "code" },
                    Group = perPlayer ? "per-player" : "gameplay",
                };
                if (Json.Bool(d, "bundledInTest")) m.Tags.Add("bundled");
                return m;
            }
            var id = Placed.IdOf(dir, Path.GetFileName(dir));
            if (id == null) return null;
            var name = Json.Str(Json.Obj(File.Exists(Path.Combine(dir, "manifest.json")) ? File.ReadAllText(Path.Combine(dir, "manifest.json")) : null), "Name") ?? Path.GetFileName(dir);
            return new M { Id = id, Name = name, Folder = dir, Archive = archive, Tags = { "pack" }, Group = "cosmetic" };
        }

        Dictionary<string, object> ModsJson() => ModsJson(List());

        Dictionary<string, object> ModsJson(List<M> listed) => new Dictionary<string, object>
        {
            ["mods"] = listed.Select(m => (object)new Dictionary<string, object>
            {
                ["id"] = m.Id, ["name"] = m.Name, ["version"] = "", ["enabled"] = !Disabled.Contains(m.Id), ["group"] = m.Group,
                ["tags"] = m.Tags.Cast<object>().ToArray(), ["folder"] = m.Folder, ["archive"] = m.Archive, ["removable"] = true, ["options"] = null,
            }).ToArray(),
            ["problems"] = Problems.Cast<object>().ToArray(), ["modlist"] = null, ["settings"] = Path.Combine(Mods, "hellforge-mods.json"),
        };

        // ---------------------------------------------------------------- building mods

        /// <summary>A library mod: a folder with a manifest.json (Guid) and one patch file.</summary>
        public string LibraryPack(string name, string guid, byte[] patch = null)
        {
            var dir = Path.Combine(Library, name + "_0123456789ab");
            Directory.CreateDirectory(Path.Combine(dir, "Main"));
            File.WriteAllText(Path.Combine(dir, "manifest.json"),
                "{\"Version\": 1, \"Guid\": \"" + guid + "\", \"Name\": \"" + name + "\", \"Options\": [{\"Name\": \"Main\", \"Include\": [\"Main\"]}]}");
            File.WriteAllBytes(Path.Combine(dir, "Main", "0123456789abcdef.patch_0"), patch ?? Patch());
            return dir;
        }

        /// <summary>A code mod as a folder in mods\ (as a player copies one there).</summary>
        public string CodeFolder(string id, string name, string extra = "")
        {
            var dir = Path.Combine(Mods, name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "hellforge-mod.json"), "{\r\n  \"id\": \"" + id + "\",\r\n  \"name\": \"" + name + "\"" + extra + "\r\n}\r\n");
            return dir;
        }

        public const string Manifest = "{\r\n  \"id\": \"someone.big-pack\",\r\n  \"name\": \"Big Pack\",\r\n  \"version\": \"1.0.0\"\r\n}\r\n";

        /// <summary>A code mod as a zip in mods\ (as HellForge holds Vanilla Plus Megapack).</summary>
        public string CodeZip(string manifest = Manifest)
        {
            var zip = Path.Combine(Mods, "Big Pack.zip");
            if (File.Exists(zip)) File.Delete(zip);
            using (var a = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                using (var w = new StreamWriter(a.CreateEntry("hellforge-mod.json").Open(), new UTF8Encoding(false))) w.Write(manifest);
                using (var w = new StreamWriter(a.CreateEntry("src/mod.c").Open())) w.Write("int x;");
            }
            return zip;
        }

        /// <summary>A patch file: the 0x48-byte head, no type rows, one 80-byte row per resource,
        /// then each resource's bytes.</summary>
        public static byte[] Patch(params (ulong Type, byte[] Data)[] resources)
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write(0xF0000011u); w.Write(0u); w.Write((uint)resources.Length);
            w.Write(new byte[0x48 - 12]);
            long data = 0x48 + resources.Length * 80L;
            foreach (var r in resources)
            {
                var row = new byte[80];
                BitConverter.GetBytes(0x1111UL).CopyTo(row, 0);
                BitConverter.GetBytes(r.Type).CopyTo(row, 8);
                BitConverter.GetBytes((ulong)data).CopyTo(row, 0x10);
                BitConverter.GetBytes((uint)r.Data.Length).CopyTo(row, 0x38);
                w.Write(row);
                data += r.Data.Length;
            }
            foreach (var r in resources) w.Write(r.Data);
            return ms.ToArray();
        }

        // ---------------------------------------------------------------- files

        public static int Links(string path)
        {
            using (var f = File.OpenRead(path))
                return GetFileInformationByHandle(f.SafeFileHandle.DangerousGetHandle(), out var info) ? (int)info.Links : 0;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct Info
        {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
            public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetFileInformationByHandle(IntPtr file, out Info info);
    }
}
