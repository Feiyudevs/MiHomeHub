<div align="center">

# MiHomeHub

**把米家设备管得又好看、又好用的 Windows 桌面控制中心**

深色玻璃拟态 · 深浅双主题 · 规格驱动的全屋控制 · 可视化自动化编辑

[![License](https://img.shields.io/badge/license-GPL--3.0--only-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4.svg)](https://dotnet.microsoft.com)
[![Avalonia](https://img.shields.io/badge/Avalonia-11.3-8B44D0.svg)](https://avaloniaui.net)
[![Platform](https://img.shields.io/badge/platform-Windows%20x64-0078D4.svg)](#快速开始)
[![Build](https://github.com/Feiyudevs/MiHomeHub/actions/workflows/build.yml/badge.svg)](https://github.com/Feiyudevs/MiHomeHub/actions/workflows/build.yml)

基于 **Avalonia 11 + .NET 10** 原生构建，不是套壳、不是 Web 页面包装。
扫码登录即可接管你的全部米家设备——灯、空调、窗帘、开关、传感器、音箱、场景与自动化。

</div>

<p align="center">
  <img src="docs/screenshots/home-dark.png" alt="MiHomeHub 深色主题首页" width="880">
  <br>
  <sub>深色主题 · 首页总览：房间导航、环境读数、设备概览与场景快捷入口</sub>
</p>

## 为什么是 MiHomeHub

市面上不缺米家第三方客户端，缺的是**既好看又能真的把设备管全**的那一个。
MiHomeHub 的取舍很明确：视觉不将就，控制不打折。

### 界面是设计出来的，不是拼出来的

- **深色 / 浅色双主题共享同一套玻璃拟态语言**——半透明卡片、分层圆角、克制的光影，不是简单地反色，而是两套各自成体系的配色。
- **无边框自绘窗口 + 自定义标题栏**，标题栏与内容区无缝衔接，没有系统灰边和丑陋的原生控件。
- **每台设备自动拉取真机产品图**，矢量图标兜底：灯带、筒灯、磁吸格栅灯、网关、电视，一眼可辨，而不是清一色的通用图标。
- **统一的动效与状态反馈**：场景卡悬停浮起、设备卡电源键由灰转紫、抽屉从右侧滑入，点哪里都有回应。
- 场景卡按语义着色——电竞是紫、温馨是红、清洁是青，一眼扫过去就知道该点哪个。

### 流畅靠架构，不靠祈祷

- **编译绑定**（`AvaloniaUseCompiledBindingsByDefault`）把 XAML 绑定在编译期校验，运行时零反射查找。
- **虚拟化自适应网格**：截图里客厅 51 台设备同屏铺开，滚动与重建卡片依然跟手；房间再多也不掉帧。
- **本地缓存直出首屏**：启动先用上次的设备快照铺满界面，再在后台静默刷新——界面从不等网络转圈。
- **真机图标异步加载、MIoT 规格按需解析**，重活全在后台线程，主线程只负责画。
- **单文件自包含发布**：一个 exe，无 .NET 运行时安装、无依赖目录，双击即用。

### 控制齐全，不挑型号

- **常用品类做深**：灯的亮度 / 色温 / 模式，空调的模式 / 温度 / 风速 / 摆风，窗帘的开合 / 暂停 / 百分比，开关插座、传感器、环境读数、耗材寿命。
- **冷门型号靠规格兜底**：控制面板由 MIoT 规格（`siid`/`piid`、取值范围、枚举、动作列表）自动生成——滑杆、下拉、开关、动作按钮该出什么出什么，**新设备即插即用，不为单一型号写死分支**。
- **设备组与虚拟子设备**（灯组、开关组）统一控制，组内成员状态实时透传；单灯也能反查到自己所属灯组的场景与自动化。
- **小爱音箱**：语音指令下发、音量与播放控制。

### 功能强大，一直做到自动化编辑

- **场景 / 自动化可视化编辑**：改名、启停、**拖拽调整执行顺序**、增删动作，改完直接写回米家云端，手机 App 同步生效——不是在本地做假动作。
- **设备详情抽屉**：控制项与关联的智能场景、自动化清单同屏呈现，并标注「场景 / 控制 / 仅本机」来源，不用再回手机翻。
- **全局搜索、房间分组、多家庭切换**，设备再多也能三步之内找到。
- **桌面分区卡片**：把任意房间、设备或场景贴到桌面，多显示器独立布局，卡片可拖拽排序——不打开主界面也能控制，详见[桌面组件](#桌面组件)。
- **托盘常驻 + 快捷菜单 + 开机自启**，深浅主题与外观细节可调。

## 界面

<table>
  <tr>
    <td width="50%" align="center">
      <img src="docs/screenshots/home-light.png" alt="浅色主题首页"><br>
      <sub><b>浅色主题</b> · 同一套布局，完全不同的呼吸感</sub>
    </td>
    <td width="50%" align="center">
      <img src="docs/screenshots/device-drawer.png" alt="设备控制抽屉"><br>
      <sub><b>设备抽屉</b> · 亮度色温滑杆 + 关联场景清单</sub>
    </td>
  </tr>
</table>

<p align="center">
  <img src="docs/screenshots/scene-editor.png" alt="场景编辑" width="880">
  <br>
  <sub><b>场景编辑</b> · 执行顺序可拖拽，逐条动作带设备图标，改完直写云端</sub>
</p>

## 桌面组件

主界面之外还有一层：把房间、设备或音箱**钉到桌面本身**，不打开窗口也能控制全屋。
卡片是半透明的，壁纸的颜色会从卡片底下透上来——和系统长在一起，而不是浮在桌面上的一块板子。

<p align="center">
  <img src="docs/screenshots/desktop-widgets.jpg" alt="桌面分区卡片：房间设备卡与音箱播放卡" width="880">
  <br>
  <sub><b>桌面组件</b> · 房间设备卡 + 音箱播放卡，随壁纸透光的玻璃拟态</sub>
</p>

- **房间卡**：一张卡片就是整个房间的设备清单。标题栏显示当前分区序号（如 `6/8`），
  上下箭头直接翻到相邻房间，不必回到主界面点导航。
- **状态一眼可读**：传感器读数直接印在设备行上（睡眠插座 `35°C`、书房空调 `19°C · 56%`），
  离线设备自动灰显并标注「离线」——不用点进去猜哪台掉线了。
- **音箱卡**：正在播放 / 已暂停、当前音量、上一首 / 下一首 / 播放暂停，常用操作都在手边。
- **不抢焦点**：卡片嵌在桌面层而不是置顶窗口，正常点桌面、拖图标不会被打断；
  需要操作时直接点卡片。
- **多显示器独立布局**：每块屏幕各自摆放卡片，位置与分区配置持久化，重开即还原。
- **随时调整**：标题栏支持钉住、翻页、快速添加设备、打开更多菜单。

> 实现上由 `Services/DesktopEmbed.cs` 把卡片宿主嵌入桌面 `WorkerW` 层，
> `DesktopGroupStore` 持久化分区与位置，`DesktopDoubleClickHook` 接管双击桌面唤出主窗口。

## 功能一览

| 分类 | 能力 |
| --- | --- |
| 账号 | 扫码登录、免扫码一键登录（复用本地 `passToken`）、退出登录、多家庭切换 |
| 设备 | 房间分组、全局搜索、设备详情抽屉、状态轮询、离线标记、耗材与环境读数 |
| 控制 | 灯 / 空调 / 窗帘 / 开关插座面板，MIoT 规格驱动的通用属性控件，设备组与虚拟子设备 |
| 场景 | 手动执行、可视化编辑（启停 / 排序 / 增删动作）、与米家云端双向同步 |
| 语音 | 小爱音箱语音指令、音量与播放控制 |
| 桌面 | 多显示器分区卡片、拖拽排序、托盘快捷菜单、开机自启 |
| 外观 | 深浅双主题、图标风格、布局密度等设置项，持久化到本地 |

## 技术栈

| 项目 | 内容 |
| --- | --- |
| UI | Avalonia 11.3.9（Themes.Fluent、Svg.Skia） |
| 运行时 | .NET 10，`win-x64` 自包含单文件发布 |
| MVVM | CommunityToolkit.Mvvm 8.4.0 |
| 二维码 | QRCoder 1.6.0 |
| 构建 / 打包 | `build.cmd`、`package.cmd`、Inno Setup 6 |

性能相关的几处关键设计：

- 编译绑定 + 虚拟化网格（`VirtualizingAdaptiveGridPanel` / `RecyclingItemsControl`），复用卡片而非每次重建
- 缓存优先渲染，网络请求异步补齐
- 设备图标与 MIoT 规格分离加载，失败自动降级到矢量图标
- 不开 `PublishTrimmed`：项目仍有 `ReflectionBinding` 与 `System.Text.Json` 反射序列化，裁剪会让绑定失效

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
├── docs/            # 构建与技术文档、界面截图
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

登录凭证保存在 `%APPDATA%\Feiyu\MiHome\auth.json`，以 Windows DPAPI 加密（仅当前用户可解密），
**不要提交或分享**，详见 [SECURITY.md](SECURITY.md)。

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

---

<div align="center">
<sub>本项目与小米科技无隶属关系，米家、小米为小米科技有限责任公司商标。</sub>
</div>
