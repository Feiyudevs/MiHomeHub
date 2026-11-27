# build/macos —— macOS 打包脚本（内部说明）

把仓库里**同一个** C# / .NET 10 + Avalonia 项目打成 macOS 的 `.app` 与 `.dmg`。
不改任何 `.cs` / `.axaml` / `.csproj`，Windows 的打包链路（`scripts/package-setup.ps1`）也不受影响。

## 文件

| 文件 | 作用 |
| --- | --- |
| `publish-test-build.sh` | **唯一入口**：调 `package-macos.sh` 打包 → 校验包内版本戳与 HEAD 一致 → 把 `.app` 覆盖到 `output/macos-build-next/<rid>/` → 同步 dmg / 清单 / 校验和 |
| `package-macos.sh` | 底层打包脚本：publish → 组装 `.app` → 签名 →（可选）公证 → 打 `.dmg` → 写清单与校验和。`publish-test-build.sh` 调它；需要自己传签名/公证参数时也直接用它 |
| `make-icon.sh` | `Assets/logo-256.png` → `MiHomeHub.icns`（`sips` 缩放 + `iconutil` 打包） |
| `Info.plist.in` | `Contents/Info.plist` 模板，`@@TOKEN@@` 由脚本替换 |
| `entitlements.plist` | Hardened Runtime 所需的 entitlements（.NET 的 JIT 与库校验豁免是硬性要求） |

## 用法

```bash
# 日常出包：唯一入口，先提交再打包
git commit -am "改动说明"
bash build/macos/publish-test-build.sh                 # 两个架构都打
bash build/macos/publish-test-build.sh --rid osx-arm64 # 只打 Apple Silicon
bash build/macos/publish-test-build.sh --no-package    # 只重新同步（复用已有产物）
```

打包入口会做**版本溯源硬断言**：包内 `MiHomeHub.dll` 的 `InformationalVersion`（`<版本>+<HEAD SHA>`）
必须等于当前 `HEAD`，对不上直接失败、不产出半成品。原因是包里的溯源戳只记录提交时的 HEAD，
「先打包、后提交」会发出一个溯源到旧提交的包（1.0.12 就这么翻过车）。

底层脚本（需要自己指定版本、输出目录或签名/公证参数时用）：

```bash
# Apple Silicon（默认 ad-hoc 临时签名，不公证）
bash build/macos/package-macos.sh --rid osx-arm64

# Intel
bash build/macos/package-macos.sh --rid osx-x64

# 指定版本与输出目录
bash build/macos/package-macos.sh --rid osx-arm64 --version 1.0.4 --output-dir publish/release/1.0.4

# 用自己的 Developer ID 签名 + 公证（需要付费 Apple Developer 账号）
bash build/macos/package-macos.sh \
  --rid osx-arm64 \
  --version 1.0.4 \
  --sign-identity "Developer ID Application: Your Name (TEAMID)" \
  --notarize --notary-profile mihomehub-notary
```

`--notary-profile` 对应 `xcrun notarytool store-credentials <profile>` 存的那份钥匙串配置。
公证前先在钥匙串里建好 Apple ID 应用专用密码。

其它选项：`--configuration Release|Debug`、`--channel stable|test`、`--with-local-settings`
（把本机私有覆盖一起打进包，**仅限本机联调，别用于分发**）、`--skip-publish`（复用已有 publish 产物）。

## 产物

默认落在 `publish/release/<版本>/`：

```
MiHomeHub.app                              # 可双击启动的 bundle
MiHomeHub-Setup-<版本>-<rid>.dmg           # 分发件，资产名沿用 Windows 版的命名规则
update-macos-<rid>.json                   # 更新清单，字段与仓库根 update.json 一致
SHA256SUMS-macos-<rid>.txt                # 校验清单，格式同 Windows 版 SHA256SUMS.txt
```

`.app` 内部结构：

```
Contents/MacOS/MiHomeHub                  # 可执行文件（ApplicationIcon 被清空，图标走 .icns）
Contents/MacOS/*.dll -> ../Resources/*.dll # 软链接：托管程序集与原生库都放在 Resources
Contents/MacOS/MiHomeHub.settings.json -> ../Resources/MiHomeHub.settings.json   # 软链接：AppContext.BaseDirectory 就在这儿
Contents/Resources/MiHomeHub.settings.json    # 设置文件的真实位置
Contents/Resources/MiHomeHub.icns
Contents/Resources/LICENSE / NOTICE / THIRD-PARTY-NOTICES.md
Contents/Info.plist
```

macOS **不做单文件自解包**（`PublishSingleFile` / `EnableCompressionInSingleFile` /
`IncludeNativeLibrariesForSelfExtract` 在 `osx-*` RID 下由 `MiHomeHub.csproj` 自动关闭）：
单文件会把程序集解包到临时目录，破坏代码签名与 Hardened Runtime。

## 几个容易踩的点

- **`Contents/MacOS/` 里只能放可执行代码**。`codesign` 把这个目录里的每个文件都当成嵌套可执行文件，
  `settings.json`、`.pdb` 这类非代码文件放进去会直接签不了名（报
  `code object is not signed at all / In subcomponent: ...`）。所以脚本把这些文件挪到
  `Contents/Resources/`，原位留一个软链接——应用照旧能在 `AppContext.BaseDirectory` 找到它们。
- **`-p:ApplicationIcon=` 是必须的**。`MiHomeHub.csproj` 里的 `<ApplicationIcon>Assets\logo.ico</ApplicationIcon>`
  是 Windows 专用的，macOS 发布时用空值覆盖，图标由 `Info.plist` 的 `CFBundleIconFile` 指向 `.icns`。
- **`MiHomeHub.settings.local.json` 默认不进包**。它是 `.gitignore` 的本机覆盖文件，可能含私有后台地址，
  只有显式加 `--with-local-settings` 才打进去；打包后请确认分发件里没有它。
- **bundle 被签名后再改里面的文件会破坏签名**。想改后台地址优先用环境变量
  `MIHOMEHUB_API_BASE` / `MIHOMEHUB_APP_ID`，别直接改包内的文件；非要改就得重新 `codesign`。
- **最低系统版本 14.0**（`LSMinimumSystemVersion`），改这个值要同步改本目录里的模板与 CI。
- **`.dmg` 要单独公证**。`.app` 公证 + staple 之后才生成 `.dmg`，此时 `.dmg` 是新的产物，
  还得再走一遍 `notarytool submit` + `stapler staple`，脚本已经按这个顺序处理。
- **`iconutil` 要求 iconset 文件名严格匹配**（`icon_16x16.png`、`icon_32x32@2x.png` …），
  `make-icon.sh` 里的尺寸表别乱改。
