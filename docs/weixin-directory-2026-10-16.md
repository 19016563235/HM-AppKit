# 微信 4.1.15.53 指定目录参数核验

2026-10-16 的另一台电脑日志显示，使用 `/S /D=C:\Software\Weixin` 后，系统记录位置为 `C:\Program Files\Tencent\Weixin`，HM AppKit 因目录不符正确报告需关注。此处下载与缓存校验已通过，问题发生在安装参数处理阶段。

本次只读分析的官方 `WeChatWin_4.1.15.exe` 为 256,557,480 字节，SHA-256 为 `AA727E6CC74CD7C4E101C350D125508B0230A8A19C1F5304BF6F33DDE2E758C5`，与 [Microsoft WinGet 对应版本清单](https://github.com/microsoft/winget-pkgs/blob/master/manifests/t/Tencent/WeChat/Universal/4.1.15.53/Tencent.WeChat.Universal.installer.yaml) 一致；发布者签名此前已核验为 Tencent Technology (Shenzhen) Company Limited。清单只声明 Nullsoft 类型，没有说明厂商的自定义目录解析方式。

NSIS [官方变量文档](https://nsis.sourceforge.io/Reference/%24CMDLINE) 说明，标准 `/D=` 参数不会出现在脚本的 `$CMDLINE` 中。[官方实现](https://github.com/kichik/nsis/blob/master/Source/exehead/Main.c) 会在解析该参数时截断传给脚本的命令行。微信本版初始化脚本随后重新设置安装目录，再把剩余参数交给自身插件。因此标准 `/D=` 的目录值虽然先被 NSIS 接收，却不能到达微信后续覆盖目录的入口。

对原包只读解包与静态分析得到：

- NSIS 初始化指令 616–630 读取已有安装位置，缺少记录时重设为 Program Files 下的腾讯目录。
- 指令 771–782 调用 `WeChatInstallDll.dll` 的 `GetParameters` 并接收目录值，将其重新赋给 `$INSTDIR`。
- 该插件入口 RVA 为 `0x28520`；它搜索 `/D`，取后续内容并移除一个分隔字符。使用末尾 `/D 空格路径` 可使目录保留在 `$CMDLINE` 中，随后由厂商入口接收；路径整体不加引号，后面不能再接其他参数。

HM AppKit 因此为微信显式启用 `weixin` 适配，生成 `/S /D C:\Software\Weixin`；含空格或中文的路径也保留为完整原始尾部。普通 NSIS 应用继续使用各自的 `/D=` 规则，目录参数仍由计划统一放在末尾。执行不经过命令解释器，并拒绝引号或控制字符。

新增回归验证使用真实内置目录配置、生产命令构造代码和静态分析得到的参数契约，覆盖普通路径、空格、中文、非静默规划及拒绝非法配置。这是**静态参数核验与命令构造测试**：未运行微信安装器、插件或卸载器，未修改本机微信及其注册表，也未宣称已经在干净系统完成新版指定目录安装复测。安装后的实际目录仍必须由引擎检查；已有安装保留原位置，不自动迁移。
