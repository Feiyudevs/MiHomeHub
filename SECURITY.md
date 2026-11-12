# 安全说明

## 本地登录凭证

扫码登录后，凭证以 **Windows DPAPI（当前用户作用域）加密**的形式保存在用户目录：

```
%APPDATA%\Feiyu\MiHome\auth.json
```

文件内容为 `dpapi:<Base64>`，只有登录该 Windows 账户的用户可解密；复制到其他账户或
其他机器无法使用。旧版本遗留的明文 `auth.json` 会在首次读取时自动改写为加密形式。
若需要换机迁移登录态，请在新机器重新扫码，不要拷贝该文件。

解密后的内容包括 `serviceToken`、`passToken`、`ssecurity`、`userId`、`cUserId`、`deviceId`。
这些字段等同于账号的完整访问权限，**请勿**：

- 将该文件或其所在目录提交到仓库、打包进附件、贴到 issue / 讨论区；
- 在截图、日志、探针输出里包含二维码、`serviceToken` 或 `passToken`；
- 把该文件同步到公开网盘或备份服务。

「退出登录」会清除 `serviceToken` / `ssecurity`，但**保留** `passToken` 用于免扫码一键登录；
如需彻底清除凭证，请在设置中执行退出后手动删除 `auth.json`，或删除整个
`%APPDATA%\Feiyu\MiHome\` 目录（会同时丢失本地偏好、设备缓存与桌面分组配置）。

`.gitignore` 已忽略 `auth.json`、`app.json`、`theme.json`、`devices.json`、`crash_*.txt`、
`*.log` 及各类探针输出。

## 调试探针

`MiHome.exe` 的部分命令行参数（设备触发器、场景、音箱探测）会把真实设备 `did` 与云端
响应报文打印到控制台或写入本地文件。这些输出可能包含家庭与设备标识，分享前请自行脱敏。

## 网络与数据

应用在运行期直连米家云端接口与 miot-spec.org 规格服务，用于登录、设备/场景读写与规格解析；
项目依赖中未引入第三方统计或遥测组件。设备真机图标等资源由对应服务在运行期提供，不随源码分发。

## 报告漏洞

请优先通过 GitHub 仓库的 **Security → Report a vulnerability** 提交私有报告，
不要直接开公开 issue 描述可利用细节。报告请附：影响版本、复现步骤、最小化的日志片段
（务必先移除 token / userId / did）。
