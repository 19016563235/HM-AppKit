# Gitee 同步与更新分发

已接入公开仓库 [huanmeng-it/HM-AppKit](https://gitee.com/huanmeng-it/HM-AppKit)。v0.15 起，助手自身更新优先使用 Gitee 正式版及附件，必要时回退 GitHub；不需要用户登录 Gitee，也不在客户端保存发布凭据。v0.14 的 ZIP 和 SHA-256 已从 GitHub 原样上传至 Gitee，真实匿名下载通过完整性、解包和程序版本校验。

## 每次发布

1. 在 GitHub 主仓推送版本提交与标签，由现有 Actions 完成验证和构建。
2. 将 GitHub 的分支和标签同步到 Gitee。网页导入、手动同步和镜像同步只同步 Git 数据，不能替代下一步的发行版附件上传。
3. 在 Gitee 创建同标签的正式发行版，上传 GitHub 生成的 `HM-AppKit-win-x64.zip` 和 `HM-AppKit-win-x64.zip.sha256`，不得重新压缩或修改文件。
4. 确认匿名访问发行版及两个附件成功，并用生产更新器完成下载校验。

本次已配置客户端更新源并完成 Gitee 网页发布；尚未配置发布 Token 或跨平台自动发布任务。GitHub 的后续构建不会因此自动把 ZIP 上传到 Gitee。Gitee 未发布新版期间，已升级到 v0.15 的助手会查询 GitHub；v0.14 及更早的助手仍先通过 GitHub 下载一次新版，然后使用新的更新源。

## 镜像与限制

2026-10-16 查询的 Gitee 官方帮助说明支持 Pull 方向的 GitHub 仓库镜像，通过 Webhook 触发分支、标签和提交同步。页面保留历史产品策略提示，帐号能否开通应以实际仓库设置为准，本次未据此声称自动镜像已启用。

当前压缩包约 62 MB，低于官方帮助列出的普通项目单附件 100 MB 限制；普通仓库附件总额为 1 GB，维护时需要控制历史包的保留数量。

- [Gitee 官方：仓库镜像管理](https://help.gitee.com/repository/settings/sync-between-gitee-github)
- [Gitee 官方：发行版与附件限制](https://help.gitee.com/repository/release/intro)
- [Gitee 官方：API 文档](https://gitee.com/api/v5/swagger)
