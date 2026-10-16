# HM AppKit v0.16

新增 GitHub → Gitee 自动同步工作流。配置仓库 Secret `GITEE_TOKEN` 后，`main` 提交自动同步代码；GitHub 正式发布成功后自动同步标签、ZIP 和 SHA-256 附件。

- 同步 GitHub 原始构建，不重新压缩；上传后匿名核对文件大小与 SHA-256。
- 重复运行复用已校验的附件，支持补传缺失文件；内容冲突或代码分叉时停止，不强制覆盖。
- 同步任务串行排队，GitHub 已发布版本不受 Gitee 同步失败影响；支持手动补同步已有正式版。
- 客户端更新仍优先 Gitee 并保留 GitHub 回退，原配置和 `Local Installer` 缓存继续保留。

同步凭据仅用于 GitHub Actions，不写入源代码、安装器、下载地址或日志。配置及故障恢复见 `docs/gitee-migration.md`。
