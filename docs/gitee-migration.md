# Gitee 同步与更新分发

已接入公开仓库 [huanmeng-it/HM-AppKit](https://gitee.com/huanmeng-it/HM-AppKit)。v0.15 起，助手自身更新优先使用 Gitee 正式版及附件，必要时回退 GitHub；不需要用户登录 Gitee，也不在客户端保存发布凭据。v0.14 的 ZIP 和 SHA-256 已从 GitHub 原样上传至 Gitee，真实匿名下载通过完整性、解包和程序版本校验。

## 自动同步

仓库提供 [Sync HM AppKit to Gitee](../.github/workflows/sync-gitee.yml) 工作流和 [同步脚本](../scripts/Sync-Gitee.ps1)。首次启用需要在 GitHub 仓库的 Actions Secrets 中配置 `GITEE_TOKEN`；没有凭据时同步会明确报错，不会声称已经同步成功。

配置凭据后：

1. 向 GitHub `main` 推送提交，自动同步 `main` 和标签到 Gitee。
2. 推送版本标签，由现有 `Publish HM AppKit` 完成测试、构建及 GitHub 正式发布。该流程成功后自动触发 Gitee 同步，上传 GitHub 同标签的原始 ZIP 与 SHA-256 文件，不重新构建或压缩。
3. 同步完成前匿名核对 Gitee 附件的字节数与 SHA-256。已有同名且内容相同的附件直接复用，缺少附件可补传；同名内容不同则停止，不覆盖已公开文件。

代码同步使用正常推送，不执行强制推送、镜像删除或覆盖独立提交。遇到分叉、标签冲突、过期令牌或网络错误时，该同步任务会失败，GitHub 已发布版本继续可用。人工处理后可以重新运行同步任务。

如需补同步旧版，在 GitHub Actions 打开 `Sync HM AppKit to Gitee`，点击 `Run workflow`，使用 `main` 分支，输入已发布标签（例如 `v0.15`），或填写 `latest`。该入口仅同步已有正式版，不能创建或改写 GitHub Release。

### 首次凭据配置

1. 在 Gitee 私人令牌页面生成仓库级令牌，选择仓库 `huanmeng-it/HM-AppKit`，勾选 `projects`。Gitee 强制附带 `user_info`；无需勾选 Pull Requests、Issues、Hook 或其他仓库。
2. 将令牌直接存入 GitHub 仓库 `19016563235/HM-AppKit` → Settings → Secrets and variables → Actions，名称为 `GITEE_TOKEN`。不要提交令牌到代码、粘贴到聊天或写入程序配置。
3. 手动运行同步工作流，输入 `v0.15` 完成首次真实验证。当前版本已在 Gitee 发布，因此相同附件应被校验后复用。

令牌仅注入受信任主分支工作流的同步步骤。HTTP 认证放在请求正文，Git 认证放在子进程环境中；客户端 EXE、下载 URL、Git 远程地址和仓库文件都不包含令牌。令牌撤销或过期后，更新 GitHub 中的 Secret 并重新运行任务。

Gitee 未发布新版期间，已升级到 v0.15 的助手会查询 GitHub；v0.14 及更早的助手仍先通过 GitHub 下载一次新版，然后使用新的更新源。

### 本地只读验证

使用 PowerShell 7：

```powershell
./tests/gitee-sync/Test-Sync-Gitee.ps1
./scripts/Sync-Gitee.ps1 -Tag v0.15 -PlanOnly
```

`-PlanOnly` 不需要发布凭据，不推送代码或上传文件。

## 镜像与限制

2026-10-16 查询的 Gitee 官方帮助说明支持 Pull 方向的 GitHub 仓库镜像，通过 Webhook 触发分支、标签和提交同步。页面保留历史产品策略提示，帐号能否开通应以实际仓库设置为准，本次未据此声称自动镜像已启用。

当前压缩包约 62 MB，低于官方帮助列出的普通项目单附件 100 MB 限制；普通仓库附件总额为 1 GB，维护时需要控制历史包的保留数量。

- [Gitee 官方：仓库镜像管理](https://help.gitee.com/repository/settings/sync-between-gitee-github)
- [Gitee 官方：发行版与附件限制](https://help.gitee.com/repository/release/intro)
- [Gitee 官方：API 文档](https://gitee.com/api/v5/swagger)
