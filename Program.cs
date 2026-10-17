using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: AssemblyTitle("FF14 回顧轉換工具")]
[assembly: AssemblyProduct("FF14 回顧轉換工具")]
[assembly: AssemblyVersion("1.3.0.0")]
[assembly: AssemblyFileVersion("1.3.0.0")]

namespace ReplayConverter
{
    public sealed class VersionRoute
    {
        public uint build { get; set; }
        public string version { get; set; }
        public string gameVersion { get; set; }
        public ushort initOpcode { get; set; }
        public Dictionary<string, int> mapping { get; set; }
        public Dictionary<string, int[]> expectedSizes { get; set; }
        public string[] steps { get; set; }
        public string[] sources { get; set; }
    }

    public sealed class Profile
    {
        public int formatVersion { get; set; }
        public int operatingSystem { get; set; }
        public uint sourceBuild { get; set; }
        public uint targetBuild { get; set; }
        public string sourceVersion { get; set; }
        public string targetVersion { get; set; }
        public string targetGameVersion { get; set; }
        public Dictionary<string, string> gameVersions { get; set; }
        public ushort sourceInitOpcode { get; set; }
        public ushort targetInitOpcode { get; set; }
        public string diffSource { get; set; }
        public Dictionary<string, int> mapping { get; set; }
        public Dictionary<string, int> verifiedSizes { get; set; }
        public VersionRoute[] routes { get; set; }
        public string supportedDescription { get; set; }
        public Dictionary<string, int> namedOpcodes { get; set; }
        public string[] updateSources { get; set; }
        public string gameExecutableSha256 { get; set; }

