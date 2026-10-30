# GitHub Release

工作流：`.github/workflows/release.yml`。用户 README 保持为使用说明，发版操作集中在此。

## 一次性配置

1. 确认当前仓库允许 GitHub Actions 运行及使用内置 `GITHUB_TOKEN` 创建 Release；无需个人访问令牌。
2. 核对原版 RoboEXP 资源、Pixiv 开屏图及随包工具链的再分发权利和许可义务。
3. 确认后，在 **Settings → Secrets and variables → Actions → Variables** 添加：
   - `RELEASE_REDISTRIBUTION_CONFIRMED` = `true`

未设置该变量，流程会明确失败，不会自动发布。官方可下载地址和 PID 署名不等于获得再分发授权。

原版安装程序和资源程序集不进入仓库或发布包，CI 也不下载它们：

- `src/RoboExp.App/Assets/LegacyResources/`：一次提取的语言表、控件图标和参数界面图片，直接内置进 CE。
- `assets/robot-resources.zip`：机器人头文件、库、例程及说明等数据；不含原版 `.exe`、`.dll`、`.pdb`。旁边的 `.sha256` 用于构建时校验。
- GCC 仍从 Arm 官方源下载并校验，属于程序正常需要的编译工具链，不是原版 RoboEXP 安装包。

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

全部平台都包含机器人资源和目标平台的 Arm GNU Toolchain，用户无需再次导入或下载编译器。

| 平台 | 架构 | 文件 |
| --- | --- | --- |
| Windows | x64、ARM64 | 每个架构各一份安装器 `.exe` 和免安装 `.zip` |
| Linux | x64、ARM64 | 每个架构各一份 `.tar.gz` |
| macOS | Apple Silicon、Intel | 每个架构各一份 `.dmg`，应用临时签名，未公证 |
| 校验 | 全部 | `SHA256SUMS.txt` |

Windows ARM64 使用官方 x64 GCC，依赖 Windows 11 ARM 兼容层；Intel Mac 使用官方 14.2.rel1 工具链，其余平台使用 15.3.rel2。

macOS 将机器人资源和 GCC 放在 `Contents/Resources`，应用在该位置自动发现；这样不会把工具链目录误作嵌套应用签名。

流程先校验并解压仓库中的机器人资源包，准备原生 Linux 编译器并运行测试，再并行构建六个目标。Linux x64 和 macOS ARM64 额外用包内 GCC 实际编译一个 C 文件。所有目标成功后，校验八个二进制产物齐全，生成 SHA-256，再创建草稿、上传并公开发布；不会因单个平台失败而发布残缺版本。

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
