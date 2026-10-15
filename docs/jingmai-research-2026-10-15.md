# 京麦静默与离线安装复核（v0.4 历史研究）

日期：2026-10-15。本文保留 HM-AutoSetup v0.4 阶段的研究、试验与清理事实，其中“严格静默跳过”是当时行为。

HM AppKit v0.5 已改为打开官方可见安装向导并预填目标目录，仍不声称完整静默。当前实现与验证请查看 [HM AppKit v0.5 测试报告](hm-appkit-v0.5-test-2026-10-15.md)；此处不把后续实现写回早期试验结果。

## 网络查询

- [京麦官网](https://jm.jd.com/index/index.html)的[当前版本接口](https://jm.jd.com/homeVersionUpgradeNewServlet?platform=pc)返回 Windows 13.10.0，下载地址为 `https://s.360buyimg.com/man.assist.jd.com/JdWorkStationSetup13.10.0.exe`。
- 本次重新下载得到 210,564,752 字节（约 200.8 MiB），文件版本 13.10.0.0，Windows 签名主体为“北京京东尚科信息技术有限公司”。SHA-256：`44BE514F33B5729821615ADFA09D134D20D04818EBC9F77650B24B367877A528`。
- 包含 `JMInstaller.exe`、所需安装器 DLL 和 `install.7z`；归档中包含应用本体、运行资源和官方卸载程序。它可预先下载作为本地安装源。本轮没有断开整机网络，不能据此声称依赖检查、登录或业务操作均可完全脱网。
- [Microsoft Store 京麦条目](https://apps.microsoft.com/detail/XP9D5KR2KFMR4V)有离线分发的完整 EXE。[微软原始清单](https://storeedgefd.dsx.mp.microsoft.com/v9.0/packageManifests/XP9D5KR2KFMR4V?Market=CN)登记的 Silent 参数是 `/NCRC /S`，没有安装位置开关。商店缓存实际对应 13.4.6，210,567,152 字节，哈希 `32216c6d8435f4cfa49ec15eb4840b4e9a68c1ee840241d262089356f45a74a9`，与本地参考包一致；不是当前版或另一个 MSI。
- [微软发布要求](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msi/upload-app-packages)要求 MSI/EXE 为完整离线安装器，并为 EXE 提供静默参数。但清单中的参数不能证明第二阶段会自动完成。公开搜索未找到可验证的京麦独立 MSI、企业部署包或解决当前两阶段问题的指定目录静默方案。

## 包内分析与本机试验

1. 新旧外层 NSIS 最后都以非等待方式启动 `"$INSTDIR\JMInstaller.exe" /update=install`。`/S` 不向第二阶段转发，`/D` 只决定外层释放位置。
2. 第二阶段的 `/installpath=` 要求 UTF-8 路径的 Base64。`/localzippath=` 接受普通路径。编码方式从解析器确认，不能将普通路径直接套入此参数。
3. 原生新装入口创建安装主页、预填目录，然后等待安装按钮的信号；完整参数解析中未发现自动开始或静默开关。
4. `copyfile` 是升级链，缺少新装流程中的卸载器注册步骤。第一次原文目录试验出现升级进度，未形成完整安装，目标目录仍为空；停止无进展的试验进程后清理了解包目录。
5. 随后的正确 Base64 参数与 `/S` 组合也没有形成完整安装。隐藏启动和普通启动试验均未产生主程序、卸载记录，不能将“窗口不可见”当成安装成功。

该研究阶段没有为复制结果手工伪造卸载注册，也没有将界面自动点击标记为原生静默。当时结论是：**有完整包可以本地分发，但尚无经过本机验证的完整静默新装及自定义目录方案。** HM-AutoSetup v0.4 当时继续在严格静默模式下跳过京麦；v0.5 的常规安装预填流程见上方新报告。

代码地址、十个已识别参数、两套 worker 表及 NSIS 指令的详细分析保留在本地测试档案，未随公开源码分发。原始试验记录位于 `E:\TempSoftware\HM-AutoSetup-TestReports\jingmai-research-20261015`。

## 清理范围

本轮没有形成可卸载的完整京麦安装。清理针对试验进程、目标空目录、下载和解包的测试副本；并删除在本轮原生探测中出现且已核对为空的两个 App Paths 键。未删除无法证明本轮新增的既有 UUID 设置。

17:32 最终核对已完成，见 [清理记录](../evidence/jingmai-research/cleanup.json)：卸载项新增、变更、移除均为 0；没有新增服务、驱动或任务；试验进程、目标目录、解包目录和下载的研究安装包均已清理，两个空 App Paths 键已移除。原有应用的版本和位置与本轮开始前一致。
