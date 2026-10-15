# 应用来源与参数核验

更新日期：2026-10-15（Asia/Shanghai），对应 HM AppKit v0.5。11 款内置应用均有 `OnlineInstaller`，从厂商下载原始安装包，支持独立保留；公共软件和剪映也不再通过 WinGet 进程安装。在线来源与本地外置参考包独立，发布包不包含厂商安装程序。

## 七款平台软件在线来源

开启保留安装包后，启动检查已有缓存，安装时也检查官方当前版本并复用已校验的包，不固定历史版本。下表保留 2026-10-15 v0.4 阶段实际 HTTP 核验结果；这些厂商来源在 v0.5 继续使用，后续以官方发布内容为准。

| 软件 | 动态来源与提取字段 | 本次响应 |
| --- | --- | --- |
| 千牛 | [淘宝官方版本接口](https://pc.work.taobao.com/version/getOfficialVersion)，`model` 内 `platform=pc` 的 `extend.downloadUrl64New` | 9.97.74N；`download.alicdn.com` 的 NK x64 包 |
| 抖店 | [官方 Tron 更新接口](https://tron.jiyunhudong.com/api/sdk/check_update?pid=7005992474254514440&branch=master&buildId=&uid=)，`data.manifest.win32.extra.downloadUrlX64` | 1.1.12；`lf3-cdn-tos.bytescm.com` 的 Windows x64 包 |
| 京麦 | [京东官网版本接口](https://jm.jd.com/homeVersionUpgradeNewServlet?platform=pc)，`homeVersionUpgradeList.downloadUrl` | 13.10.0，发布时间 2026-09-17；`s.360buyimg.com/man.assist.jd.com/JdWorkStationSetup13.10.0.exe` |
| 拼多多 | [官方 lateststable 地址](https://meta.pinduoduo.com/api/one/app/v1/lateststable?appId=com.xunmeng.workbench&platform=windows&subType=main) | 重定向至 `cd.pddpic.com` 当前 EXE，221,643,256 字节 |
| 千帆 | [小红书官方更新元数据](https://walle.xiaohongshu.com/client-update/latest_beta_win.yml)，读取 `path` 并解析相对地址 | 带官方客户端请求头返回 1.2.6，253,093,152 字节，安装文件重定向至 `eva-static.xhscdn.com` |
| 快语 | [快手官方客服工作台](https://im.kwaixiaodian.com/)，页面配置 `windowsUrl` | 1.1.4；`p5-ec.ecukwai.com/udata/pkg/ESHOP-ELECTRON/release/1.1.4-1722/kwaixiaodian-Setup-1.1.4.exe` |
| 得物 | `h5static.dewu.com/mcs-platform-latest/得物商家客服3.0.exe`，不含版本号的当前 EXE；[官方更新元数据](https://h5static.dewu.com/mcs-platform-latest/updater_windows.json) 交叉核对 | 6.2.0，9,907,720 字节；元数据发布日期 2026-10-09 |

来源链：

- [千牛下载页](https://work.taobao.com/download.html) 引用官方 `g.alicdn.com/crs-qn/qn-new-portal/0.0.26/js/download.js`，请求上述版本接口。使用完整 x64 包，避免 JS 旧默认版本或在线下载器。
- [抖店下载页](https://jinritemai.com/) 的官方页面 JS `index.19574816.js` 使用 `TronClientWebChecker`，`pid=7005992474254514440`、`region=cn`、`branch=master`；官方 vendor JS 将 cn 对应到 `tron.jiyunhudong.com/api/sdk/check_update`。未把“抖音聊天”当成商家客户端。
- [京麦官网](https://jm.jd.com/index/index.html) 的 `scripts/index.js` 调用 `/homeVersionUpgradeNewServlet`，只选稳定 `pc`，不选 `pc-beta` 或 Mac。
- 拼多多地址来自用户提供且签名有效的 PddInstall.exe 内嵌字符串，实际 HTTP 重定向已验证。地址不包含用户 Cookie 或登录令牌。
- 千帆来源及请求头来自签名有效的 1.2.6 包内 `app-update.yml` 和 `app.asar`，仅静态读取。元数据头为 `x-system-platform: windows`、`x-system-arch: x64`、`x-system-version: windows11`、`x-system-version-code: 10.0.26100`。不带这些头会返回旧 1.1.69。下载 EXE 本身无需请求头，重定向已验证。`latest_beta_win` 是原客户端使用的厂商通道名，本助手沿用该通道。
- 快语解析官方页面实时注入的 `windowsUrl`，避免误选同页存在的 Chrome 安装包链接。
- 得物元数据地址来自签名有效的程序配置。元数据下载项是 Tauri 更新 ZIP，助手使用同一官方发布目录中实际下载核验的当前 EXE。此次 EXE 版本 6.2.0，NSIS，Windows 签名有效，发布者“上海得物信息集团有限公司”，SHA-256 为 `515D125AB6361655AB52B67ECC906C375C091300959B8F6F43554EF6427F59CD`。在线不固定此哈希，每次改为验证签名及允许的厂商名称。

在线直连只接受明确允许的 HTTPS 域名，并在执行前核对 Windows 数字签名及准确发布者名称。官方渠道可能按系统、区域或放量策略返回不同版本，“当前版本”指渠道当时提供的版本。

## v0.5 安装包保留与更新

- “保留安装包”默认开启，安装包与索引保存在 `HM AppKit.exe` 旁的 `Local Installer/<应用Id>/`。安装器与安装包分别存放，包不会被打入发布压缩包。
- 启动仅检查已经登记的在线缓存；没有缓存的应用在实际安装时下载。此检查不会安装、升级或迁移系统中的应用，也不改变用户选择的在线/本地来源。
- 复用前重新核对文件哈希和厂商签名，再以官方版本元数据、清单哈希或 HTTP 版本标识判断更新。新包校验通过后才更新保留记录。
- 网络不可用或更新查询失败时，仅允许复用重新校验通过的原包，并在日志中明确无法确认其为最新版。这不代表软件依赖、登录或业务功能均可脱网。
- 关闭保留后，新下载使用临时缓存并在安装结束后清理；已经保留的安装包不会因关闭选项自动删除。本地参考包仍按固定文件名、SHA-256 和签名核验，不会自动替换 `F:\客服软件` 的原文件。

当前实现及验证范围见 [HM AppKit v0.5 测试报告](../docs/hm-appkit-v0.5-test-2026-10-15.md)；v0.4 的真实安装结果仍保留在原报告中。

## 静默和目录参数

| 安装器 | 软件 | 参数依据 |
| --- | --- | --- |
| NSIS | 微信、ToDesk、抖店、千帆、快语、得物 | `/S`，末尾未加引号的 `/D=绝对目录`，含空格路径也按此规则 |
| 千牛自定义 EXE | 千牛 9.95.01N NK x64 | 真实解析器处理 `/S`、`/D=目录`，支持键值引号 |
| 拼多多自定义 EXE | 拼多多 3.6.8.3 / 3.7.3.7 | `/slient`、`/installpath=目录`；目标目录须提前存在，否则回退 C 盘；已修复并实测复验 |
| 京麦专用适配器 | 官网 13.10.0、本地 13.4.6 | 只读解包已校验官方容器，打开可见的 `JMInstaller.exe` 常规向导，并按厂商编码规则预填目标目录；需用户确认安装 |

[NSIS 官方安装器文档](https://nsis.sourceforge.io/Docs/Chapter3.html#installerusage) 给出参数大小写、次序和引号规则。厂商脚本仍可覆盖默认行为。v0.4 实测表明京麦 `/S` 只控制外层，第二阶段仍等待交互，因此当时严格静默模式跳过。v0.5 的两种来源均配置 `Adapter=jingmai`、`InteractiveFallback=true`：即使全局优先静默，也按“常规安装·预填目录”打开官方向导，完成后仍核对实际安装位置，不声称完整静默。历史研究见 [京麦 v0.4 研究记录](../docs/jingmai-research-2026-10-15.md)，当前验证见 [v0.5 测试报告](../docs/hm-appkit-v0.5-test-2026-10-15.md)。

本地文件、签名及参数见 [local-installers.md](local-installers.md)，v0.4 六款成功安装及卸载记录见 [本机实测报告](../docs/real-install-test-2026-10-15.md)。

快语官网当前包使用有效签名主体“北京快手科技有限公司”，已在在线配置中加入该准确名称，保留英文名称，不放宽签名检查。[快手官方协议](https://open.kuaishou.com/platform/protocol)确认该公司为运营主体。

## 公共软件与剪映的当前在线来源

飞书、微信和剪映通过 Microsoft 仓库的 [WinGet 社区清单](https://github.com/microsoft/winget-pkgs) 解析版本目录、适用架构的厂商下载地址和 SHA-256，再由 HM AppKit 直接下载、校验和保留厂商原包。安装命令来自本项目已核验的参数配置，不执行清单中的任意命令；内置流程不依赖 WinGet 安装进程。社区收录可能晚于厂商官网。

ToDesk 改为解析 [官网稳定版下载页](https://www.todesk.com/download.html) 的 `win_link`，不再使用旧 WinGet 4.7.4.3。2026-10-15 已下载官网 5.2.1.0，159,362,472 字节，NSIS 3 Unicode，签名有效，发布者 `Hainan YouQu Technology Co., Ltd`；仅下载和静态核验，未执行此安装包。详情见 [下载核验记录](../evidence/v0.5-downloads/ToDesk_5.2.1.0-verification.json)。

| 软件 | 来源标识 | 2026-10-15 已记录版本 | 静默与目录依据 |
| --- | --- | --- | --- |
| 飞书 | `b/ByteDance/Feishu` | 8.2.14（v0.5 清单实时解析） | `--command=quiet_install`、`--target_dir=目录`，用户范围 |
| 微信 | `t/Tencent/WeChat/Universal` | 4.1.15.53（v0.5 清单实时解析） | Nullsoft，机器范围，直接传入 `/S`、末尾 `/D=目录` |
| ToDesk | 官网 `win_link`，`dl.todesk.com` | 5.2.1.0（v0.5 官网下载核验） | Nullsoft，机器范围，直接传入 `/S`、末尾 `/D=目录` |
| 剪映专业版 | `b/ByteDance/JianyingPro` | 11.6.0.14552（v0.5 清单实时解析） | `/silent_install=1`、`/install_path=目录`，用户范围 |

上述版本是已记录的核验样本，不固定运行时版本。v0.5 的 [11 款在线来源解析记录](../evidence/v0.5-source-live/source-resolution.json) 已取得这些地址和三款清单哈希；这项解析测试没有下载或执行安装包。三款清单来源在启动缓存检查和安装时解析 Microsoft 仓库当前的稳定数字版本；解析不到唯一适用的安装器、类型变化或下载域名不符时停止，不猜测链接。下载同时核对清单 SHA-256 和厂商签名。

此前核验清单：[飞书](https://github.com/microsoft/winget-pkgs/blob/master/manifests/b/ByteDance/Feishu/8.2.14/ByteDance.Feishu.installer.yaml)、[微信](https://github.com/microsoft/winget-pkgs/blob/master/manifests/t/Tencent/WeChat/Universal/4.1.15.53/Tencent.WeChat.Universal.installer.yaml)、[剪映](https://github.com/microsoft/winget-pkgs/blob/master/manifests/b/ByteDance/JianyingPro/11.6.0.14552/ByteDance.JianyingPro.installer.yaml)。旧版 [ToDesk WinGet 4.7.4.3 清单](https://github.com/microsoft/winget-pkgs/blob/master/manifests/y/Youqu/ToDesk/4.7.4.3/Youqu.ToDesk.installer.yaml) 仅保留为历史参考。

飞书、微信、ToDesk、剪映、千牛、抖店保留真实包 ID 供识别或维护参考；内置 `OnlineInstaller` 优先。京麦、拼多多、千帆、快语、得物未伪造 WinGet ID。用户添加且没有直连配置的自定义应用仍可走原 WinGet 安装流程。
