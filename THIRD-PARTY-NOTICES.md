# 第三方组件与许可证

本项目自身以 **GPL-3.0-only** 发布（见 `LICENSE`、`NOTICE`）。以下列出随项目
使用的第三方组件；各组件许可证以对应官方仓库的 LICENSE 文件为准。

## 直接 NuGet 依赖

| 组件 | 版本 | 用途 | 许可证 |
| --- | --- | --- | --- |
| Avalonia | 11.3.9 | 跨平台 UI 框架 | MIT |
| Avalonia.Desktop | 11.3.9 | 桌面后端 | MIT |
| Avalonia.Themes.Fluent | 11.3.9 | Fluent 主题 | MIT |
| Avalonia.Svg.Skia | 11.3.0 | SVG 图标渲染 | MIT |
| CommunityToolkit.Mvvm | 8.4.0 | MVVM / RelayCommand | MIT |
| QRCoder | 1.6.0 | 登录二维码生成 | MIT |

参考链接：

- Avalonia：https://github.com/AvaloniaUI/Avalonia
- Avalonia.Svg.Skia：https://github.com/wieslawsoltes/Svg.Skia
- CommunityToolkit.Mvvm：https://github.com/CommunityToolkit/dotnet
- QRCoder：https://github.com/codebude/QRCoder

## 传递依赖（随上述组件引入）

SkiaSharp、ShimSkiaSharp、Svg.Skia、HarfBuzzSharp、System.Reactive 等，
均为 MIT / BSD 类宽松许可，具体以各自仓库为准。

## 运行时与构建工具（不随源码分发）

- .NET 10 SDK / Runtime：MIT（https://github.com/dotnet/runtime）
- Inno Setup 6：仅用于生成安装包，不随本项目源码分发
- GitHub Actions / setup-dotnet：仅 CI 使用

## 协议与算法参考（非 NuGet 依赖）

米家云端登录、签名与 MIoT 规格相关实现参考了 mijia-api 等开源项目，
该部分为 **GPL-3.0**，详见 `NOTICE`。

设备规格数据在运行期从 miot-spec.org 获取，产品图标从米家产品库接口获取，
这些资源由各自服务方提供，不包含在本仓库源码中。
