using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace ReplayConverter
{
    public sealed class CatalogVersion
    {
        public string retail_version { get; set; }
        public string version_string { get; set; }
    }
    public sealed class OpcodeEntry
    {
        public string name { get; set; }
        public int opcode { get; set; }
    }
    public sealed class NamedTable
    {
        public string region { get; set; }
        public string version { get; set; }
        public Dictionary<string, OpcodeEntry[]> lists { get; set; }
    }
    public sealed class DiffEntry
    {
        public string[] old { get; set; }
        public string[] @new { get; set; }
    }
    public sealed class CommitInfo { public string sha { get; set; } }

    public sealed class UpdatePlan
    {
        public string TargetVersion;
        public string TargetGameVersion;
        public Dictionary<string, string> GameVersions;
        public ushort InitOpcode;
        public Dictionary<string, int> Mapping;
        public Dictionary<string, int> NamedOpcodes;
        public string[] Steps;
        public string[] Sources;
        public int NamedChecks;
    }

    public static class Updater
    {
        public static JavaScriptSerializer Json()
        { return new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 }; }

        static InvalidDataException Fail(string message) { return new InvalidDataException(message); }

        public static string Download(string url, CancellationToken cancel)
        {
            Uri uri = new Uri(url);
            if (uri.Scheme != "https" || (uri.Host != "api.github.com" && uri.Host != "raw.githubusercontent.com"))
                throw Fail("更新來源不在允許的公開資料站內。");
            cancel.ThrowIfCancellationRequested();
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var request = (HttpWebRequest)WebRequest.Create(uri);
            request.UserAgent = "FF14ReplayConverter/1.3";
            request.Timeout = 15000; request.ReadWriteTimeout = 15000;
            request.AllowAutoRedirect = false;
            try
            {
            using (cancel.Register(request.Abort))
            using (var response = (HttpWebResponse)request.GetResponse())
            {
                if (response.StatusCode != HttpStatusCode.OK) throw Fail("公開版本資料尚未發布或暫時無法存取。");
                if (response.ContentLength > 4 * 1024 * 1024) throw Fail("更新資料超過大小上限。");
                using (Stream stream = response.GetResponseStream())
                using (var output = new MemoryStream())
                {
                    byte[] buffer = new byte[16384]; int count;
                    while ((count = stream.Read(buffer, 0, buffer.Length)) != 0)
                    {
                        cancel.ThrowIfCancellationRequested();
                        if (output.Length + count > 4 * 1024 * 1024) throw Fail("更新資料超過大小上限。");
                        output.Write(buffer, 0, count);
                    }
                    return Encoding.UTF8.GetString(output.ToArray());
                }
            }
            }
            catch (WebException ex)
            {
                if (cancel.IsCancellationRequested) throw new OperationCanceledException(cancel);
                var failed = ex.Response as HttpWebResponse;
                int status = failed == null ? 0 : (int)failed.StatusCode;
                if (failed != null) failed.Close();
                if (status == 404) throw Fail("對應的公開版本資料尚未發布，請稍後再更新。");
                if (status == 403 || status == 429) throw Fail("公開資料站暫時限制查詢，請稍後再更新。");
                throw Fail("目前無法下載版本資料，請確認網路連線後再試。原版本資料已保留。");
            }
        }

        static string Revision(string repository, string branch, CancellationToken cancel)
        {
            string sha = Json().Deserialize<CommitInfo>(Download("https://api.github.com/repos/" + repository + "/commits/" + branch, cancel)).sha;
            if (sha == null || !Regex.IsMatch(sha, "^[a-fA-F0-9]{40}$")) throw Fail("公開資料的版本識別無效。");
            return sha;
        }

        static string Series(string version)
        {
            Match m = Regex.Match(version ?? "", "^([0-9]+)\\.([0-9])");
            if (!m.Success) throw Fail("無法辨識公開遊戲版本。");
            return m.Groups[1].Value + "." + m.Groups[2].Value;
        }

        public static UpdatePlan FetchPlan(Profile profile, string localVersion, CancellationToken cancel)
        {
            string diffRevision = Revision("xivdev/opcodediff", "main", cancel);
            string diffBase = "https://raw.githubusercontent.com/xivdev/opcodediff/" + diffRevision + "/";
            var catalog = Json().Deserialize<CatalogVersion[]>(Download(diffBase + "automation/ffxiv_versions_global.json", cancel));
            CatalogVersion[] transitions = SelectTransitions(catalog, profile.targetVersion, localVersion);

            var combined = new Dictionary<string, int>();
            var steps = new List<string>(); var sources = new List<string>();
            foreach (CatalogVersion transition in transitions)
            {
                string version = transition.retail_version;
                if (!Regex.IsMatch(version ?? "", "^[0-9]+\\.[0-9]+(?:h[0-9]*|a)?$")) throw Fail("公開差異檔的版本名稱無效。");
                string url = diffBase + "diffs/" + version + ".diff.json";
                Dictionary<string, int> next = ParseDiff(Download(url, cancel));
                if (steps.Count == 0) combined = next;
                else combined = combined.Where(p => next.ContainsKey(p.Value.ToString())).ToDictionary(p => p.Key, p => next[p.Value.ToString()]);
                steps.Add(version); sources.Add(url);
            }
            if (combined.Count < 20) throw Fail("公開對照資料不完整，無法建立可靠的轉換路徑。");

            string namedRevision = Revision("karashiiro/FFXIVOpcodes", "master", cancel);
            string namedUrl = "https://raw.githubusercontent.com/karashiiro/FFXIVOpcodes/" + namedRevision + "/opcodes.min.json";
            var tables = Json().Deserialize<NamedTable[]>(Download(namedUrl, cancel));
            NamedTable named = tables.FirstOrDefault(t => t.region == "Global" && String.Equals(t.version, transitions.Last().retail_version, StringComparison.OrdinalIgnoreCase));
            if (named == null || !named.lists.ContainsKey("ServerZoneIpcType"))
                throw Fail("新版具名封包表尚未同步，請稍後再更新。");
            Dictionary<string, int> names = named.lists["ServerZoneIpcType"].ToDictionary(e => e.name, e => e.opcode);
            int init;
            if (!names.TryGetValue("InitZone", out init) || init < 0 || init > 65535) throw Fail("新版場景載入封包資料缺失。");
            int checks = CheckNamedAnchors(profile.namedOpcodes, names, combined);
            int mappedInit;
            if (!combined.TryGetValue(profile.targetInitOpcode.ToString(), out mappedInit) || mappedInit != init)
                throw Fail("新版場景載入對照與獨立封包表不一致。");
            sources.Add(namedUrl);
            return new UpdatePlan { TargetVersion = localVersion, TargetGameVersion = transitions.Last().retail_version,
                GameVersions = catalog.ToDictionary(v => v.version_string, v => v.retail_version), InitOpcode = (ushort)init, Mapping = combined,
                NamedOpcodes = names, Steps = steps.ToArray(), Sources = sources.ToArray(), NamedChecks = checks };
        }

        public static CatalogVersion[] SelectTransitions(CatalogVersion[] catalog, string oldVersion, string newVersion)
        {
            if (catalog == null || catalog.Length < 2 || catalog.Any(v => v == null || !Regex.IsMatch(v.version_string ?? "", "^[0-9]{4}\\.[0-9]{2}\\.[0-9]{2}\\.[0-9]{4}\\.[0-9]{4}$"))
                || catalog.Select(v => v.version_string).Distinct().Count() != catalog.Length)
                throw Fail("公開版本目錄格式無效。");
            for (int i = 1; i < catalog.Length; i++) if (String.CompareOrdinal(catalog[i - 1].version_string, catalog[i].version_string) >= 0) throw Fail("公開版本目錄順序無效。");
            int source = Array.FindIndex(catalog, v => v.version_string == oldVersion), target = Array.FindIndex(catalog, v => v.version_string == newVersion);
            if (source < 0) throw Fail("公開資料尚未收錄工具目前支援的基準版本。");
            if (target < 0) throw Fail("本機遊戲版本的公開封包對照尚未發布。可稍後再按「更新版本資料」。");
            if (target <= source) throw Fail(target == source ? "目前遊戲版本已經有可用資料，不需要更新。" : "本機遊戲比工具的版本資料舊，請確認遊戲安裝位置。");
            CatalogVersion[] result = catalog.Skip(source + 1).Take(target - source).ToArray();
            if (result.Any(v => Series(v.retail_version) != Series(catalog[source].retail_version)))
                throw Fail("這次更新跨越主要版本，可能需要封包內容轉換，請取得支援該版本的新程式。");
            return result;
        }

        public static Dictionary<string, int> ParseDiff(string json)
        {
            var entries = Json().Deserialize<DiffEntry[]>(json);
            var result = new Dictionary<string, int>();
            var targets = new HashSet<int>(); var allOld = new HashSet<int>(); var allNew = new HashSet<int>();
            foreach (DiffEntry entry in entries)
            {
                if (entry.old == null || entry.@new == null) throw Fail("封包差異檔格式無效。");
                foreach (string oldValue in entry.old)
                    if (!allOld.Add(Hex(oldValue))) throw Fail("封包差異檔含有重複來源編號。");
                foreach (string newValue in entry.@new)
                    if (!allNew.Add(Hex(newValue))) throw Fail("封包差異檔含有重複目的編號。");
                if (entry.old.Length != 1 || entry.@new.Length != 1) continue;
                int source = Hex(entry.old[0]), target = Hex(entry.@new[0]);
                if (!targets.Add(target)) throw Fail("封包差異檔不是一對一對照。");
                result.Add(source.ToString(), target);
            }
            return result;
        }

        static int Hex(string value)
        {
            if (!Regex.IsMatch(value ?? "", "^0x[0-9A-Fa-f]{1,4}$")) throw Fail("封包編號格式無效。");
            return Convert.ToInt32(value.Substring(2), 16);
        }

        public static int CheckNamedAnchors(Dictionary<string, int> oldNames, Dictionary<string, int> newNames, Dictionary<string, int> mapping)
        {
            if (oldNames == null || newNames == null) throw Fail("缺少獨立封包表，不能驗證更新。");
            int checks = 0;
            foreach (var old in oldNames)
            {
                int target, expected;
                if (!newNames.TryGetValue(old.Key, out expected) || !mapping.TryGetValue(old.Value.ToString(), out target)) continue;
                if (target != expected) throw Fail("封包 " + old.Key + " 的版本對照與獨立資料不一致。");
                checks++;
            }
            if (checks < 20) throw Fail("新版對照的可核對封包不足 20 種，暫不能更新。");
            return checks;
        }

        public static Profile BuildUpdatedProfile(Profile old, UpdatePlan plan, GameIdentity game, CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            if (game.VersionString != plan.TargetVersion || game.Build <= old.targetBuild)
                throw Fail("本機主程式建置與新的版本資料不一致，請先完成遊戲更新。");
            if (plan.NamedChecks < 20 || CheckNamedAnchors(old.namedOpcodes, plan.NamedOpcodes, plan.Mapping) < 20)
                throw Fail("新版對照資料尚未通過獨立封包表核對。");
            Profile updated = Json().Deserialize<Profile>(Json().Serialize(old));
            updated.targetBuild = game.Build; updated.targetVersion = game.VersionString;
            updated.targetGameVersion = plan.TargetGameVersion; updated.gameVersions = plan.GameVersions;
            updated.targetInitOpcode = plan.InitOpcode;
            var oldTargetSizes = new Dictionary<int, HashSet<int>>();
            foreach (VersionRoute route in old.routes)
                foreach (var sizes in route.expectedSizes)
                {
                    int target = route.mapping[sizes.Key];
                    if (!oldTargetSizes.ContainsKey(target)) oldTargetSizes[target] = new HashSet<int>();
                    oldTargetSizes[target].UnionWith(sizes.Value);
                }
            var routes = new List<VersionRoute>();
            foreach (VersionRoute route in old.routes)
            {
                Dictionary<string, int> mapped = route.mapping.Where(p => plan.Mapping.ContainsKey(p.Value.ToString())).ToDictionary(p => p.Key, p => plan.Mapping[p.Value.ToString()]);
                if (!mapped.ContainsKey(route.initOpcode.ToString())) throw Fail("舊版的場景載入轉換路徑在新版中缺失。");
                routes.Add(new VersionRoute { build = route.build, version = route.version, gameVersion = route.gameVersion,
                    initOpcode = route.initOpcode, mapping = mapped,
                    expectedSizes = route.expectedSizes.Where(p => mapped.ContainsKey(p.Key)).ToDictionary(p => p.Key, p => p.Value),
                    steps = route.steps.Concat(plan.Steps).ToArray(), sources = route.sources.Concat(plan.Sources).Distinct().ToArray() });
            }
            var previousSizes = oldTargetSizes.Where(p => plan.Mapping.ContainsKey(p.Key.ToString())).ToDictionary(p => p.Key.ToString(), p => p.Value.ToArray());
            routes.Add(new VersionRoute { build = old.targetBuild, version = old.targetVersion, gameVersion = old.targetGameVersion,
                initOpcode = old.targetInitOpcode, mapping = plan.Mapping, expectedSizes = previousSizes, steps = plan.Steps, sources = plan.Sources });
            updated.routes = routes.ToArray(); updated.namedOpcodes = plan.NamedOpcodes;
            updated.supportedDescription = VersionNames.Supported(updated);
            updated.updateSources = plan.Sources; updated.gameExecutableSha256 = GameClient.HashExecutable(game);
            Profile.Validate(updated);
            return updated;
        }

        public static void Save(Profile profile, string path)
        {
            Profile.Validate(profile);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                byte[] data = new UTF8Encoding(true).GetBytes(Json().Serialize(profile));
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(data, 0, data.Length); stream.Flush(true); }
                Profile reloaded = Json().Deserialize<Profile>(File.ReadAllText(temporary)); Profile.Validate(reloaded);
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
