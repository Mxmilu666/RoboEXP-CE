# GitHub Release

工作流：`.github/workflows/release.yml`。用户 README 保持为使用说明，发版操作集中在此。

## 一次性配置

1. 确认当前仓库允许 GitHub Actions 运行及使用内置 `GITHUB_TOKEN` 创建 Release；无需个人访问令牌。
2. 核对原版 RoboEXP 资源、Pixiv 开屏图的再分发权利和许可义务，以及按需安装的官方工具链许可。
3. 确认后，在 **Settings → Secrets and variables → Actions → Variables** 添加：
   - `RELEASE_REDISTRIBUTION_CONFIRMED` = `true`

未设置该变量，流程会明确失败，不会自动发布。官方可下载地址和 PID 署名不等于获得再分发授权。

原版安装程序和资源程序集不进入仓库或发布包，CI 也不下载它们：

- `src/RoboExp.App/Assets/LegacyResources/`：一次提取的语言表、控件图标和参数界面图片，直接内置进 CE。
- `assets/robot-resources.zip`：机器人头文件、库、例程及说明等数据；不含原版 `.exe`、`.dll`、`.pdb`。旁边的 `.sha256` 用于构建时校验。
- 发布构建不下载或打包 GCC；首次编译按需从 Arm 官方源下载完整归档，校验固定的 SHA-256，再安装精简 C 工具链并保留原有许可证。CI 测试独立安装原生工具链，不将其复制到发布包。

仅更新上游资源时，在本地用 `dotnet run --project packaging/ExtractAssets -- <已解压原版目录> .` 重新提取，检查资源变更并提交数据文件。原始安装包仍受 `.gitignore` 排除。

## 发布

1. 修改 `packaging/VERSION`，例如 `0.1.1`，提交本次发布代码。
2. 创建并推送相同版本的标签：

```sh
git tag v0.1.1
git push origin v0.1.1
```

仅接受 `v主版本.次版本.修订版本`，必须与标签对应提交中的 `packaging/VERSION` 一致；目前不支持预发布后缀。

也可以在 **Actions → Release → Run workflow** 输入已经存在的版本标签。手动运行不会替你创建标签。

## 产物

全部平台保留自包含 .NET 运行时和机器人资源，不再附带 GCC 或 `.pdb`（包括原生 NuGet 包带来的符号文件）。用户无需安装 .NET 或导入原版安装包，打开应用、查看和编辑工程可离线进行。

若未配置可用的 Arm GCC，首次编译需要联网下载官方完整归档并校验 SHA-256，安装时只保留受支持控制器编译/链接所需的 C 工具链及原有许可证。精简降低安装后的磁盘占用，**不会降低首次完整归档的网络下载量**；安装成功后可离线编译。

| 平台 | 架构 | 文件 |
| --- | --- | --- |
| Windows | x64、ARM64 | 每个架构各一份安装器 `.exe` 和免安装 `.zip` |
| Linux | x64、ARM64 | 每个架构各一份 `.tar.gz` |
| macOS | Apple Silicon、Intel | 每个架构各一份 `.dmg`，应用临时签名，未公证 |
| 校验 | 全部 | `SHA256SUMS.txt` |

Windows ARM64 使用官方 x64 GCC，依赖 Windows 11 ARM 兼容层；Intel Mac 使用官方 14.2.rel1 工具链，其余平台使用 15.3.rel2。

macOS 将机器人资源、`LICENSE` 和 `THIRD-PARTY-NOTICES` 放在 `Contents/Resources`，应用自动发现机器人资源。工具链安装在用户数据目录，不修改或重新签名应用包。所有发布路径在归档或签名前递归删除 `.pdb`。

流程先校验并解压仓库中的机器人资源包，通过 `ToolchainCatalog.ForCurrentHost()` 和 `ToolchainInstaller.InstallAsync()` 单独安装原生 Linux 编译器并运行测试，再并行构建六个目标。所有目标检查发布目录含自包含运行时、许可证及归档内全部机器人资源，且不含 GCC 或 PDB；这只是轻量静态载荷校验，不代表应用启动或首次编译下载流程已实测。所有目标成功后，校验八个二进制产物齐全，生成 SHA-256，再创建草稿、上传并公开发布；不会因单个平台失败而发布残缺版本。

重新运行可继续未公开的草稿并覆盖同名附件；已公开版本不会被覆盖，应发布新标签。构建任务只有只读仓库权限，仅最终发布任务有 `contents: write`。

## 本地验证

```sh
python3 packaging/release.py validate v0.1.0
bash packaging/windows/build-installer.sh win-x64
bash packaging/windows/build-installer.sh win-arm64
bash packaging/publish.sh linux-x64 linux-arm64
bash packaging/macos/build-app.sh --arch both
```

按当前版本调整示例标签。Windows 安装器需要 NSIS、Python 3；macOS DMG 需要 macOS 的签名和磁盘镜像工具。CI 中的构建成功不代表真机安装、控制器下载和签名信任已验证。
