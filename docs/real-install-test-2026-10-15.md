# HM-AutoSetup 实际安装测试记录

日期：2026 年 10 月 15 日，Asia/Shanghai。

本轮从官方在线渠道下载当前安装包并校验数字签名，随后依据用户追加授权，在本机逐款执行静默安装，目标根目录为 `E:\TempSoftware`。每款测试后使用对应官方卸载程序移除本轮新增的应用。此轮为真实主机安装，不属于隔离安装。

6 款应用已实际验证主程序安装到指定目录或其子目录。京麦 13.10.0 未通过静默安装验证，现已在严格静默模式下跳过。安装器返回 0 本身不视为验证成功：同时核对应用卸载记录、实际目录和目标中的可执行文件。

## 实际安装结果

| 应用 | 本次在线版本 | 指定目录 | 实际应用目录 | 最终安装验证 |
| --- | --- | --- | --- | --- |
| 千牛工作台 | 9.97.74N | `E:\TempSoftware\Qianniu` | 同指定目录 | Succeeded |
| 抖店工作台 | 1.1.12 | `E:\TempSoftware\Doudian` | `E:\TempSoftware\Doudian\抖店工作台` | Succeeded |
| 拼多多商家工作台 | 3.7.3.7 | `E:\TempSoftware\Pinduoduo` | `E:\TempSoftware\Pinduoduo\pinduoduo\3.7.3.7` | 修复后重测 Succeeded |
| 快语客服工作台 | 1.1.4 | `E:\TempSoftware\Kuaiyu` | 同指定目录 | Succeeded |
| 得物商家客服 | 6.2.0 | `E:\TempSoftware\Dewu` | 同指定目录 | Succeeded |
| 千帆客服工作台 | 1.2.6 | `E:\TempSoftware\Qianfan` | 同指定目录 | 检测修复后重新安装 Succeeded |
| 京麦工作台 | 13.10.0.0 | `E:\TempSoftware\Jingmai` | 未形成可验证的完成安装 | 首轮 Unverified；修复后严格模式 Skipped |

千牛版本依据安装包文件名及本次新增卸载记录；其安装包没有可读取的版本资源。拼多多安装包版本为 3.7.3.7，厂商卸载项的 `DisplayVersion` 仅写为 3.7.3。

以上目录验证覆盖应用主程序。厂商的用户数据、依赖、服务或驱动可能写入其他位置；千牛本轮实际创建的 AlibabaProtect 服务和驱动位于 C 盘，已单独纳入清理工作。

## 测试发现与修复

### 拼多多需要预先存在的目标目录

首次以 `/slient /installpath=E:\TempSoftware\Pinduoduo` 执行，进程返回 0，但实际安装到了 `C:\Program Files (x86)\pinduoduo\3.7.3.7`。安装引擎正确将其标为 Failed，未将错误目录视为成功。

卸载首轮 C 盘安装后，为该安装源增加 `RequiresExistingTargetDirectory`，由引擎在启动安装前创建目标目录。保持厂商实际使用的 `/slient` 参数重测，应用安装到指定 E 盘目录内并通过验证。

### 千帆、快语的已安装名称包含版本

千帆首次安装后存在目标文件及卸载记录，但卸载项名称为 `千帆客服工作台 1.2.6`，初始精确名称匹配没有识别，因此报告 Unverified。

修复版本后缀的应用名称匹配后，重新检查同一安装，报告 AlreadyInstalled，未再次下载或启动安装器。随后卸载并重新完整安装，结果为 Succeeded。快语同类版本名称也能被识别。

### 快语当前官方签名主体使用中文名称

首次在线下载快语安装包后，签名主体为 `北京快手科技有限公司`，原允许列表仅含英文主体，校验按设计拒绝执行。核实官方来源和当前签名后，仅为在线源补充该准确中文主体，保留原英文主体；重新下载通过签名验证。本地安装包的发布者限制没有随之放宽。

### 京麦 13.10.0 是两阶段安装器

本次官方当前版本在第一阶段返回 0 后仍显示第二阶段安装界面，未形成可验证的完成安装。报告为 Unverified，相关界面已关闭，未继续第二阶段安装。

该在线源现已取消静默和静默指定目录能力声明。严格静默重测结果为 Skipped，`Payload` 为空、安装器进程记录为 0，不会静默落到厂商默认目录。不能将旧版 NSIS 安装包的参数支持推断到该当前版本。

## 原有应用的跳过基线

以下应用在测试前已安装，直接跳过。最终对照测试前记录，四款软件的版本、位置和卸载项均保持一致。

| 应用 | 测试前版本 | 测试前实际位置 |
| --- | --- | --- |
| 飞书 | 8.1.17 | `D:\Software\Feishu` |
| 微信 | 4.1.15.13 | `D:\Software\Tencent\Weixin` |
| ToDesk | 5.1.0.0 | `D:\Software\ToDesk` |
| 剪映专业版 | 11.5.0.14471 | `D:\Software\JianyingPro` |

## 本轮实际卸载与附带组件处理

以下记录依据本轮卸载入口与实际执行操作，不表示所有厂商卸载器都支持完整静默卸载。

