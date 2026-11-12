# MiHomeHub

基于 **Avalonia 11 + .NET 10** 的 Windows 桌面客户端，通过米家云端接口管理设备、场景与自动化。
扫码登录即可使用；深色玻璃拟态卡片界面，支持托盘常驻与桌面分区卡片。

本项目以 **GPL-3.0-only** 开源，许可证全文见 [LICENSE](LICENSE)，移植代码来源与版权声明见
[NOTICE](NOTICE)，第三方依赖见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

## 功能

- 扫码登录、免扫码一键登录（复用本地 `passToken`）、退出登录、多家庭切换
- 设备列表 / 房间分组 / 全局搜索 / 设备详情抽屉
- 灯、空调、窗帘、开关控制面板；按 MIoT 规格生成的通用属性控件（开关、滑杆、下拉、动作按钮）
- 传感器读数、耗材提醒、环境概览、设备状态轮询
- 手动场景执行、场景与自动化编辑、设备组与虚拟子设备控制
- 小爱音箱：语音指令下发、音量与播放控制
- 桌面分区卡片（多显示器）、托盘快捷菜单、深浅主题与外观设置
- 本地缓存首屏、设备真机图标异步加载

## 技术栈

| 项目 | 内容 |
| --- | --- |
| UI | Avalonia 11.3.9（Themes.Fluent、Svg.Skia） |
| 运行时 | .NET 10，`win-x64` 自包含单文件发布 |
| MVVM | CommunityToolkit.Mvvm 8.4.0 |
| 二维码 | QRCoder 1.6.0 |
| 构建 / 打包 | `build.cmd`、`package.cmd`、Inno Setup 6 |

## 目录

```
MiHomeHub/
├── MiHome.csproj / Program.cs / App.axaml(.cs)
├── Assets/          # 矢量图标与程序图标
├── Controls/        # 自定义控件（虚拟化网格、拖拽排序、浮层等）
├── Converters/      # XAML 值转换器
├── Themes/          # 画笔、模板与样式
├── Models/          # 设备、场景、规格、设置等模型
├── Services/        # 登录与加解密、云端 API、规格/图标、缓存、托盘与桌面钩子
├── ViewModels/      # MVVM 视图模型（按功能拆分 partial）
├── Views/           # AXAML 视图与 code-behind
├── docs/            # 构建与技术文档
├── installer/       # Inno Setup 脚本
├── scripts/         # 编译、发布、打包与 GitHub Release 脚本
└── .github/         # CI 与 issue 模板
```

## 快速开始

环境：Windows x64、.NET 10 SDK（版本由 `global.json` 固定为 `10.0.301`，允许 `latestPatch`）。
仅生成安装包时需要 Inno Setup 6。

```bat
build.cmd                  :: 编译 + 单文件发布 + 启动
build.cmd -NoRun           :: 只编译
package.cmd                :: 按当前版本打包（exe + 安装包 + SHA256）
package.cmd -Bump patch    :: 升版本后打包
package.cmd -SkipInstaller :: 只出单文件 exe
```

产物：`publish\sc-win-x64\MiHome.exe`（启动与回归的固定路径）、
`publish\release\<版本>\`（发版目录）。完整流程见 [docs/一键编译与打包.md](docs/一键编译与打包.md)。

> 只运行 publish 产物，不要运行 `bin\Release` 下的中间产物（裁剪与自包含行为不一致）。
> 应用为托盘常驻，关闭窗口只收进托盘；发布脚本会精确结束占用发布 exe 的旧实例。

等价的 PowerShell 入口：`.\scripts\build.ps1 [-NoRun]`、`.\scripts\release.ps1 [-Bump patch] [-SkipInstaller]`。

## 使用

1. 运行 `publish\sc-win-x64\MiHome.exe`。
2. 首次在设置 → 账号中点「获取二维码」，用米家 App 扫码登录。
3. 登录后自动同步家庭、房间、设备与场景；后续启动可一键登录。

登录凭证保存在 `%APPDATA%\Feiyu\MiHome\auth.json`，**不要提交或分享**，
详见 [SECURITY.md](SECURITY.md)。

## 架构

```
Avalonia Views（AXAML + 编译绑定）
        ↓  Commands / Bindings
ViewModels（CommunityToolkit.Mvvm）
        ↓
Services
├── MijiaAuth / MijiaCrypto   扫码登录、签名与请求加密
├── MijiaClient               设备/家庭/场景/属性读写
├── DeviceSpecService         MIoT 规格解析（miot-spec.org）
├── MijiaIconService          设备真机图
└── 缓存 / 托盘 / 桌面钩子
        ↓ HTTPS
米家云端接口 + miot-spec.org
```

设备能力以规格驱动为主：属性 `siid/piid`、取值范围与动作列表来自 MIoT 规格，
控制面板据此生成，而非为单一型号写死分支。

## 文档

- [构建与打包](docs/一键编译与打包.md)
- [透明窗口文字渲染修复经验](docs/透明窗口文字渲染修复经验.md)
- [贡献指南](CONTRIBUTING.md)
- [安全说明](SECURITY.md)

`ui/mijia-panel.html` 是早期静态设计原型，含模拟数据，不代表当前实现。

## 许可证

GNU General Public License v3（`GPL-3.0-only`）。许可证全文见 [LICENSE](LICENSE)，
移植代码来源与版权声明见 [NOTICE](NOTICE)。

依据 GPL-3.0 第 6 节：分发本项目的可执行文件（单文件 exe、安装包）时，
对应版本的完整源代码可通过仓库 https://github.com/Feiyudevs/MiHomeHub 获取，
每个发布版本均以 Git 标签（如 `v1.0.0`）固定，标签上的源码即对应二进制的构建来源，
标签与源码将长期保留。本项目不附加任何超出 GPL-3.0 的限制条款。
