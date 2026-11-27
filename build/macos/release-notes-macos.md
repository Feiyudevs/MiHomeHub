MiHomeHub **macOS 版 1.0.14**（同一套 Avalonia 代码，Windows 版行为不变）

本轮修掉的问题：
- **开机自启目录不可写时，应用启动即崩**：启动时对齐 LaunchAgent 的那段代码没有兜异常，`~/Library/LaunchAgents` 不可写（受管设备 ACL、只读 home、目录被占）时会抛未处理异常，进程直接崩掉，用户连主窗口都看不到。现在这段整体兜住异常：开机自启没开上只记日志，**不影响应用启动**，并把原因如实显示在设置页「开机启动」下面
- **构建入口说明与实际流程不一致**：README 与 `build/macos/README.md` 以前直接让用户跑底层脚本，会绕过版本溯源硬断言、也不落到固定测试目录。现在统一写明唯一入口是 `bash build/macos/publish-test-build.sh`（先提交，再打包），底层脚本仅在需要自己传签名/公证参数时使用
- **包里的版本溯源戳指向父提交**：上一版打包时比提交早了 1 分钟，包内 `MiHomeHub.dll` 的版本戳停在 `1.0.12+6c4146d`，而不是发布的那次提交 `3a58ee1`。代码本身没问题（两个 dll 只差 PE 时间戳等 97 字节），但验收按「溯源戳必须等于发布提交」判的，所以这版重打了一遍
- **打包脚本加了一条硬断言**：以后跑 `bash build/macos/publish-test-build.sh` 时，会比对包内版本戳（`<版本>+<HEAD SHA>`）和当前提交；对不上直接失败、不产出半成品，也不再出现「先打包后提交」的顺序事故。脚本头部写了正确顺序：先 commit，再打包

沿用上一版（1.0.12）的桌面分区卡片对齐成果：拖动改成两端同一套逻辑（按下记起点、过 4 像素阈值、按光标位移推窗口）、背景是真磨砂（自建 `NSVisualEffectView` + `contentView.layer` 8 像素圆角裁剪）、点卡片不抢焦点、浮层压在卡片之上、双击间隔读系统设置。

沿用：标题栏红黄绿（红＝隐藏这一扇窗、黄＝最小化、绿＝铺满工作区）、拖四边跟手且边缘有可拖动光标、点 Dock 图标能唤回最小化的窗口、桌面分区卡片贴在访达桌面图标层之上（可点可拖、跨桌面空间）、桌面组件显隐走菜单栏 + 全局快捷键 `Alt+Cmd+D`（Carbon 注册，不需要辅助功能权限）、设备级全局快捷键走 Carbon `RegisterEventHotKey`（可同时注册多条、各自绑定设备、免辅助功能权限）、托盘菜单含「设置」、开机自启走 LaunchAgent（不会再多弹一个抢单实例的副本）、更新走 GitHub Release（dmg 就地替换 .app 后自动重启）、登录凭证存 macOS 钥匙串。

仓库里的更新日志也补了 macOS 版本线：`changelog/released.md` 新增 `[macOS 1.0.14]` 一节，并写明两个更新渠道的关系 —— 根目录 `update.json` 只服务 Windows 客户端（它校验 `.exe` 文件名），macOS 用按架构分开的 `update-macos-<rid>.json`。

打包：`.app` + `.dmg`，Apple Silicon / Intel 双架构，Hardened Runtime 自签（**未公证**，首次打开请右键 → 打开，或 `xattr -dr com.apple.quarantine /Applications/MiHomeHub.app`）。
