# 贡献指南

本项目以 **GPL-3.0-only** 发布。提交 PR 即表示你同意将你的贡献按同一许可证授权，
并确认你有权以该许可证提供这些代码。许可证全文见 `LICENSE`，第三方与移植代码来源见
`NOTICE` / `THIRD-PARTY-NOTICES.md`。

## 开发环境

- Windows x64
- .NET 10 SDK（版本由根目录 `global.json` 固定为 10.0.301，允许 `latestPatch`）
- Inno Setup 6：仅在生成安装包时需要（`winget install --id JRSoftware.InnoSetup -e`）

## 构建与运行

```bat
build.cmd                 :: 编译 + 单文件发布 + 启动
build.cmd -NoRun          :: 只编译
package.cmd               :: 按当前版本打包（exe + 安装包 + SHA256）
package.cmd -Bump patch   :: 升版本后打包
package.cmd -SkipInstaller:: 只出单文件 exe
```

等价 PowerShell：`.\scripts\build.ps1 [-NoRun]` / `.\scripts\release.ps1 [-Bump patch] [-SkipInstaller]`。

**只运行 publish 产物**（`publish\sc-win-x64\MiHome.exe`），不要运行 `bin\Release` 下的中间产物：
两者的裁剪与自包含行为不一致，验证结果无效。完整流程见 `docs\一键编译与打包.md`。

## 工程约束

- **不要开启 `PublishTrimmed`**：XAML 仍有少量 `ReflectionBinding`，`System.Text.Json` 仍走反射序列化；
  开启后会裁坏登录（`auth.json`）、二维码与面板关闭逻辑。若将来要开，需先清完 ReflectionBinding、
  改为 JSON 源生成、补齐 `TrimmerRoots.xml` 并做完整功能回归。
- **不要开启 `PublishReadyToRun`**（体积明显增大，无收益）。
- 项目默认开启 `AvaloniaUseCompiledBindingsByDefault`：新增 View 要写 `x:DataType`；
  确实无法类型化的绑定路径显式写 `{ReflectionBinding}`。
- 应用为**托盘常驻**：关闭窗口只收进托盘；发布前脚本会按「完整 exe 名 + `ExecutablePath` 落在
  本仓库 `publish\` 下」双重限定精确结束旧实例，禁止使用通配/前缀匹配杀进程。
- 版本号唯一来源是 `MiHome.csproj` 的 `<Version>`；打包失败重试时**不要**再带 `-Bump`。

## 提交前检查

1. `dotnet build -c Release` 0 错误。
2. 功能回归至少覆盖：扫码登录/二维码、已登录态恢复、设备列表、开关/灯/空调/窗帘控制、
   场景执行、设置持久化、托盘与关闭行为。
3. **禁止提交**：登录凭证（`auth.json`、`app.json`）、设备缓存（`devices.json`）、
   探针输出（含真实 did 与云端报文）、崩溃日志、调试截图、构建产物。
4. 新增第三方依赖时，同步更新 `THIRD-PARTY-NOTICES.md`。

## 代码风格

- 注释与文档用简体中文；标识符用英文。
- 保持现有 MVVM 分层：`Views/` 只放视图与极薄的 code-behind，业务逻辑放 `ViewModels/`，
  云端访问放 `Services/`。
- 新增设备能力优先走 MIoT 规格驱动（spec 属性/动作），避免为单个型号写死分支。
