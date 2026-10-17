using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace ReplayConverter
{
    public static class UpdateTests
    {
        static void Check(bool condition, string text) { if (!condition) throw new Exception(text); }
        static void Reject(Action action, string text)
        { try { action(); } catch (InvalidDataException) { return; } throw new Exception(text); }

        public static void Run(Profile current, string oldReplay, string gameManifest, string confirmedReplay, string folder)
        {
            Directory.CreateDirectory(folder);
            Profile baseline = Updater.Json().Deserialize<Profile>(Updater.Json().Serialize(current));
            VersionRoute last = current.routes.Single(r => r.build == 14006063);
            Dictionary<int, int> inverse = last.mapping.ToDictionary(p => p.Value, p => Int32.Parse(p.Key));
            baseline.targetGameVersion = last.gameVersion; baseline.targetBuild = last.build; baseline.targetVersion = last.version; baseline.targetInitOpcode = last.initOpcode;
            baseline.namedOpcodes = current.namedOpcodes.Where(p => inverse.ContainsKey(p.Value)).ToDictionary(p => p.Key, p => inverse[p.Value]);
            baseline.routes = current.routes.Where(r => r.build != last.build).Select(r => new VersionRoute {
                build = r.build, version = r.version, gameVersion = r.gameVersion, initOpcode = r.initOpcode,
                mapping = r.mapping.Where(p => inverse.ContainsKey(p.Value)).ToDictionary(p => p.Key, p => inverse[p.Value]),
                expectedSizes = r.expectedSizes, steps = r.steps.Take(r.steps.Length - 1).ToArray(), sources = r.sources.Take(r.sources.Length - 1).ToArray()
            }).ToArray();
            Profile.Validate(baseline);
            Updater.Save(baseline, Path.Combine(folder, "基準版本.json"));
            var passed = new List<string>();
            UpdatePlan plan = Updater.FetchPlan(baseline, current.targetVersion, CancellationToken.None);
            Check(plan.NamedChecks >= 20, "Insufficient independent named anchor checks.");
            Profile updated = Updater.BuildUpdatedProfile(baseline, plan, GameClient.Read(gameManifest), CancellationToken.None);
            Check(updated.targetBuild == current.targetBuild && updated.targetVersion == current.targetVersion, "Incorrect target identity.");
            foreach (VersionRoute route in current.routes)
            {
                VersionRoute generated = updated.routes.Single(r => r.build == route.build);
                Check(generated.mapping.Count == route.mapping.Count && route.mapping.All(p => generated.mapping[p.Key] == p.Value), "Old routes not retained.");
            }
            passed.Add("直接讀取遊戲主程式，真實連線更新 7.56（修正版）→ 7.57，核對 " + plan.NamedChecks + " 種具名封包，完整保留所有舊版路徑");
            byte[] expected = File.ReadAllBytes(confirmedReplay);
            Check(Engine.Prepare(File.ReadAllBytes(oldReplay), updated, CancellationToken.None).Data.SequenceEqual(expected), "Updated replay differs from the confirmed playable replay.");
            passed.Add("更新後產出與使用者已確認可播放的檔案逐位元組相同");
            string path = Path.Combine(folder, "版本資料", "profile.json"); Updater.Save(updated, path); Updater.Save(updated, path);
            Profile offline = Updater.Json().Deserialize<Profile>(File.ReadAllText(path)); Profile.Validate(offline);
            Check(Engine.Prepare(File.ReadAllBytes(oldReplay), offline, CancellationToken.None).Data.SequenceEqual(expected), "Offline cache conversion failed.");
            passed.Add("原子保存與覆寫更新資料成功，重新載入後離線轉換結果一致");
            var catalog = new CatalogVersion[] {
                new CatalogVersion { retail_version = "7.57", version_string = current.targetVersion },
                new CatalogVersion { retail_version = "7.58", version_string = "2026.11.01.0000.0000" },
                new CatalogVersion { retail_version = "7.60", version_string = "2027.01.01.0000.0000" }
            };
            Check(Updater.SelectTransitions(catalog, current.targetVersion, "2026.11.01.0000.0000").Length == 1, "Minor update path failed.");
            Reject(() => Updater.SelectTransitions(catalog, current.targetVersion, "2027.01.01.0000.0000"), "Major update accepted without layout support.");
            Reject(() => Updater.SelectTransitions(catalog, current.targetVersion, "2028.01.01.0000.0000"), "Unpublished update accepted.");
            Reject(() => Updater.ParseDiff("[{\"old\":[\"0x1\"],\"new\":[\"0x2\"]},{\"old\":[\"0x3\"],\"new\":[\"0x2\"]}]"), "Duplicate target accepted.");
            Check(Updater.ParseDiff("[{\"old\":[\"0x1\"],\"new\":[\"0x2\",\"0x3\"]}]").Count == 0, "Ambiguous split guessed.");
            var conflict = new Dictionary<string, int>(plan.NamedOpcodes); conflict["InitZone"] = 0;
            Reject(() => Updater.CheckNamedAnchors(baseline.namedOpcodes, conflict, plan.Mapping), "Conflicting named table accepted.");
            string older = GameClientTests.Fixture(Path.Combine(folder, "old-game"), baseline.targetVersion, baseline.targetBuild);
            Reject(() => Updater.BuildUpdatedProfile(baseline, plan, GameClient.Read(older), CancellationToken.None), "Old executable accepted as new game build.");
            GameClientTests.Run(gameManifest, Path.Combine(folder, "game-reader"));
            passed.Add("拒絕尚未發布、跨主要版本、重複或拆分對照、獨立資料衝突與錯誤的遊戲主程式；整個更新流程未讀取新版回顧");
            File.WriteAllText(Path.Combine(folder, "更新功能測試.json"), Updater.Json().Serialize(passed));
        }
    }
}