| 应用 | 本轮卸载方式与已观察结果 |
| --- | --- |
| 千牛 | 指定目录中的 `Uninstall.exe /S`。首次无参数打开的卸载界面仅关闭本轮测试进程后，改用静默卸载。附带组件另行处理，见下文。 |
| 抖店 | `抖店工作台卸载.exe /S`，退出 0，应用卸载项消失，目标目录变空。 |
| 拼多多 | 两轮 `PddUninstall.exe /S` 都会显示卸载界面。首轮界面消失后核对卸载项已移除、C 盘应用目录变空；未据此推断是谁完成界面操作。重试轮由操作代理明确取消保留聊天数据，点击“卸载”及“完成”，再核对卸载项与 E 盘目录并清理空目录。 |
| 快语 | `Uninstall 快语客服工作台.exe /allusers /S`，退出 0，应用卸载项消失。 |
| 得物 | 指定目录中的 `uninstall.exe /S`，退出 0，应用卸载项消失。 |
| 千帆 | 两轮均执行 `Uninstall 千帆客服工作台.exe /currentuser /S`，应用卸载项消失，目录变空。 |
| 京麦 | 关闭未完成的第二阶段安装界面，删除本轮指定目录内的部分解压文件；未将其记录为成功安装或完整官方卸载。 |

千牛创建的 AlibabaProtect 组件通过官方 `AlibabaProtectCon /unload` 处理后，服务和 BluePerfmon 驱动消失；剩余 AliPaladin 由本轮操作者卸载过滤驱动、删除对应服务项，再删除已精确核对的驱动文件。指向本轮测试目录的 AliUpdater 计划任务也在核对后移除。本轮新增的 AlibabaProtect 目录已清理。

服务事件 7045 显示，Alibaba PC Safe Service、AliPaladin、BluePerfmon4_x64 分别在 16:44:20、16:44:22、16:44:28 创建；其中两个驱动晚于主安装进程退出时间。初始快照未包含完整服务和驱动基线，因此不以文件时间单独证明此前不存在；本次事件与安装窗口的对应关系作为附带组件处理证据。

原有 ToDeskAgent 服务及 ToDesk 驱动未参与上述清理。桌面“得物”快捷方式经检查，创建于 2026 年 10 月 10 日，指向 `E:\Program Files\Tencent\Androws\Application\AndrowsLauncher.exe`，属于原有安卓模拟器应用，予以保留。

## 最终清理核验状态

最终核验于 17:06 完成，详见 [cleanup.json](../evidence/real-install-test-2026-10-15/cleanup.json)：

- 对比初始全量卸载记录，新增、变更、删除的原有卸载项数量均为 0；四款原有应用版本和位置未变。
- 七个测试应用目录全部不存在，误装到 C 盘的拼多多目录也已删除；没有在测试目录中运行的进程。
- 本次新增的 AlibabaProtect 服务、AliPaladin/BluePerfmon 驱动、驱动文件、AliUpdater 任务及 AlibabaProtect 文件夹均已清理。
- Windows Sandbox 组件已撤销为 Disabled，全程未重启。原有 ToDeskAgent、`E:\TempSoftware\Todesk` 和安卓模拟器的得物快捷方式均保留。

核验范围为上述安装记录、程序文件、进程及已识别附带组件；保留 Windows 和厂商历史日志，以及本次测试报告，不声称进行了全盘逐字节还原。

## 证据

原始记录保存在 `E:\TempSoftware\HM-AutoSetup-TestReports\20261015-host-*`，每次记录包含官方安装包名称、版本、SHA-256、签名主体、实际启动参数、退出码、目录和应用卸载记录。后续报告另记录安装前后的服务、驱动和计划任务库存。

仓库中仅保存本轮应用的必要摘要，没有复制完整主机卸载注册表或全量服务、驱动及计划任务清单：

- [总索引](../evidence/real-install-test-2026-10-15/summary.json)
- [原有应用基线](../evidence/real-install-test-2026-10-15/preserved-applications-baseline.json)
- [千牛](../evidence/real-install-test-2026-10-15/20261015-host-qianniu.json)、[抖店](../evidence/real-install-test-2026-10-15/20261015-host-doudian.json)
- [拼多多首轮](../evidence/real-install-test-2026-10-15/20261015-host-pinduoduo.json)、[拼多多重试](../evidence/real-install-test-2026-10-15/20261015-host-pinduoduo-retry.json)
- [快语](../evidence/real-install-test-2026-10-15/20261015-host-kuaiyu.json)、[得物](../evidence/real-install-test-2026-10-15/20261015-host-dewu.json)
- [千帆首轮](../evidence/real-install-test-2026-10-15/20261015-host-qianfan.json)、[跳过复查](../evidence/real-install-test-2026-10-15/20261015-host-qianfan-skip-recheck.json)、[千帆重试](../evidence/real-install-test-2026-10-15/20261015-host-qianfan-retry.json)
- [京麦首轮](../evidence/real-install-test-2026-10-15/20261015-host-jingmai.json)、[京麦严格跳过](../evidence/real-install-test-2026-10-15/20261015-host-jingmai-strict-skip.json)
