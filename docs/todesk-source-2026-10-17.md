# ToDesk 个人版下载来源修复

2026-10-17 的用户日志中，ToDesk 两次在签名发布者检查时停止；其他应用继续安装。日志中的包体为 54.4 MiB，发布者为 `Shanghai Jiuchi Network Technology Co., Ltd.`。这不是个人远控版更换了发布者，而是旧下载规则误选了同一官网下载页中的另一款产品。

只读检查 [ToDesk 官方下载页](https://www.todesk.com/download.html) 发现，当前页面把个人版的稳定 Windows 地址写成 NUXT 数据变量 `win_link:j`，变量 `j` 对应 `https://dl.todesk.com/irrigation/ToDesk_5.2.2.0.exe`。页面另有 `daas_win_link:"...ToDesk_DaaS_v1.1.1.1.exe"`。旧规则 `win_link:"(?<url>https[^"]+)"` 无法匹配变量值，却能匹配 `daas_win_link` 的后半部分，因此错误地下载了云电脑客户端。

本次分别从官网下载这两个文件，只核对文件信息、SHA-256 和 Windows Authenticode，不运行安装器：

| 项目 | 个人远控版 | 旧规则误选的云电脑 |
| --- | --- | --- |
| 官方文件 | `irrigation/ToDesk_5.2.2.0.exe` | `daas/ToDesk_DaaS_v1.1.1.1.exe` |
| 字节数 | 159,375,560 | 57,012,640 |
| 文件版本 | 5.2.2.0 | 1.1.1.1.678 |
| Authenticode | Valid | Valid |
| 发布者 | Hainan YouQu Technology Co., Ltd | Shanghai Jiuchi Network Technology Co., Ltd. |
| SHA-256 | `2E8573C9EB4DD0A934DE8CFE6F5DF9B8D007C2EF29DE725D96A704D268B5B2FA` | `A84EE0A460A16D32B54072F868B0C5FD301CE92390469B1621F4771452560F51` |

原有的精确发布者校验阻止了云电脑客户端执行；修复保留该校验，不把云电脑发布者加入 ToDesk 个人版白名单。

ToDesk 配置改为显式的 `todesk-windows` 元数据格式。专用读取器仅解析页面中的 NUXT 数据，支持文字值和参数变量映射，拒绝函数调用、属性访问及其他 JavaScript 表达式。它只取 `data[].clientInfo` 的精确 `win_link`、`win_version`，要求版本一致，地址必须是 `https://dl.todesk.com/irrigation/ToDesk_<四段版本>.exe`，不接受 DaaS、精简版、下载器、灰度入口或歧义数据；官网结构再变时停止下载并报告原因。

该解析格式计入 ToDesk 缓存来源身份，旧规则产生的记录不能作为新来源的离线备用包。其他应用未启用该格式，原有缓存身份保持不变。更新失败不会删除旧缓存文件。

验证包含 13 项 ToDesk 回归（变量映射、多产品、灰度、版本不符、重复数据、外部域名、解析资源上限及拒绝执行表达式），以及缓存身份兼容和解析器切换失效测试。另通过生产 `InstallerPayloadProvider` 实际解析官网并下载个人版 5.2.2.0，签名、文件长度和哈希均与上表一致；第二次官网返回 304，复用同一个已核验缓存文件。公开摘要见 [核验记录](../evidence/todesk-source-2026-10-17/verification.json)。

本次未执行任何厂商安装器、卸载器或客户端，也未修改现有 ToDesk、服务、注册表及用户安装包缓存。这些结果验证下载来源、完整性检查与缓存复用；不等同于本机再次完成静默安装测试。