        public static string CachePath { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "版本資料", "profile.json"); } }

        public static void Validate(Profile p)
        {
            if (p == null || p.formatVersion != 5 || p.operatingSystem != 3 || p.targetBuild == 0
                || !System.Text.RegularExpressions.Regex.IsMatch(p.targetVersion ?? "", "^[0-9]{4}\\.[0-9]{2}\\.[0-9]{2}\\.[0-9]{4}\\.[0-9]{4}$")
                || p.gameVersions == null || !p.gameVersions.ContainsKey(p.targetVersion) || p.gameVersions[p.targetVersion] != p.targetGameVersion
                || !System.Text.RegularExpressions.Regex.IsMatch(p.targetGameVersion ?? "", "^[0-9]+\\.[0-9]+(?:h[0-9]*|a)?$")
                || p.namedOpcodes == null || p.namedOpcodes.Count < 20 || p.namedOpcodes.Values.Any(v => v < 0 || v > 65535)
                || p.routes == null || p.routes.Length == 0 || p.routes.Length > 100 || p.routes.Any(r => r == null) || p.routes.Select(r => r.build).Distinct().Count() != p.routes.Length)
                throw new InvalidDataException("版本資料不完整。");
            int namedInit;
            if (!p.namedOpcodes.TryGetValue("InitZone", out namedInit) || namedInit != p.targetInitOpcode) throw new InvalidDataException("場景載入版本資料不一致。");
            foreach (VersionRoute r in p.routes)
            {
                int init;
                if (r.build == 0 || r.build == p.targetBuild || r.version == null || !p.gameVersions.ContainsKey(r.version) || p.gameVersions[r.version] != r.gameVersion
                    || r.mapping == null || r.mapping.Count < 20 || r.expectedSizes == null
                    || r.steps == null || r.sources == null || r.mapping.Values.Any(v => v < 0 || v > 65535)
                    || r.mapping.Keys.Any(k => { int n; return !Int32.TryParse(k, out n) || n < 0 || n > 65535; })
                    || r.mapping.Values.Distinct().Count() != r.mapping.Count || !r.mapping.TryGetValue(r.initOpcode.ToString(), out init) || init != p.targetInitOpcode
                    || r.expectedSizes.Any(s => !r.mapping.ContainsKey(s.Key) || s.Value == null || s.Value.Length == 0 || s.Value.Any(v => v < 0 || v > 65535)))
                    throw new InvalidDataException("版本對照資料無效或不是一對一。");
            }
        }

        public static Profile Load()
        {
            Profile embedded = LoadEmbedded();
            try
            {
                if (File.Exists(CachePath) && new FileInfo(CachePath).Length <= 4 * 1024 * 1024)
                {
                    Profile cached = Updater.Json().Deserialize<Profile>(File.ReadAllText(CachePath)); Validate(cached);
                    if (String.CompareOrdinal(cached.targetVersion, embedded.targetVersion) >= 0) return cached;
                }
            }
            catch (InvalidDataException) { } catch (IOException) { } catch (UnauthorizedAccessException) { } catch (ArgumentException) { } catch (InvalidOperationException) { }
            return embedded;
        }

        public static Profile LoadEmbedded()
        {
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("ConverterProfile.json"))
            using (StreamReader reader = new StreamReader(s, Encoding.UTF8))
            {
                Profile p = Updater.Json().Deserialize<Profile>(reader.ReadToEnd()); Validate(p);
                return p;
            }
        }
    }

    public sealed class PreparedReplay
    {
        public byte[] Data;
        public bool AlreadyCurrent;
        public bool RepairedHeader;
        public int PacketCount;
        public int OpcodeCount;
        public bool NoRecording;
        public string SourceVersion;
    }

    public sealed class ConvertResult
    {
        public string Source { get; set; }
        public string Output { get; set; }
        public string Status { get; set; }
        public string Detail { get; set; }
        public int Packets { get; set; }
        public string Sha256 { get; set; }
        public string SourceVersion { get; set; }
    }

    public sealed class ImportInput
    {
        public string Container;
        public int EntryIndex = -1;
        public string EntryName;
        public string Error;
        public string FileName { get {
            if (EntryIndex < 0) return Path.GetFileName(Container);
            string name = EntryName.Replace('\\', '/').Split('/').Last();
            char[] invalid = Path.GetInvalidFileNameChars();
            return new String(name.Select(c => invalid.Contains(c) || c < 32 ? '_' : c).ToArray());
        } }
        public string DisplayName { get { return EntryIndex < 0 ? FileName : Path.GetFileName(Container) + " / " + FileName; } }
        public string SourceLabel { get { return EntryIndex < 0 ? Container : Container + "::[" + EntryIndex + "]" + EntryName; } }
    }

    public static class Engine
    {
        static readonly byte[] Magic = Encoding.ASCII.GetBytes("FFXIVREPLAY\0");
        public const long MaxFileBytes = 512L * 1024 * 1024;

        static InvalidDataException Bad(string text) { return new InvalidDataException(text); }
        static ushort U16(byte[] b, int p) { return BitConverter.ToUInt16(b, p); }
        static uint U32(byte[] b, int p) { return BitConverter.ToUInt32(b, p); }
        static void Put16(byte[] b, int p, ushort value)
        { b[p] = (byte)value; b[p + 1] = (byte)(value >> 8); }

        public static PreparedReplay Prepare(byte[] original, Profile profile, CancellationToken cancel)
        {
            if (original.Length < 0x6C || !Magic.SequenceEqual(original.Take(12)))
                throw Bad("這不是有效的 FF14 任務回顧檔。");
            if (U16(original, 0x0C) != profile.formatVersion || U16(original, 0x0E) != profile.operatingSystem)
                throw Bad("尚未支援這個回顧格式或平台，原檔未變更。");
            uint build = U32(original, 0x10);
            if (U32(original, 0x48) == 0 && U32(original, 0x18) == 0 && U32(original, 0x68) == 0)
                return new PreparedReplay { Data = original, NoRecording = true };

            long chapterSize = U32(original, 0x44);
            long packetBytes = U32(original, 0x48);
            long packetStart = 0x68L + chapterSize;
            // Format 5 reserves 64 chapter slots. Chapters are outside the converted packet stream.
            if (chapterSize != 0x304 || packetBytes < 12 || packetStart + packetBytes != original.LongLength)
                throw Bad("回顧檔的長度或章節區不完整，無法轉換。");
            uint storedChapterCount = U32(original, 0x68);
            if (storedChapterCount > 64) throw Bad("回顧章節數超過格式上限。");
            int chapterCount = (int)storedChapterCount;
            if (chapterCount > 64) throw Bad("回顧章節數超過格式上限。");

            int start = checked((int)packetStart);
            ushort firstOpcode = U16(original, start);
            if (U16(original, start + 2) != 136 || U32(original, start + 4) != 0)
                throw Bad("找不到預期的場景載入資料；此回顧尚無法可靠轉換。");
            bool current = build == profile.targetBuild && firstOpcode == profile.targetInitOpcode;
            VersionRoute route = profile.routes.FirstOrDefault(r => r.build == build);
            bool repair = false;
            if (build == profile.targetBuild && !current)
            {
                VersionRoute[] matches = profile.routes.Where(r => r.initOpcode == firstOpcode).ToArray();
                if (matches.Length == 1) { route = matches[0]; repair = true; }
            }
            if (!current && route == null)
                throw Bad("尚未收錄這個舊版本（建置 " + build + "）。原檔未變更。");
            if (!current && firstOpcode != route.initOpcode)
                throw Bad("版本與場景資料不一致，請匯入未修改的舊回顧檔。");

            byte[] data = current ? original : (byte[])original.Clone();
            var opcodes = new HashSet<ushort>();
            var boundaries = new HashSet<int>();
            int packets = 0;
            int pos = start;
            while (pos < original.Length)
            {
                if ((packets & 4095) == 0) cancel.ThrowIfCancellationRequested();
                if (original.Length - pos < 12) throw Bad("回顧在封包標頭處中斷。");
                ushort opcode = U16(original, pos);
                int size = U16(original, pos + 2);
                long end = (long)pos + 12 + size;
                if (end > original.Length) throw Bad("回顧包含不完整的封包內容。");
                boundaries.Add(pos - start);
                opcodes.Add(opcode);
                if (!current)
                {
                    int target;
                    if (opcode == 0xF001 || opcode == 0xF002) target = opcode; // A Realm Recorded custom packets.
                    else if (!route.mapping.TryGetValue(opcode.ToString(), out target))
                        throw Bad("尚無封包 0x" + opcode.ToString("X4") + " 的可靠對照資料，已停止轉換。");
                    int[] expectedSizes;
                    if (route.expectedSizes.TryGetValue(opcode.ToString(), out expectedSizes) && !expectedSizes.Contains(size))
                        throw Bad("封包內容長度與已驗證版本不符，已停止轉換。");
                    if (target < 0 || target > UInt16.MaxValue) throw Bad("內建版本資料無效。");
                    Put16(data, pos, (ushort)target);
                }
                pos = (int)end;
                packets++;
            }
            for (int chapter = 0; chapter < chapterCount; chapter++)
            {
                uint offset = U32(original, 0x70 + chapter * 12);
                if (offset > packetBytes || (offset != packetBytes && !boundaries.Contains((int)offset)))
                    throw Bad("章節位置不在完整封包邊界上，已停止轉換。");
            }
            if (!current)
            {
                Buffer.BlockCopy(BitConverter.GetBytes(profile.targetBuild), 0, data, 0x10, 4);
                // Verify every untouched field and payload against the source before writing anything.
                for (int i = 0; i < start; i++)
                    if ((i < 0x10 || i >= 0x14) && data[i] != original[i]) throw Bad("檔頭驗證失敗。");
                pos = start;
                while (pos < data.Length)
                {
                    int end = pos + 12 + U16(original, pos + 2);
                    for (int i = pos + 2; i < end; i++)
                        if (data[i] != original[i]) throw Bad("戰鬥資料驗證失敗。");
                    pos = end;
                }
            }
            return new PreparedReplay { Data = data, AlreadyCurrent = current, RepairedHeader = repair,
                PacketCount = packets, OpcodeCount = opcodes.Count, SourceVersion = VersionNames.Display(current ? profile.targetGameVersion : route.gameVersion) };
        }

        public static List<ImportInput> ExpandInputs(IEnumerable<string> paths)
        {
            var inputs = new List<ImportInput>();
            foreach (string path in paths)
            {
                bool archive = path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".zip.backup", StringComparison.OrdinalIgnoreCase);
                if (!archive) { inputs.Add(new ImportInput { Container = path }); continue; }
                try
                {
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
                    {
                        var entries = new List<ImportInput>();
                        for (int index = 0; index < zip.Entries.Count; index++)
                        {
                            ZipArchiveEntry entry = zip.Entries[index];
                            if (!entry.FullName.EndsWith(".dat", StringComparison.OrdinalIgnoreCase)) continue;
                            entries.Add(new ImportInput { Container = path, EntryIndex = index, EntryName = entry.FullName });
                            if (entries.Count > 2048) throw Bad("單個 ZIP 超過 2048 份回顧，請分批匯入。");
                        }
                        if (entries.Count == 0) throw Bad("ZIP 中沒有 .dat 任務回顧檔。");
                        inputs.AddRange(entries);
                    }
                }
                catch (Exception ex) { inputs.Add(new ImportInput { Container = path, Error = "無法匯入備份：" + ex.Message }); }
            }
            return inputs;
        }

        static byte[] ReadData(Stream stream, long length)
        {
            if (length > MaxFileBytes || length < 0) throw Bad("此回顧超過 512 MB，這一版工具暫不支援。");
            byte[] data = new byte[checked((int)length)];
            int read = 0;
            while (read < data.Length)
            {
                int count = stream.Read(data, read, data.Length - read);
                if (count == 0) throw Bad("回顧檔讀取不完整。");
                read += count;
            }
            return data;
        }

        public static void ValidateGame(string versionFile, Profile profile)
        {
            GameIdentity game = GameClient.Read(versionFile);
            if (game.VersionString != profile.targetVersion || game.Build != profile.targetBuild)
                throw Bad("本機遊戲版本或建置已變更，請按「更新版本資料」後再轉換。");
        }

        public static ConvertResult ConvertFile(string source, string directory, string versionFile,
            Profile profile, CancellationToken cancel)
        { return ConvertInput(new ImportInput { Container = source }, directory, versionFile, profile, cancel); }

        public static ConvertResult ConvertInput(ImportInput input, string directory, string versionFile,
            Profile profile, CancellationToken cancel)
        {
            ValidateGame(versionFile, profile);
            if (input.Error != null) throw Bad(input.Error);
            if (!String.Equals(Path.GetExtension(input.FileName), ".dat", StringComparison.OrdinalIgnoreCase))
                throw Bad("請選擇 .dat 回顧或 ZIP 備份檔。");
            byte[] original;
            // Do not read a replay while another process is actively writing it.
            using (var stream = new FileStream(input.Container, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (input.EntryIndex < 0) original = ReadData(stream, stream.Length);
                else using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    if (input.EntryIndex >= zip.Entries.Count || zip.Entries[input.EntryIndex].FullName != input.EntryName)
                        throw Bad("備份內容已變更，請重新匯入。");
                    ZipArchiveEntry entry = zip.Entries[input.EntryIndex];
                    using (Stream entryStream = entry.Open()) original = ReadData(entryStream, entry.Length);
                }
            }
            PreparedReplay prepared = Prepare(original, profile, cancel);
            if (prepared.NoRecording)
                return new ConvertResult { Source = input.SourceLabel, Status = "略過", Detail = "沒有有效錄影資料，無須產生副本。" };
            if (prepared.AlreadyCurrent)
                return new ConvertResult { Source = input.SourceLabel, Status = "略過", Detail = "已是目前版本，無須再次轉換。", Packets = prepared.PacketCount, SourceVersion = prepared.SourceVersion };
            cancel.ThrowIfCancellationRequested();
            Directory.CreateDirectory(directory);
            string stem = Path.GetFileNameWithoutExtension(input.FileName) + "_轉換_" + profile.targetGameVersion;
            string temporary = Path.Combine(directory, ".ff14-convert-" + Guid.NewGuid().ToString("N") + ".tmp");
            string destination = null;
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    output.Write(prepared.Data, 0, prepared.Data.Length);
                    output.Flush(true);
                }
                if (!File.ReadAllBytes(temporary).SequenceEqual(prepared.Data)) throw Bad("寫入後驗證失敗。");
                cancel.ThrowIfCancellationRequested();
                // File.Move never replaces a file. Retry a fresh name if another process created it.
                for (int index = 1; index < 10000; index++)
                {
                    destination = Path.Combine(directory, stem + (index == 1 ? "" : "_" + index) + ".dat");
                    try { File.Move(temporary, destination); break; }
                    catch (IOException) { if (!File.Exists(destination)) throw; destination = null; }
                }
                if (destination == null) throw new IOException("輸出資料夾中有太多同名檔案。");
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            string hash;
            using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(prepared.Data)).Replace("-", "").ToLowerInvariant();
            return new ConvertResult { Source = input.SourceLabel, Output = destination, Status = "完成", Packets = prepared.PacketCount, SourceVersion = prepared.SourceVersion,
                Sha256 = hash, Detail = prepared.RepairedHeader ? "已修復僅改版本號的檔案，並完成封包轉換。" : "已產生新版副本，原檔完整保留。" };
        }
    }

    public sealed class Settings
    {
        public string VersionFile { get; set; }
        public string OutputDirectory { get; set; }
    }

    public sealed class MainForm : Form
    {
        Profile profile;
        readonly Color background = Color.FromArgb(17, 23, 36);
        readonly Color surface = Color.FromArgb(27, 35, 51);
        readonly Color muted = Color.FromArgb(160, 175, 196);
        readonly Color accent = Color.FromArgb(172, 164, 255);
        Label gameStatus, gamePath, outputPath, summary, dropHint, support, titleLabel, subtitleLabel;
        Button import, gameBrowse, outputBrowse, openOutput, updateButton;
        ListView results;
        ProgressBar progress;
        string versionFile, outputDirectory;
        bool busy, closeAfterCancel;
        CancellationTokenSource cancellation;
        readonly string settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "設定.json");
        readonly ToolTip hints = new ToolTip();

        public MainForm(Profile p)
        {
            profile = p;
            Text = "FF14 回顧轉換工具";
            Icon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location);
            ClientSize = new Size(880, 700);
            MinimumSize = new Size(780, 720);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = background;
            ForeColor = Color.FromArgb(235, 240, 249);
            Font = new Font("Microsoft JhengHei UI", 10F);
            AutoScaleMode = AutoScaleMode.Dpi;
            AllowDrop = true;
            BuildLayout();
            LoadSettings();
            RefreshGame();
            DragEnter += OnDragEnter;
            DragDrop += OnDrop;
            FormClosing += delegate(object sender, FormClosingEventArgs e) {
                if (busy) { e.Cancel = true; closeAfterCancel = true; cancellation.Cancel(); summary.Text = "正在結束轉換…"; }
            };
            Shown += async delegate {
                if (Environment.GetCommandLineArgs().Length == 1 && File.Exists(versionFile))
                {
                    try { GameIdentity game = GameClient.Read(versionFile);
                        if (game.VersionString != profile.targetVersion || game.Build != profile.targetBuild) await UpdateVersionData(true); }
                    catch (InvalidDataException ex) { summary.Text = ex.Message; hints.SetToolTip(summary, ex.Message); } catch (IOException) { }
                }
            };
        }

        Label LabelAt(string text, float size, Color color)
        { return new Label { Text = text, AutoSize = true, ForeColor = color, Font = new Font(Font.FontFamily, size), Margin = new Padding(0) }; }

        Button MakeButton(string text, bool primary)
        {
            var b = new Button { Text = text, AutoSize = false, Height = 40, Width = 146,
                FlatStyle = FlatStyle.Flat, BackColor = primary ? accent : surface,
                ForeColor = primary ? background : ForeColor, Cursor = Cursors.Hand,
                Font = new Font(Font.FontFamily, primary ? 12 : 9.5F, primary ? FontStyle.Bold : FontStyle.Regular),
                Margin = new Padding(0) };
            b.FlatAppearance.BorderSize = 0;
            return b;
        }

        void BuildLayout()
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(32, 26, 32, 22),
                ColumnCount = 1, RowCount = 8, BackColor = background };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            foreach (float h in new float[] { 164, 158, 34 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, h));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            Controls.Add(layout);

            var header = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2, RowCount = 2, Margin = new Padding(0, 0, 0, 16) };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.RowStyles.Add(new RowStyle(SizeType.AutoSize)); header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            titleLabel = LabelAt("FF14 回顧轉換", 23, ForeColor); titleLabel.Anchor = AnchorStyles.Left;
            subtitleLabel = LabelAt("把舊回顧帶回目前版本，保留每一次戰鬥。", 10, muted);
            subtitleLabel.Margin = new Padding(1, 8, 0, 0); subtitleLabel.Anchor = AnchorStyles.Left;
            updateButton = MakeButton("更新版本資料", false); updateButton.Size = new Size(146, 40);
            updateButton.Anchor = AnchorStyles.Top | AnchorStyles.Right; updateButton.Margin = new Padding(12, 0, 0, 0);
            updateButton.Click += async delegate { await UpdateVersionData(false); };
            header.Controls.Add(titleLabel, 0, 0); header.Controls.Add(updateButton, 1, 0);
            header.Controls.Add(subtitleLabel, 0, 1); header.SetColumnSpan(subtitleLabel, 2);
            layout.Controls.Add(header, 0, 0);

            var drop = new Panel { Dock = DockStyle.Fill, BackColor = surface, Margin = new Padding(0, 0, 0, 16), AllowDrop = true };
            var dropTitle = LabelAt("將回顧 .dat 或 ZIP 拖到這裡", 15, ForeColor);
            dropTitle.AutoSize = false; dropTitle.TextAlign = ContentAlignment.MiddleCenter; dropTitle.Dock = DockStyle.Top; dropTitle.Height = 47;
            dropHint = LabelAt("可批次匯入回顧或備份壓縮檔，自動辨識舊版本", 9.5F, muted);
            dropHint.AutoSize = false; dropHint.TextAlign = ContentAlignment.MiddleCenter; dropHint.Dock = DockStyle.Top; dropHint.Height = 28;
            import = MakeButton("匯入並轉換回顧", true); import.Size = new Size(228, 44);
            import.Click += async delegate { using (var dialog = new OpenFileDialog { Filter = "FF14 回顧與備份|*.dat;*.zip;*.zip.backup|回顧檔 (*.dat)|*.dat|ZIP 備份|*.zip;*.zip.backup", Multiselect = true, Title = "選取舊版本回顧或備份" })
                { if (Directory.Exists(outputDirectory)) dialog.InitialDirectory = outputDirectory;
                  if (dialog.ShowDialog(this) == DialogResult.OK) await ConvertFiles(dialog.FileNames); } };
            var buttonRow = new Panel { Dock = DockStyle.Bottom, Height = 62 };
            buttonRow.Controls.Add(import); buttonRow.Resize += delegate { import.Location = new Point((buttonRow.ClientSize.Width - import.Width) / 2, 3); };
            drop.Controls.Add(dropHint); drop.Controls.Add(dropTitle); drop.Controls.Add(buttonRow);
            drop.DragEnter += OnDragEnter; drop.DragDrop += OnDrop;
            layout.Controls.Add(drop, 0, 1);

            var config = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4,
                Margin = new Padding(0), BackColor = background };
            config.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); config.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 126));
            config.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); config.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            config.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); config.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            gameStatus = LabelAt("確認遊戲版本中…", 10, accent); gameStatus.Dock = DockStyle.Fill; gameStatus.TextAlign = ContentAlignment.MiddleLeft;
            config.Controls.Add(gameStatus, 0, 0); config.SetColumnSpan(gameStatus, 2);
            gamePath = LabelAt("", 9, muted); gamePath.AutoSize = false; gamePath.AutoEllipsis = true; gamePath.Dock = DockStyle.Fill; gamePath.TextAlign = ContentAlignment.MiddleLeft;
            gameBrowse = MakeButton("選擇遊戲位置", false); gameBrowse.Width = 126;
            gameBrowse.Click += delegate { using (var d = new OpenFileDialog { Filter = "遊戲主程式或版本檔|ffxiv_dx11.exe;ffxivgame.ver", Title = "選取 game 資料夾內的 ffxiv_dx11.exe" })
                { if (File.Exists(versionFile)) d.InitialDirectory = Path.GetDirectoryName(versionFile);
                  if (d.ShowDialog(this) == DialogResult.OK) { versionFile = Path.Combine(Path.GetDirectoryName(d.FileName), "ffxivgame.ver"); SaveSettings(); RefreshGame(); } } };
            config.Controls.Add(gamePath, 0, 1); config.Controls.Add(gameBrowse, 1, 1);
            var outLabel = LabelAt("輸出位置 · 自動另存副本", 10, ForeColor); outLabel.Dock = DockStyle.Fill; outLabel.TextAlign = ContentAlignment.MiddleLeft;
            config.Controls.Add(outLabel, 0, 2); config.SetColumnSpan(outLabel, 2);
            outputPath = LabelAt("", 9, muted); outputPath.AutoSize = false; outputPath.AutoEllipsis = true; outputPath.Dock = DockStyle.Fill; outputPath.TextAlign = ContentAlignment.MiddleLeft;
            outputBrowse = MakeButton("更換資料夾", false); outputBrowse.Width = 126;
            outputBrowse.Click += delegate { using (var d = new FolderBrowserDialog { Description = "選取轉換後回顧的存放位置", SelectedPath = Directory.Exists(outputDirectory) ? outputDirectory : "" })
                { if (d.ShowDialog(this) == DialogResult.OK) { outputDirectory = d.SelectedPath; outputPath.Text = outputDirectory; SaveSettings(); } } };
            config.Controls.Add(outputPath, 0, 3); config.Controls.Add(outputBrowse, 1, 3);
            layout.Controls.Add(config, 0, 2);
            var resultTitle = LabelAt("轉換結果", 11, ForeColor); resultTitle.Dock = DockStyle.Fill; resultTitle.TextAlign = ContentAlignment.MiddleLeft;
            layout.Controls.Add(resultTitle, 0, 3);
            results = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false,
                BorderStyle = BorderStyle.None, BackColor = surface, ForeColor = ForeColor, HeaderStyle = ColumnHeaderStyle.None,
                HideSelection = false, ShowItemToolTips = true, Margin = new Padding(0) };
            results.Columns.Add("回顧檔案", 460); results.Columns.Add("結果", 285);
            results.Resize += delegate { results.Columns[0].Width = Math.Max(250, results.ClientSize.Width - 290); results.Columns[1].Width = 280; };
            results.DoubleClick += delegate { if (results.SelectedItems.Count == 1) { var r = results.SelectedItems[0].Tag as ConvertResult;
                if (r != null && !String.IsNullOrEmpty(r.Output) && File.Exists(r.Output))
                    Process.Start("explorer.exe", "/select,\"" + r.Output + "\""); } };
            layout.Controls.Add(results, 0, 4);
            progress = new ProgressBar { Dock = DockStyle.Fill, Height = 5, Style = ProgressBarStyle.Continuous, Margin = new Padding(0, 10, 0, 6) };
            layout.Controls.Add(progress, 0, 5);
            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 146));
            summary = LabelAt("準備就緒 · 原檔會完整保留", 9.5F, muted); summary.AutoSize = false; summary.Dock = DockStyle.Fill; summary.TextAlign = ContentAlignment.MiddleLeft;
            openOutput = MakeButton("開啟輸出資料夾", false);
            openOutput.Click += delegate { try { if (!Directory.Exists(outputDirectory)) Directory.CreateDirectory(outputDirectory); Process.Start("explorer.exe", "\"" + outputDirectory + "\""); }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "無法開啟資料夾", MessageBoxButtons.OK, MessageBoxIcon.Information); } };
            footer.Controls.Add(summary, 0, 0); footer.Controls.Add(openOutput, 1, 0); layout.Controls.Add(footer, 0, 6);
            support = LabelAt("轉換可離線 · " + VersionNames.Supported(profile), 8.5F, muted);
            support.AutoSize = false; support.AutoEllipsis = true; hints.SetToolTip(support, VersionNames.Supported(profile));
            support.Dock = DockStyle.Fill; support.TextAlign = ContentAlignment.MiddleLeft;
            layout.Controls.Add(support, 0, 7);
        }

        void LoadSettings()
        {
            Settings settings = null;
            try { if (File.Exists(settingsPath)) settings = new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(settingsPath)); } catch { }
            versionFile = settings == null ? null : settings.VersionFile;
            if (!File.Exists(versionFile))
            {
                string[] roots = { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) };
                versionFile = roots.Select(r => Path.Combine(r, "SquareEnix", "FINAL FANTASY XIV - A Realm Reborn", "game", "ffxivgame.ver")).FirstOrDefault(File.Exists);
            }
            outputDirectory = settings == null ? null : settings.OutputDirectory;
            if (String.IsNullOrWhiteSpace(outputDirectory))
                outputDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "FINAL FANTASY XIV - A Realm Reborn", "replay", "autorenamed");
            outputPath.Text = outputDirectory;
        }

        void SaveSettings()
        {
            try { File.WriteAllText(settingsPath, new JavaScriptSerializer().Serialize(new Settings { VersionFile = versionFile, OutputDirectory = outputDirectory }), new UTF8Encoding(true)); }
            catch { /* Settings are optional: conversion remains available in a read-only portable folder. */ }
        }

        void RefreshGame()
        {
            gamePath.Text = versionFile == null ? "尚未找到遊戲安裝位置" : Path.GetDirectoryName(versionFile);
            try { Engine.ValidateGame(versionFile, profile); gameStatus.Text = "● 目前遊戲版本 " + VersionNames.Display(profile.targetGameVersion) + " · 可以轉換"; gameStatus.ForeColor = accent; import.Enabled = !busy; }
            catch (Exception ex) { gameStatus.Text = File.Exists(versionFile) ? "● 遊戲版本已變更 · 請更新版本資料" : "● 尚未找到遊戲 · 請選擇遊戲位置";
                hints.SetToolTip(gameStatus, ex.Message); gameStatus.ForeColor = Color.FromArgb(247, 185, 123); import.Enabled = false; }
        }

        void OnDragEnter(object sender, DragEventArgs e)
        { e.Effect = !busy && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; }
        async void OnDrop(object sender, DragEventArgs e)
        { if (!busy && e.Data.GetDataPresent(DataFormats.FileDrop)) await ConvertFiles((string[])e.Data.GetData(DataFormats.FileDrop)); }

        public async Task UpdateVersionData(bool automatic)
        {
            if (busy) return;
            GameIdentity local;
            try { local = GameClient.Read(versionFile); }
            catch (Exception ex) { summary.Text = ex.Message; hints.SetToolTip(summary, ex.Message); return; }
            if (local.VersionString == profile.targetVersion && local.Build == profile.targetBuild)
            { summary.Text = "遊戲 " + VersionNames.Display(profile.targetGameVersion) + " 的資料已就緒，可直接匯入回顧。"; return; }
            if (local.VersionString == profile.targetVersion)
            { summary.Text = "主程式建置與已保存資料不同，但安裝版本尚未變更；請先完成遊戲更新或修復。"; hints.SetToolTip(summary, summary.Text); return; }
            busy = true; cancellation = new CancellationTokenSource();
            import.Enabled = gameBrowse.Enabled = outputBrowse.Enabled = updateButton.Enabled = false;
            summary.Text = "已讀取遊戲主程式建置，正在查詢版本對照…";
            try
            {
                UpdatePlan plan = await Task.Run(() => Updater.FetchPlan(profile, local.VersionString, cancellation.Token));
                Profile updated = await Task.Run(() => Updater.BuildUpdatedProfile(profile, plan, local, cancellation.Token));
                cancellation.Token.ThrowIfCancellationRequested();
                GameIdentity latest = GameClient.Read(versionFile);
                if (latest.VersionString != local.VersionString || latest.Build != local.Build)
                    throw new InvalidDataException("遊戲在更新資料時又發生變更，請重新檢查。");
                Updater.Save(updated, Profile.CachePath);
                profile = updated; support.Text = "轉換可離線 · " + VersionNames.Supported(profile); hints.SetToolTip(support, VersionNames.Supported(profile));
                summary.Text = "已更新至遊戲 " + VersionNames.Display(profile.targetGameVersion) + " · 可匯入舊回顧，並實測新版播放。";
            }
            catch (Exception ex)
            {
                if (cancellation.IsCancellationRequested) summary.Text = "已取消更新，原版本資料已保留。";
                else { summary.Text = "尚未更新 · " + ex.Message; hints.SetToolTip(summary, ex.Message);
                    if (!automatic) MessageBox.Show(this, ex.Message, "版本資料尚未更新", MessageBoxButtons.OK, MessageBoxIcon.Information); }
            }
            finally
            {
                busy = false; cancellation.Dispose(); gameBrowse.Enabled = outputBrowse.Enabled = updateButton.Enabled = true; RefreshGame();
                if (closeAfterCancel) Close();
            }
        }

        public async Task ConvertFiles(string[] paths)
        {
            if (busy) return;
            RefreshGame();
            if (!import.Enabled && File.Exists(versionFile))
            {
                await UpdateVersionData(true);
                if (IsDisposed || Disposing || closeAfterCancel) return;
                RefreshGame();
            }
            if (!import.Enabled) { summary.Text = "請先確認遊戲版本與安裝位置。"; return; }
            paths = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (paths.Length == 0) return;
            List<ImportInput> inputs = Engine.ExpandInputs(paths);
            busy = true; cancellation = new CancellationTokenSource();
            import.Enabled = gameBrowse.Enabled = outputBrowse.Enabled = updateButton.Enabled = false;
            results.Items.Clear(); progress.Maximum = inputs.Count; progress.Value = 0;
            var receipts = new List<ConvertResult>();
            int completed = 0, skipped = 0, failed = 0;
            try
            {
                for (int i = 0; i < inputs.Count; i++)
                {
                    if (cancellation.IsCancellationRequested) break;
                    ImportInput input = inputs[i];
                    summary.Text = "正在轉換 " + (i + 1) + " / " + inputs.Count + "：" + input.FileName;
                    var item = new ListViewItem(input.DisplayName); item.SubItems.Add("處理中…"); results.Items.Add(item); item.EnsureVisible();
                    ConvertResult result;
                    try { result = await Task.Run(() => Engine.ConvertInput(input, outputDirectory, versionFile, profile, cancellation.Token)); }
                    catch (OperationCanceledException) { item.SubItems[1].Text = "已取消"; break; }
                    catch (Exception ex) { result = new ConvertResult { Source = input.SourceLabel, Status = "未轉換", Detail = ex.Message }; }
                    receipts.Add(result); item.Tag = result; item.ToolTipText = result.Detail + (result.Output == null ? "" : "\n" + result.Output);
                    item.SubItems[1].Text = result.Status + " · " + result.Detail;
                    if (result.Status == "完成") { completed++; item.ForeColor = Color.FromArgb(153, 222, 188); }
                    else if (result.Status == "略過") { skipped++; item.ForeColor = muted; }
                    else { failed++; item.ForeColor = Color.FromArgb(247, 185, 123); }
                    progress.Value = i + 1;
                }
                summary.Text = "完成 " + completed + " 個 · 略過 " + skipped + " 個 · 未轉換 " + failed + " 個";
                if (cancellation.IsCancellationRequested) summary.Text += " · 已取消剩餘檔案";
                // Receipts live beside the portable app, so the game's replay folder contains only replay files.
                try { string logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "轉換紀錄"); Directory.CreateDirectory(logDir);
                    File.WriteAllText(Path.Combine(logDir, DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".json"),
                        new JavaScriptSerializer().Serialize(receipts), new UTF8Encoding(true)); } catch { }
            }
            finally
            {
                busy = false; cancellation.Dispose(); gameBrowse.Enabled = outputBrowse.Enabled = updateButton.Enabled = true; RefreshGame();
                if (closeAfterCancel) Close();
            }
        }

        public void Preview(string filename)
        {
            Opacity = 0;
            Show(); Application.DoEvents();
            using (var bitmap = new Bitmap(Width, Height)) { DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height)); bitmap.Save(filename); }
            Hide(); Close();
        }

        public void LayoutPreview(float scale, string filename)
        {
            Opacity = 0;
            ClientSize = new Size((int)(880 * scale), (int)(700 * scale));
            titleLabel.Font = new Font(Font.FontFamily, 23 * scale);
            subtitleLabel.Font = new Font(Font.FontFamily, 10 * scale);
            Show(); Application.DoEvents(); PerformLayout();
            if (subtitleLabel.Top < titleLabel.Bottom + 6 || subtitleLabel.Bottom > subtitleLabel.Parent.ClientSize.Height)
                throw new Exception("Title or subtitle was clipped at scale " + scale);
            if (subtitleLabel.Width < subtitleLabel.PreferredWidth || subtitleLabel.Height < subtitleLabel.PreferredHeight)
                throw new Exception("Subtitle preferred bounds were clipped at scale " + scale);
            using (var bitmap = new Bitmap(Width, Height)) { DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height)); bitmap.Save(filename); }
            Hide(); Close();
        }

        public void ConfigureTestPaths(string game, string output)
        { versionFile = game; outputDirectory = output; outputPath.Text = output; RefreshGame(); }

        public string SummaryText { get { return summary.Text; } }
    }

    public static class SelfTests
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        static void Reject(Action action, string message)
        { try { action(); } catch (InvalidDataException) { return; } throw new Exception(message); }

        public static int Audit(Profile profile, string root, string folder)
        {
            Directory.CreateDirectory(folder);
            string game = GameClientTests.Fixture(folder, profile.targetVersion, profile.targetBuild);
            var paths = Directory.GetFiles(root, "*.dat", SearchOption.AllDirectories).Concat(Directory.GetFiles(root, "archive.zip*")).Distinct().ToArray();
            var before = new Dictionary<string, string>();
            foreach (string path in paths) using (var sha = SHA256.Create()) using (var s = File.OpenRead(path)) before[path] = BitConverter.ToString(sha.ComputeHash(s));
            var receipts = new List<ConvertResult>();
            foreach (ImportInput input in Engine.ExpandInputs(paths))
            {
                try { receipts.Add(Engine.ConvertInput(input, folder, game, profile, CancellationToken.None)); }
                catch (Exception ex) { receipts.Add(new ConvertResult { Source = input.SourceLabel, Status = "未轉換", Detail = ex.Message }); }
            }
            foreach (string path in paths) using (var sha = SHA256.Create()) using (var s = File.OpenRead(path))
                Check(before[path] == BitConverter.ToString(sha.ComputeHash(s)), "Source or archive was changed: " + path);
            File.WriteAllText(Path.Combine(folder, "完整回顧測試.json"), new JavaScriptSerializer().Serialize(receipts), new UTF8Encoding(true));
            return receipts.Any(r => r.Status == "未轉換") ? 1 : 0;
        }
        public static void Run(Profile profile, string source, string expected, string folder)
        {
            Directory.CreateDirectory(folder);
            byte[] original = File.ReadAllBytes(source), expectedBytes = File.ReadAllBytes(expected);
            var passed = new List<string>();
            var p = Engine.Prepare(original, profile, CancellationToken.None);
            Check(p.Data.SequenceEqual(expectedBytes), "Regression differs from the replay confirmed playable by the user.");
            Check(p.PacketCount == 203985 && p.OpcodeCount == 47, "Packet inventory mismatch.");
            passed.Add("與使用者已確認可播放的轉換檔逐位元組相同；203985 個封包、47 種編號");
            Check(Engine.Prepare(expectedBytes, profile, CancellationToken.None).AlreadyCurrent, "Double conversion not prevented.");
            passed.Add("已轉換檔案會略過");
            byte[] headerOnly = (byte[])original.Clone(); Buffer.BlockCopy(BitConverter.GetBytes(profile.targetBuild), 0, headerOnly, 16, 4);
            var repaired = Engine.Prepare(headerOnly, profile, CancellationToken.None);
            Check(repaired.RepairedHeader && repaired.Data.SequenceEqual(expectedBytes), "Header-only repair failed."); passed.Add("可修復先前只改版本號的副本");
            byte[] invalid = (byte[])original.Clone(); invalid[0] = 0;
            Reject(() => Engine.Prepare(invalid, profile, CancellationToken.None), "Bad magic accepted.");
            Reject(() => Engine.Prepare(original.Take(original.Length - 1).ToArray(), profile, CancellationToken.None), "Truncation accepted.");
            byte[] unsupported = (byte[])original.Clone(); Buffer.BlockCopy(BitConverter.GetBytes((uint)1), 0, unsupported, 16, 4);
            Reject(() => Engine.Prepare(unsupported, profile, CancellationToken.None), "Unknown build accepted.");
            byte[] unmapped = (byte[])original.Clone(); unmapped[0x400] = 0xFF; unmapped[0x401] = 0xEF;
            Reject(() => Engine.Prepare(unmapped, profile, CancellationToken.None), "Unknown opcode accepted.");
            byte[] badSize = (byte[])original.Clone(); badSize[0x36E] = 0x87;
            Reject(() => Engine.Prepare(badSize, profile, CancellationToken.None), "Wrong payload size accepted.");
            byte[] badChapter = (byte[])original.Clone(); Buffer.BlockCopy(BitConverter.GetBytes((uint)1), 0, badChapter, 0x70, 4);
            Reject(() => Engine.Prepare(badChapter, profile, CancellationToken.None), "Invalid chapter offset accepted.");
            passed.Add("拒絕錯誤格式、截斷、未知版本、未知封包、異常內容長度與章節");
            string game = GameClientTests.Fixture(folder, profile.targetVersion, profile.targetBuild);
            string stale = Path.Combine(folder, "stale.ver"); File.WriteAllText(stale, "2027.01.01.0000.0000");
            Reject(() => Engine.ValidateGame(stale, profile), "Stale game accepted."); passed.Add("遊戲更新後會停止使用舊對照表");
            var first = Engine.ConvertFile(source, folder, game, profile, CancellationToken.None);
            var second = Engine.ConvertFile(source, folder, game, profile, CancellationToken.None);
            Check(first.Output != second.Output && File.ReadAllBytes(first.Output).SequenceEqual(expectedBytes) && File.ReadAllBytes(second.Output).SequenceEqual(expectedBytes), "Collision output incorrect.");
            passed.Add("中文與空白路徑正確；重複檔名另存且不覆寫");
            using (var cts = new CancellationTokenSource()) { cts.Cancel(); bool canceled = false;
                try { Engine.ConvertFile(source, folder, game, profile, cts.Token); } catch (OperationCanceledException) { canceled = true; }
                Check(canceled, "Cancellation ignored."); }
            Check(!Directory.GetFiles(folder, "*.tmp").Any(), "Temporary file leaked.");
            Check(File.ReadAllBytes(source).SequenceEqual(original), "Original changed."); passed.Add("取消時不產生半成品；來源檔完整保留");
            File.WriteAllText(Path.Combine(folder, "測試結果.json"), new JavaScriptSerializer().Serialize(passed), new UTF8Encoding(true));
        }
    }

    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            try
            {
                Profile profile = Profile.Load();
                if (args.Length == 3 && args[0] == "--inspect-game")
                {
                    GameIdentity game = GameClient.Read(args[1]); string retail;
                    profile.gameVersions.TryGetValue(game.VersionString, out retail);
                    File.WriteAllText(args[2], Updater.Json().Serialize(new { GameVersion = VersionNames.Display(retail), Build = game.Build, Source = game.ExecutablePath }));
                    return 0;
                }
                if (args.Length == 4 && args[0] == "--self-test") { SelfTests.Run(profile, args[1], args[2], args[3]); return 0; }
                if (args.Length == 5 && args[0] == "--update-test") { UpdateTests.Run(profile, args[1], args[2], args[3], args[4]); return 0; }
                if (args.Length == 3 && args[0] == "--audit") return SelfTests.Audit(profile, args[1], args[2]);
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                if (args.Length == 2 && args[0] == "--layout-test")
                {
                    Directory.CreateDirectory(args[1]);
                    foreach (float scale in new float[] { 1F, 1.25F, 1.5F, 2F })
                        new MainForm(profile).LayoutPreview(scale, Path.Combine(args[1], "排版_" + (int)(scale * 100) + ".png"));
                    File.WriteAllText(Path.Combine(args[1], "排版測試.txt"), "100%、125%、150%、200% 標題與說明文字均沒有重疊或被裁切。");
                    return 0;
                }
                var form = new MainForm(profile);
                if (args.Length == 2 && args[0] == "--preview") { form.Preview(args[1]); return 0; }
                if (args.Length == 3 && args[0] == "--update-ui-test")
                {
                    Profile baseline = Updater.Json().Deserialize<Profile>(File.ReadAllText(args[1])); Profile.Validate(baseline);
                    form = new MainForm(baseline);
                    string folder = args[2]; Directory.CreateDirectory(folder);
                    string game = GameClientTests.Fixture(folder, profile.targetVersion, profile.targetBuild);
                    form.ConfigureTestPaths(game, folder); form.Opacity = 0; int code = 0;
                    form.Shown += async delegate {
                        try {
                            await form.UpdateVersionData(true);
                            if (!form.SummaryText.StartsWith("已更新至遊戲 " + VersionNames.Display(profile.targetGameVersion), StringComparison.Ordinal)) throw new Exception(form.SummaryText);
                            using (var bitmap = new Bitmap(form.Width, form.Height)) {
                                form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height)); bitmap.Save(Path.Combine(folder, "更新後介面.png")); }
                            File.WriteAllText(Path.Combine(folder, "更新介面測試結果.txt"), form.SummaryText);
                        } catch (Exception ex) { code = 1; File.WriteAllText(Path.Combine(folder, "更新介面測試失敗.txt"), ex.ToString()); }
                        finally { form.Close(); }
                    };
                    Application.Run(form); return code;
                }
                if (args.Length == 4 && args[0] == "--ui-test")
                {
                    string folder = args[3]; Directory.CreateDirectory(folder);
                    string game = GameClientTests.Fixture(folder, profile.targetVersion, profile.targetBuild);
                    string invalid = Path.Combine(folder, "無效的回顧.dat"); File.WriteAllText(invalid, "not a replay");
                    form.ConfigureTestPaths(game, folder); form.Opacity = 0;
                    int code = 0;
                    form.Shown += async delegate {
                        try {
                            await form.ConvertFiles(new string[] { args[1], args[2], invalid });
                            bool zipTest = args[1].EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
                            string expectedSummary = zipTest ? "完成 2 個 · 略過 2 個 · 未轉換 1 個" : "完成 1 個 · 略過 1 個 · 未轉換 1 個";
                            if (form.SummaryText != expectedSummary) throw new Exception(form.SummaryText);
                            using (var bitmap = new Bitmap(form.Width, form.Height)) {
                                form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height)); bitmap.Save(Path.Combine(folder, "批次轉換畫面.png")); }
                            File.WriteAllText(Path.Combine(folder, "介面測試結果.txt"), form.SummaryText);
                        } catch (Exception ex) { code = 1; File.WriteAllText(Path.Combine(folder, "介面測試失敗.txt"), ex.ToString()); }
                        finally { form.Close(); }
                    };
                    Application.Run(form); return code;
                }
                if (args.Length > 0) form.Shown += async delegate { await form.ConvertFiles(args); };
                Application.Run(form); return 0;
            }
            catch (Exception ex)
            {
                if (args.Length > 0 && (args[0] == "--self-test" || args[0] == "--update-test")) File.WriteAllText(Path.Combine(args[args.Length - 1], "測試失敗.txt"), ex.ToString());
                else MessageBox.Show(ex.Message, "FF14 回顧轉換工具", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
    }
}
