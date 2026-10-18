# FF14 回顧轉換工具

Chatgpt 編寫，無人工維護

Windows 桌面工具，將已收錄對照的舊版 FINAL FANTASY XIV 任務回顧另存為目前版本使用的副本。目前程式版本為 **v1.4**，內建目標遊戲版本為 **7.57**。新增較早版本的試轉換，尚未逐版實際播放驗證。

## 功能

- 拖曳或批次匯入 `.dat`、`.dat.old`、`.zip`、`.zip.backup`。
- 直接讀取本機 `ffxiv_dx11.exe` 的建置標記，並以 `ffxivgame.ver` 核對安裝版本；不需要新版回顧檔。
- 使用遊戲版本顯示支援範圍，例如 7.57、7.56；修正更新另行標示。
- 轉換建置編號與封包編號，保留章節、時間戳及封包內容。
- 原始檔只讀，另存副本；同名自動編號，避免覆寫。
- 從公開來源更新版本對照，驗證後快取供離線轉換。

## 內建對照與驗證範圍

| 來源遊戲版本 | 證據與限制 |
| --- | --- |
| 7.51（修正2）、7.55、7.56、7.56（修正1） | 有本機真實回顧通過結構與轉換檢查；個別回顧仍需確認遊戲內播放 |
| 7.50（修正1）、7.51、7.51（修正1）、7.55（修正1）、7.55（修正2） | 試轉換；169～170 種獨立具名封包核對，尚未實播 |
| 7.00～7.50 的其餘已收錄版本（含修正） | 試轉換；逐版二進位封包對照及合成資料檢查，尚無對應真實回顧實播驗證 |

共收錄 **45 個來源版本對照**，目標為 7.57，完整逐版清單見 [支援版本.txt](支援版本.txt)，也可點擊工具底部的版本範圍查看。這是對照可用範圍，不是所有版本均已確認可播放的聲明；6.x 及更早版本尚未納入。

修正1、修正2對應公開目錄的 `h`、`h2`，不是新的正式版本號。並非所有未收錄的歷史修正版都可轉換。支援 Windows 回顧格式 5，單份最大 512 MB；ZIP 最多 2048 份回顧，不支援加密 ZIP、RAR 或 7z。

來源建置已知時優先依建置辨識。缺少可靠建置編號的版本不會虛構編號，也不使用錄製日期猜版本；會檢查所有封包種類與已知內容長度，要求至少 20 種一般封包、其中至少 12 種通過已知長度核對，且只符合一個版本。過短、未知、衝突或不相容的回顧會停止，原檔保留。已知長度是目前可接受的結構篩選，不能證明所有歷史封包內容相容；同長度的語意變更也可能需要額外轉換。

## 建置與使用

在 Windows 上使用 .NET Framework 4.x 內建 C# 編譯器建置，不需要安裝 Python 或額外 NuGet 套件：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

產物位於 `dist/FF14回顧轉換工具.exe`，同資料夾包含使用說明與第三方授權。

開啟工具後，確認遊戲與輸出位置，再匯入回顧。遊戲位置優先沿用工具旁的 `設定.json`，否則檢查 Windows Program Files 下的標準安裝位置。自訂位置或 Steam 安裝需手動指定一次。預設輸出使用目前 Windows「文件」資料夾下的 `My Games/FINAL FANTASY XIV - A Realm Reborn/replay/autorenamed`。

詳細操作見 [使用說明.txt](使用說明.txt)。

## 遊戲更新

開啟時會檢查本機遊戲版本，也可按「更新版本資料」。工具下載公開版本目錄、一對一封包差異與獨立具名封包表，交叉核對後更新對照；轉換本身在本機進行，不上傳回顧。

目前目標的自動更新限同一內容版本系列，例如 7.5 系列；這與已內建的舊版來源範圍是兩回事。跨系列的未來目標、封包內容結構改變、未知或拆分合併的封包仍需更新程式；只更新編號不能保證所有未來版本可播放。首次轉換後應在遊戲內確認結果。下載失敗或資料不一致時會保留原對照。升級時會合併新增歷史對照與原有同目標快取；較新目標快取若保留 7.57 的轉換路徑，也能串接新增來源。

## 原始碼與驗證

| 檔案 | 用途 |
| --- | --- |
| `Program.cs` | 介面、回顧讀取與轉換、核心回歸檢查 |
| `GameClient.cs` | 遊戲 PE 建置辨識、版本名稱與讀取器檢查 |
| `Updater.cs` | 公開資料下載、對照串接與快取 |
| `UpdateTests.cs` | 更新流程整合檢查 |
| `HistoricalRoutes.cs` | 保守的來源特徵辨識與舊快取合併 |
| `HistoricalTests.cs` | 45 個來源合成檢查、衝突拒絕及未來路徑保留 |
| `profile.json` | 編入程式的版本資料 |
| `generate_profiles.py`、`historical_profiles.py`、`data/` | 從固定版本的資料重建內建對照與獨立核對證據 |

`profile.json` 已收錄，日常建置不需要執行 Python。需要重建資料時使用 Python 3：

```powershell
python .\generate_profiles.py
```

介面排版檢查可在建置後執行：

```powershell
Start-Process -FilePath '.\dist\FF14回顧轉換工具.exe' -ArgumentList '--layout-test', '.\test-output\layout' -Wait
```

核心回歸檢查 `--self-test <來源回顧> <已確認可播放副本> <測試輸出資料夾>` 使用開發時的特定樣本，包含該樣本的封包數量檢查，不能當作任意回顧的通用測試。本儲存庫不包含私人回顧樣本、角色資料或遊戲主程式。

`--historical-test <測試輸出資料夾>` 不需要私人樣本，檢查全部歷史來源的合成資料辨識、原內容保留、歧義拒絕、舊快取合併及未來目標串接。合成資料通過不代表實際遊戲內播放通過。開發時另以 173 筆本機真實回顧／備份內容檢查，63 筆通過轉換、108 筆已是目前版本、2 筆沒有錄影資料；私人檔案未收錄於原始碼。

## 資料來源與授權

- [xivdev/opcodediff](https://github.com/xivdev/opcodediff)：版本目錄及封包差異，授權見 [LICENSE-opcodediff.txt](LICENSE-opcodediff.txt)。收錄差異的固定來源版本見 `profile.json` 的 `sources`。
- [karashiiro/FFXIVOpcodes](https://github.com/karashiiro/FFXIVOpcodes)：具名封包表，授權見 [LICENSE-FFXIVOpcodes.txt](LICENSE-FFXIVOpcodes.txt)。
- [FFXIVNetworkOpcodes 建置辨識參考](https://github.com/gamous/FFXIVNetworkOpcodes/blob/main/record_opcode_finder.py)。

第三方 MIT 授權聲明保留於儲存庫及建置產物。本工具非官方產品。
