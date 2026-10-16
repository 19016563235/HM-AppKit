# Gitee 同步与更新分发

2026-10-16 查询的 Gitee 官方帮助说明支持 Pull 方向的 GitHub 仓库镜像，通过 Webhook 触发分支、标签和提交同步。相关页面仍保留历史产品策略提示，实际开通能力应以目标帐号当时的仓库设置为准；也可以在 GitHub 构建流程中独立推送代码。

镜像同步的是 Git 数据。Release 是托管平台另外提供的对象，安装器 ZIP 与 SHA-256 附件需要额外创建发行版并上传，不能只把 GitHub 仓库导入 Gitee 就认为附件已经可用。当前 HM AppKit 压缩包约 62 MB，低于官方帮助列出的普通项目单附件 100 MB 限制；该文档同时列出普通仓库附件总额 1 GB，后续需要控制历史包的保留数量。

建议保留 GitHub 主仓和自动构建，发布后将同一 ZIP 和校验文件上传至 Gitee，再验证公开下载和校验值，最后把助手更新检查和下载配置接入已核验的 Gitee 仓库。实际迁移需要目标仓库和相应发布权限，本次用户要求的是后续迁移，当前仍使用已配置的 GitHub 更新源。

- [Gitee 官方：仓库镜像管理](https://help.gitee.com/repository/settings/sync-between-gitee-github)
- [Gitee 官方：发行版与附件限制](https://help.gitee.com/repository/release/intro)
