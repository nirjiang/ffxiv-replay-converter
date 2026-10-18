using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ReplayConverter
{
    public sealed class GameIdentity
    {
        public string VersionString;
        public uint Build;
        public string ExecutablePath;
        public string MarkerDate;
    }

    public static class VersionNames
    {
        public static string Display(string retail)
        {
            Match m = Regex.Match(retail ?? "", "^([0-9]+\\.[0-9]+)(h([0-9]*)|a)?$");
            if (!m.Success) return "版本待辨識";
            if (!m.Groups[2].Success) return m.Groups[1].Value;
            if (m.Groups[2].Value == "a") return m.Groups[1].Value + "（修正版）";
            return m.Groups[1].Value + "（修正" + (m.Groups[3].Value.Length == 0 ? "1" : m.Groups[3].Value) + "）";
        }

        public static string Supported(Profile profile)
        { return "內建 " + Display(profile.routes.OrderBy(r => r.version).First().gameVersion) + "～" + Display(profile.routes.OrderBy(r => r.version).Last().gameVersion)
            + " 對照 → " + Display(profile.targetGameVersion) + " · 較早版為試轉換 · 點此查看範圍"; }
    }

    public static class GameClient
    {
        static readonly object Gate = new object();
        static readonly Dictionary<string, GameIdentity> Cache = new Dictionary<string, GameIdentity>();
        static readonly byte[] Prefix = Encoding.ASCII.GetBytes("/*****ff14******rev");
        static readonly Regex Marker = new Regex(@"^/\*{5}ff14\*{6}rev(?<build>[1-9][0-9]{5,9})_(?<date>[0-9]{4}/[0-9]{2}/[0-9]{2})_(?<time>[0-9]{2}:[0-9]{2}:[0-9]{2})", RegexOptions.CultureInvariant);

        static InvalidDataException Bad(string text) { return new InvalidDataException(text); }

        public static GameIdentity Read(string versionFile)
        {
            if (String.IsNullOrWhiteSpace(versionFile) || !File.Exists(versionFile)) throw Bad("請先選擇遊戲安裝位置。");
            string version = File.ReadAllText(versionFile).Trim().Trim('\0');
            if (!Regex.IsMatch(version, "^[0-9]{4}\\.[0-9]{2}\\.[0-9]{2}\\.[0-9]{4}\\.[0-9]{4}$")) throw Bad("遊戲的安裝版本檔格式無法辨識。");
            string executable = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(versionFile), "ffxiv_dx11.exe"));
            var info = new FileInfo(executable);
            if (!info.Exists) throw Bad("此資料夾找不到 ffxiv_dx11.exe，請選擇正確的 game 資料夾。");
            string key = executable + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks + "|" + version;
            lock (Gate)
            {
                GameIdentity cached;
                if (Cache.TryGetValue(key, out cached)) return cached;
            }
            GameIdentity identity;
            using (var stream = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.Read))
                identity = ReadExecutable(stream, version, executable);
            var after = new FileInfo(executable);
            if (after.Length != info.Length || after.LastWriteTimeUtc != info.LastWriteTimeUtc || File.ReadAllText(versionFile).Trim().Trim('\0') != version)
                throw Bad("遊戲正在更新，請等更新完成後再試。");
            lock (Gate) { if (Cache.Count > 16) Cache.Clear(); Cache[key] = identity; }
            return identity;
        }

        public static GameIdentity ReadExecutable(Stream stream, string version, string path)
        {
            using (var reader = new BinaryReader(stream, Encoding.ASCII, true))
            {
                if (stream.Length < 256 || reader.ReadUInt16() != 0x5A4D) throw Bad("遊戲主程式不是有效的 Windows 執行檔。");
                stream.Position = 0x3C; uint peOffset = reader.ReadUInt32();
                if (peOffset > 4 * 1024 * 1024 || peOffset + 24L > stream.Length) throw Bad("遊戲主程式的 PE 標頭無效。");
                stream.Position = peOffset;
                if (reader.ReadUInt32() != 0x00004550 || reader.ReadUInt16() != 0x8664) throw Bad("只支援 Windows 64 位元版 FF14 主程式。");
                int sectionCount = reader.ReadUInt16();
                stream.Position = peOffset + 20; int optionalSize = reader.ReadUInt16();
                if (sectionCount < 1 || sectionCount > 96 || optionalSize < 112 || peOffset + 24L + optionalSize + sectionCount * 40L > stream.Length)
                    throw Bad("遊戲主程式的區段資料不完整。");
                stream.Position = peOffset + 24;
                if (reader.ReadUInt16() != 0x20B) throw Bad("遊戲主程式不是 PE64 格式。");
                var markers = new Dictionary<string, GameIdentity>();
                for (int section = 0; section < sectionCount; section++)
                {
                    long header = peOffset + 24L + optionalSize + section * 40L;
                    stream.Position = header + 16; uint rawSize = reader.ReadUInt32(), rawOffset = reader.ReadUInt32();
                    stream.Position = header + 36; uint flags = reader.ReadUInt32();
                    // The game's revision marker lives in initialized writable data, not in executable code.
                    if ((flags & 0x80000040) != 0x80000040 || rawSize == 0) continue;
                    if (rawSize > 64 * 1024 * 1024 || rawOffset + (long)rawSize > stream.Length) throw Bad("遊戲主程式的資料區段無效。");
                    stream.Position = rawOffset; byte[] data = reader.ReadBytes((int)rawSize);
                    if (data.Length != rawSize) throw Bad("遊戲主程式讀取不完整。");
                    for (int i = 0; i + Prefix.Length <= data.Length; i++)
                    {
                        if (data[i] != Prefix[0]) continue;
                        int match = 1; while (match < Prefix.Length && data[i + match] == Prefix[match]) match++;
                        if (match != Prefix.Length) continue;
                        Match marker = Marker.Match(Encoding.ASCII.GetString(data, i, Math.Min(96, data.Length - i)));
                        uint build; DateTime compiled;
                        if (!marker.Success || !UInt32.TryParse(marker.Groups["build"].Value, out build) ||
                            !DateTime.TryParseExact(marker.Groups["date"].Value + " " + marker.Groups["time"].Value, "yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out compiled))
                            throw Bad("遊戲建置標記格式已改變，需要更新工具的讀取器。");
                        string date = compiled.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture);
                        markers[build + "|" + date] = new GameIdentity { Build = build, VersionString = version, ExecutablePath = path, MarkerDate = date };
                    }
                }
                if (markers.Count != 1) throw Bad(markers.Count == 0 ? "找不到唯一的 FF14 建置標記，需要更新工具的讀取器。" : "遊戲主程式包含不同建置標記，無法可靠辨識。");
                GameIdentity result = markers.Values.Single();
                if (version == null || version.Length < 10 || result.MarkerDate != version.Substring(0, 10))
                    throw Bad("遊戲主程式與安裝版本檔不一致，請先完成遊戲更新。");
                return result;
            }
        }

        public static string HashExecutable(GameIdentity identity)
        {
            using (var stream = new FileStream(identity.ExecutablePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
    }

    public static class GameClientTests
    {
        public static string Fixture(string folder, string version, uint build)
        {
            Directory.CreateDirectory(folder);
            byte[] data = new byte[1024];
            using (var stream = new MemoryStream(data)) using (var writer = new BinaryWriter(stream))
            {
                writer.Write((ushort)0x5A4D); stream.Position = 0x3C; writer.Write((uint)0x80);
                stream.Position = 0x80; writer.Write((uint)0x4550); writer.Write((ushort)0x8664); writer.Write((ushort)1);
                stream.Position = 0x94; writer.Write((ushort)0xF0);
                stream.Position = 0x98; writer.Write((ushort)0x20B);
                stream.Position = 0x188; writer.Write(Encoding.ASCII.GetBytes(".data\0\0\0"));
                stream.Position = 0x198; writer.Write((uint)512); writer.Write((uint)512);
                stream.Position = 0x1AC; writer.Write((uint)0xC0000040);
                stream.Position = 512; writer.Write(Encoding.ASCII.GetBytes("/*****ff14******rev" + build + "_" + version.Substring(0, 10).Replace('.', '/') + "_02:20:43******ff14*****/\0"));
            }
            File.WriteAllBytes(Path.Combine(folder, "ffxiv_dx11.exe"), data);
            string manifest = Path.Combine(folder, "ffxivgame.ver"); File.WriteAllText(manifest, version);
            return manifest;
        }

        static void Reject(Action action)
        { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Invalid executable was accepted."); }

        public static void Run(string actualManifest, string folder)
        {
            GameIdentity actual = GameClient.Read(actualManifest);
            if (actual.Build != 14101345) throw new Exception("Unexpected local game build.");
            string fixture = Fixture(Path.Combine(folder, "valid"), "2026.10.06.0000.0000", 14101345);
            if (GameClient.Read(fixture).Build != actual.Build) throw new Exception("Fixture build read failed.");
            byte[] bytes = File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(fixture), "ffxiv_dx11.exe"));
            byte[] damaged = (byte[])bytes.Clone(); damaged[0] = 0;
            Reject(() => GameClient.ReadExecutable(new MemoryStream(damaged), actual.VersionString, "test"));
            byte[] absent = (byte[])bytes.Clone(); absent[512] = 0;
            Reject(() => GameClient.ReadExecutable(new MemoryStream(absent), actual.VersionString, "test"));
            byte[] conflicting = (byte[])bytes.Clone();
            byte[] other = Encoding.ASCII.GetBytes("/*****ff14******rev14101346_2026/10/06_02:20:43******"); Array.Copy(other, 0, conflicting, 700, other.Length);
            Reject(() => GameClient.ReadExecutable(new MemoryStream(conflicting), actual.VersionString, "test"));
            Reject(() => GameClient.ReadExecutable(new MemoryStream(bytes), "2026.11.01.0000.0000", "test"));
            Reject(() => GameClient.ReadExecutable(new MemoryStream(bytes.Take(600).ToArray()), actual.VersionString, "test"));
            File.WriteAllText(Path.Combine(folder, "遊戲版本讀取測試.txt"), "直接讀取本機 ffxiv_dx11.exe 得到建置 14101345；有效 PE、標記缺失、衝突、截斷與安裝版本不一致的檢查全部通過。未讀取任何回顧檔。");
        }
    }
}
