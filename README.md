# HaPanel — Avalonia + HomeAssistant 桌面控制台

基于 Avalonia 11 + .NET 10，调用 HomeAssistant 官方 REST API + WebSocket，暗色圆角卡片风格。

## 项目结构

```
MiHomeWin/
├── HaPanel.csproj
├── Program.cs
├── App.axaml(.cs)
├── Models/
│   ├── AppSettings.cs      # HA 地址/Token，自动持久化到 %APPDATA%/HaPanel/settings.json
│   └── HaEntity.cs         # HA 实体（对应 /api/states 数组元素）
├── Services/
│   └── HaApiService.cs     # REST（Ping/States/CallService）+ WebSocket（state_changed 实时推送）
├── ViewModels/
│   └── MainViewModel.cs    # CommunityToolkit.Mvvm，RelayCommand 驱动
└── Views/
    └── MainWindow.axaml(.cs)
```

## 运行

```powershell
cd I:\AICode\MiHomeWin
dotnet run
```

## 使用步骤

1. 先在 HomeAssistant 里拿到长期访问令牌：
   - HA 网页 → 左下角头像 → 安全 → 长期访问令牌 → 创建令牌
2. 打开 HaPanel，填入 HA 地址（如 `http://192.168.1.10:8123`）和 Token。
3. 点 **测试连接** → 点 **连接 + 加载**。
4. 中间列表会列出所有实体；选中一个灯/开关，点右侧 **开/关 选中项** 下发指令。
5. 连接成功后 WebSocket 自动订阅 `state_changed`，右侧日志区实时打印状态变化。

## 关键链路

```
Avalonia UI
   ↓ HTTP /ws
HaApiService（REST + WebSocket，Bearer Token）
   ↓
HomeAssistant 内置 8123 API
   ↓ xiaomi_home / xiaomi_miot 集成
米家中枢网关（本地优先）/ 小米云
   ↓
米家设备
```

## 后续扩展方向

- 按 domain 分组（light / switch / climate / sensor）做侧边栏过滤
- 灯组件加亮度滑条（调用 `light.turn_on` + `brightness` 参数）
- 温度传感器卡片化显示
- WebSocket 接收后用 Dispatcher 强制切 UI 线程（当前 ObservableCollection 已自动 marshal）
- 打包单文件 exe：`dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true`
