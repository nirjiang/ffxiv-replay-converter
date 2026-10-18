using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace ReplayConverter
{
    public static class HistoricalTests
    {
        static void Check(bool valid, string message) { if (!valid) throw new Exception(message); }
        static void Reject(Action action, string message)
        { try { action(); } catch (InvalidDataException) { return; } throw new Exception(message); }
        static Profile Clone(Profile p) { return Updater.Json().Deserialize<Profile>(Updater.Json().Serialize(p)); }

        // Artificial format-5 data exercises identification and preservation, not gameplay compatibility.
        public static byte[] Fixture(VersionRoute route, int limit, bool uncheckedSizes)
        {
            var entries = route.mapping.Keys.Where(k => k != route.initOpcode.ToString()
                && (uncheckedSizes ? !route.expectedSizes.ContainsKey(k) : route.expectedSizes.ContainsKey(k)))
                .OrderBy(k => Int32.Parse(k)).Take(limit).ToList();
            entries.Insert(0, route.initOpcode.ToString());
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(new byte[0x36C]);
                foreach (string key in entries)
                {
                    int opcode = Int32.Parse(key), size = opcode == route.initOpcode ? 136 : (uncheckedSizes ? 0 : route.expectedSizes[key][0]);
                    writer.Write((ushort)opcode); writer.Write((ushort)size); writer.Write((uint)0); writer.Write((uint)0);
                    writer.Write(Enumerable.Repeat((byte)0x5A, size).ToArray());
                }
                writer.Write((ushort)0xF001); writer.Write((ushort)4); writer.Write((uint)0); writer.Write((uint)0); writer.Write((uint)0x12345678);
                byte[] result = stream.ToArray();
                Buffer.BlockCopy(System.Text.Encoding.ASCII.GetBytes("FFXIVREPLAY\0"), 0, result, 0, 12);
                Buffer.BlockCopy(BitConverter.GetBytes((ushort)5), 0, result, 12, 2);
                Buffer.BlockCopy(BitConverter.GetBytes((ushort)3), 0, result, 14, 2);
                Buffer.BlockCopy(BitConverter.GetBytes(route.build == 0 ? (uint)1 : route.build), 0, result, 16, 4);
                Buffer.BlockCopy(BitConverter.GetBytes((uint)0x304), 0, result, 0x44, 4);
                Buffer.BlockCopy(BitConverter.GetBytes((uint)(result.Length - 0x36C)), 0, result, 0x48, 4);
                Buffer.BlockCopy(BitConverter.GetBytes((uint)1), 0, result, 0x68, 4);
                return result;
            }
        }

        public static void Run(Profile profile, string folder)
        {
            Directory.CreateDirectory(folder);
            var results = new List<string>();
            Check(profile.routes.Length == 45, "Historical source count changed; review coverage assertions.");
            foreach (VersionRoute route in profile.routes)
            {
                byte[] original = Fixture(route, 200, false);
                byte[] before = (byte[])original.Clone();
                PreparedReplay prepared = Engine.Prepare(original, profile, CancellationToken.None);
                Check(prepared.SourceVersion == VersionNames.Display(route.gameVersion), "Wrong historical source: " + route.gameVersion);
                Check(prepared.FingerprintIdentified == (route.build == 0), "Wrong identity mode.");
                Check(original.SequenceEqual(before), "Synthetic source mutated.");
                Check(BitConverter.ToUInt32(prepared.Data, 16) == profile.targetBuild, "Incorrect target build.");
                for (int pos = 0x36C; pos < original.Length;)
                {
                    ushort opcode = BitConverter.ToUInt16(original, pos);
                    int size = BitConverter.ToUInt16(original, pos + 2);
                    int target = opcode == 0xF001 ? opcode : route.mapping[opcode.ToString()];
                    Check(BitConverter.ToUInt16(prepared.Data, pos) == target, "Opcode write mismatch.");
                    for (int i = pos + 2; i < pos + 12 + size; i++) Check(original[i] == prepared.Data[i], "Payload changed.");
                    pos += 12 + size;
                }
                Check(Engine.Prepare(prepared.Data, profile, CancellationToken.None).AlreadyCurrent, "Double conversion accepted.");
                results.Add(VersionNames.Display(route.gameVersion) + "：合成資料辨識與原內容保留檢查通過（不代表實播驗證）");
            }
            VersionRoute unknown = profile.routes.First(r => r.build == 0);
            Reject(() => Engine.Prepare(Fixture(unknown, 1, false), profile, CancellationToken.None), "Short recording was guessed.");
            Reject(() => Engine.Prepare(Fixture(unknown, 30, true), profile, CancellationToken.None), "Insufficient payload anchors accepted.");
            byte[] malformed = Fixture(unknown, 200, false);
            malformed[0x36E] = 135;
            Reject(() => Engine.Prepare(malformed, profile, CancellationToken.None), "Incompatible init size accepted.");
            byte[] truncated = Fixture(unknown, 200, false);
            Reject(() => Engine.Prepare(truncated.Take(truncated.Length - 1).ToArray(), profile, CancellationToken.None), "Truncation accepted.");
            Profile ambiguity = Clone(profile);
            VersionRoute other = Updater.Json().Deserialize<VersionRoute>(Updater.Json().Serialize(unknown));
            VersionRoute label = profile.routes.First(r => r.version != unknown.version);
            other.version = label.version; other.gameVersion = label.gameVersion;
            string[] unobserved = other.mapping.Keys.Where(k => !other.expectedSizes.ContainsKey(k)).Take(2).ToArray();
            int temp = other.mapping[unobserved[0]]; other.mapping[unobserved[0]] = other.mapping[unobserved[1]]; other.mapping[unobserved[1]] = temp;
            ambiguity.routes = new VersionRoute[] { unknown, other }; Profile.Validate(ambiguity);
            Reject(() => Engine.Prepare(Fixture(unknown, 200, false), ambiguity, CancellationToken.None), "Ambiguous fingerprint was guessed.");
            Profile legacy = Clone(profile); legacy.routes = legacy.routes.Where(r => r.build != 0 && r.build != 13733124).ToArray();
            foreach (VersionRoute r in legacy.routes) r.evidence = null;
            Profile.Validate(legacy);
            Profile merged = HistoricalRoutes.MergeEmbedded(legacy, profile);
            Check(merged.routes.Length == 45 && merged.routes.All(r => r.evidence != null), "Legacy cache hid new historical support.");
            var plan = new UpdatePlan { TargetVersion = "2026.11.01.0000.0000", TargetGameVersion = "7.58",
                GameVersions = new Dictionary<string, string>(profile.gameVersions), InitOpcode = (ushort)(profile.targetInitOpcode + 1),
                NamedOpcodes = profile.namedOpcodes.ToDictionary(p => p.Key, p => p.Value + 1),
                Mapping = profile.routes.SelectMany(r => r.mapping.Values).Distinct().ToDictionary(v => v.ToString(), v => v + 1),
                NamedChecks = 170, Steps = new string[] { "synthetic" }, Sources = new string[] { "synthetic-test-only" } };
            plan.GameVersions[plan.TargetVersion] = plan.TargetGameVersion;
            string game = GameClientTests.Fixture(Path.Combine(folder, "future-game"), plan.TargetVersion, profile.targetBuild + 1);
            Profile future = Updater.BuildUpdatedProfile(profile, plan, GameClient.Read(game), CancellationToken.None);
            Profile legacyFuture = Updater.BuildUpdatedProfile(legacy, plan, GameClient.Read(game), CancellationToken.None);
            Profile mergedFuture = HistoricalRoutes.MergeEmbedded(legacyFuture, profile);
            Check(future.routes.Length == 46 && mergedFuture.routes.Length == 46, "Future update lost historical routes.");
            foreach (VersionRoute r in future.routes)
            {
                VersionRoute actual = mergedFuture.routes.Single(x => x.version == r.version);
                Check(r.mapping.Count == actual.mapping.Count && r.mapping.All(p => actual.mapping[p.Key] == p.Value), "Bridge composition mismatch.");
            }
            results.Add("拒絕過短回顧、內容長度不相容、截斷、封包特徵衝突；舊版快取合併及未來更新路徑保留檢查通過。");
            File.WriteAllText(Path.Combine(folder, "歷史版本測試.json"), Updater.Json().Serialize(results));
        }
    }
}
