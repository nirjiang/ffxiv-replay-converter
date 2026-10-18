using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace ReplayConverter
{
    public static class HistoricalRoutes
    {
        public static string EvidenceLabel(VersionRoute route)
        {
            if (route.evidence == "recording") return "真實回顧回歸檢查；仍需確認個別回顧播放";
            if (route.evidence == "named") return "試轉換；" + route.namedChecks + " 種具名封包核對，未實播";
            if (route.evidence == "diff") return "試轉換；逐版二進位對照，未實播";
            return "既有建置對照；未標註歷史驗證範圍";
        }

        // Recording dates and guessed revisions are deliberately not identity inputs.
        public static VersionRoute Identify(byte[] data, int start, VersionRoute[] candidates, CancellationToken cancel)
        {
            if (candidates.Length == 0) throw new InvalidDataException("尚未收錄此來源版本的可辨識對照。原檔未變更。");
            var histogram = new Dictionary<ushort, HashSet<int>>();
            int pos = start, count = 0;
            while (pos < data.Length)
            {
                if ((count++ & 4095) == 0) cancel.ThrowIfCancellationRequested();
                if (data.Length - pos < 12) throw new InvalidDataException("回顧封包標頭不完整。");
                ushort opcode = BitConverter.ToUInt16(data, pos);
                int size = BitConverter.ToUInt16(data, pos + 2);
                if ((long)pos + 12 + size > data.Length) throw new InvalidDataException("回顧包含不完整的封包內容。");
                if (opcode != 0xF001 && opcode != 0xF002)
                {
                    if (!histogram.ContainsKey(opcode)) histogram[opcode] = new HashSet<int>();
                    histogram[opcode].Add(size);
                }
                pos += 12 + size;
            }
            if (histogram.Count < 20) throw new InvalidDataException("回顧的封包種類不足 20 種，無法可靠辨識較早版本；請使用未修改且錄製較完整的回顧。");
            var matches = new List<VersionRoute>();
            foreach (VersionRoute route in candidates)
            {
                cancel.ThrowIfCancellationRequested();
                int sizeChecks = 0; bool valid = true;
                foreach (var packet in histogram)
                {
                    string key = packet.Key.ToString(); int[] sizes;
                    if (!route.mapping.ContainsKey(key)) { valid = false; break; }
                    if (route.expectedSizes.TryGetValue(key, out sizes))
                    {
                        if (packet.Value.Any(s => !sizes.Contains(s))) { valid = false; break; }
                        sizeChecks++;
                    }
                }
                if (valid && sizeChecks >= 12) matches.Add(route);
            }
            if (matches.Count == 0) throw new InvalidDataException("來源封包或內容長度與已收錄對照不相容，無法可靠辨識／轉換；原檔未變更。");
            if (matches.Count != 1) throw new InvalidDataException("封包特徵同時符合多個遊戲版本，已停止轉換，避免誤判。");
            return matches[0];
        }

        // Carry new historical routes into a saved same-target or newer-target profile.
        public static Profile MergeEmbedded(Profile cached, Profile embedded)
        {
            Profile result = Updater.Json().Deserialize<Profile>(Updater.Json().Serialize(cached));
            VersionRoute bridge = result.targetBuild == embedded.targetBuild && result.targetVersion == embedded.targetVersion
                ? null : result.routes.FirstOrDefault(r => r.build == embedded.targetBuild && r.version == embedded.targetVersion);
            if (bridge == null && (result.targetBuild != embedded.targetBuild || result.targetVersion != embedded.targetVersion)) return cached;
            var routes = new List<VersionRoute>(result.routes);
            foreach (VersionRoute source in embedded.routes)
            {
                VersionRoute existing = routes.FirstOrDefault(r => r.version == source.version);
                // Preserve cache paths, but restore their evidence labels on app upgrade.
                if (existing != null)
                {
                    if (existing.evidence == null) { existing.evidence = source.evidence; existing.namedChecks = source.namedChecks; }
                    continue;
                }
                VersionRoute added = Updater.Json().Deserialize<VersionRoute>(Updater.Json().Serialize(source));
                if (bridge != null)
                {
                    added.mapping = source.mapping.Where(p => bridge.mapping.ContainsKey(p.Value.ToString()))
                        .ToDictionary(p => p.Key, p => bridge.mapping[p.Value.ToString()]);
                    if (!added.mapping.ContainsKey(source.initOpcode.ToString())) throw new InvalidDataException("新版快取缺少較早版本的場景轉換路徑。");
                    added.expectedSizes = source.expectedSizes.Where(p => added.mapping.ContainsKey(p.Key)).ToDictionary(p => p.Key, p => p.Value);
                    added.steps = source.steps.Concat(bridge.steps).ToArray(); added.sources = source.sources.Concat(bridge.sources).Distinct().ToArray();
                }
                result.gameVersions[source.version] = source.gameVersion; routes.Add(added);
            }
            result.routes = routes.OrderBy(r => r.version).ToArray(); result.supportedDescription = VersionNames.Supported(result);
            Profile.Validate(result); return result;
        }
    }
}
