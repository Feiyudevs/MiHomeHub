#!/usr/bin/env bash
# MiHomeHub · macOS 打包脚本：C# / .NET 10 + Avalonia 11.3.9 → .app + .dmg
#
# 用法：
#   bash build/macos/package-macos.sh [选项]
#
# 选项：
#   --rid <rid>                 osx-arm64 | osx-x64（默认 osx-arm64）
#   --version <semver>          目标版本（默认读 MiHomeHub.csproj 的 <Version>）
#   --output-dir <dir>          产物输出目录（默认 publish/release/<版本>，与 Windows 版同构）
#   --sign-identity <name>      签名身份；默认 "-"（ad-hoc 临时签名，免开发者账号）
#   --configuration <cfg>       Release（默认）| Debug
#   --channel <name>            更新清单通道（默认 stable，与 update.json 一致）
#   --notarize                  开启公证（需要 --notary-profile，且 .app 与 .dmg 分别公证+staple）
#   --notary-profile <profile>  xcrun notarytool 使用的钥匙串配置名
#   --with-local-settings       把 MiHomeHub.settings.local.json 一起打进包（可能含密钥，仅限本机联调）
#   --skip-publish              复用已有的 publish 产物，不重新 dotnet publish
#   -h|--help                   显示本说明
#
# 产物（默认落在 publish/release/<版本>/）：
#   MiHomeHub.app                                   可双击启动的 bundle
#   MiHomeHub-Setup-<版本>-<rid>.dmg                分发件（资产名沿用 Windows 版的命名规则）
#   update-macos-<rid>.json                        更新清单（字段与 update.json 一致）
#   SHA256SUMS-macos-<rid>.txt                     校验清单（格式同 Windows 版 SHA256SUMS.txt）
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/../.." && pwd)"

APP_NAME="MiHomeHub"
BUNDLE_ID="com.feiyu.mihomehub"
MIN_MACOS="14.0"
ICON_NAME="MiHomeHub.icns"

RID="osx-arm64"
VERSION=""
OUTPUT_DIR=""
SIGN_IDENTITY="-"
CONFIGURATION="Release"
CHANNEL="stable"
NOTARIZE=0
NOTARY_PROFILE=""
WITH_LOCAL_SETTINGS=0
SKIP_PUBLISH=0

usage() { sed -n '2,24p' "${BASH_SOURCE[0]}"; }

while [[ $# -gt 0 ]]; do
	case "$1" in
		--rid)            RID="${2:?--rid 需要一个值}"; shift 2 ;;
		--version)        VERSION="${2:?--version 需要一个值}"; shift 2 ;;
		--output-dir)     OUTPUT_DIR="${2:?--output-dir 需要一个路径}"; shift 2 ;;
		--sign-identity)  SIGN_IDENTITY="${2:?--sign-identity 需要一个签名身份}"; shift 2 ;;
		--configuration)  CONFIGURATION="${2:?--configuration 需要 Release 或 Debug}"; shift 2 ;;
		--channel)        CHANNEL="${2:?--channel 需要一个值}"; shift 2 ;;
		--notary-profile) NOTARY_PROFILE="${2:?--notary-profile 需要一个钥匙串配置名}"; shift 2 ;;
		--notarize)       NOTARIZE=1; shift ;;
		--with-local-settings) WITH_LOCAL_SETTINGS=1; shift ;;
		--skip-publish)   SKIP_PUBLISH=1; shift ;;
		-h|--help)        usage; exit 0 ;;
		*) echo "未知参数：$1" >&2; usage >&2; exit 2 ;;
	esac
done

fail() { echo ""; echo "打包失败：$*" >&2; exit 1; }
info() { echo "==> $*"; }

# ---------------------------------------------------------------- 工具检查
need_tool() {
	local tool="$1" hint="$2"
	command -v "$tool" >/dev/null 2>&1 || fail "缺少必需工具 ${tool} —— ${hint}"
}

need_tool dotnet "安装 .NET 10 SDK（版本见 global.json）"
need_tool codesign "装 Xcode 命令行工具：xcode-select --install"
need_tool hdiutil "macOS 自带工具，检查系统是否完整"
need_tool iconutil "macOS 自带工具，检查系统是否完整"
need_tool sips "macOS 自带工具，检查系统是否完整"
need_tool plutil "macOS 自带工具，检查系统是否完整"
if [[ "$NOTARIZE" -eq 1 ]]; then
	need_tool ditto "macOS 自带工具"
	need_tool xcrun "装 Xcode 命令行工具：xcode-select --install"
	[[ -n "$NOTARY_PROFILE" ]] || fail "--notarize 必须同时给 --notary-profile <keychain-profile>"
fi

# ---------------------------------------------------------------- 版本
if [[ -z "$VERSION" ]]; then
	VERSION="$(sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' "$ROOT_DIR/MiHomeHub.csproj" | head -n 1 | tr -d '[:space:]')"
fi
[[ -n "$VERSION" ]] || fail "读不到版本号（MiHomeHub.csproj 里没有 <Version>），请用 --version 指定"
[[ "$VERSION" =~ ^[0-9]+(\.[0-9]+){1,3}$ ]] || fail "版本号非法：${VERSION}（需要 major.minor.patch）"

case "$RID" in
	osx-arm64|osx-x64) ;;
	*) fail "不支持的 RID：${RID}（只支持 osx-arm64 / osx-x64）" ;;
esac

# AssemblyVersion 必须是 1~4 段数字
if [[ "$VERSION" =~ ^[0-9]+(\.[0-9]+){3}$ ]]; then
	ASM_VERSION="$VERSION"
else
	ASM_VERSION="$VERSION.0"
fi

OUT_DIR="${OUTPUT_DIR:-$ROOT_DIR/publish/release/$VERSION}"
PUBLISH_DIR="$ROOT_DIR/publish/macos-$RID"
# 发布用独立的中间/编译输出目录：
# 同一个工作树里如果先跑过 `dotnet build -r <rid>`（框架依赖产物），再跑自包含 publish，
# MSBuild 的增量缓存会让 deps.json / runtimeconfig.json 不被重新生成，
# publish 阶段直接报 MSB3030「无法复制文件…因为找不到该文件」。
# 用独立目录彻底避开这份共享状态，也不动用户自己的 build 产物。
PUBLISH_OBJ_DIR="$ROOT_DIR/publish/obj-macos-$RID/"
PUBLISH_BIN_DIR="$ROOT_DIR/publish/bin-macos-$RID/"
STAGE_DIR="$ROOT_DIR/publish/macos-stage/$RID"
APP_DIR="$STAGE_DIR/$APP_NAME.app"
DMG_NAME="$APP_NAME-Setup-$VERSION-$RID.dmg"
DMG_PATH="$OUT_DIR/$DMG_NAME"
MANIFEST_NAME="update-macos-$RID.json"
SUMS_NAME="SHA256SUMS-macos-$RID.txt"

echo "MiHomeHub macOS 打包"
echo "  RID            $RID"
echo "  版本           $VERSION"
echo "  配置           $CONFIGURATION"
echo "  签名身份       $SIGN_IDENTITY"
echo "  公证           $([[ "$NOTARIZE" -eq 1 ]] && echo "开启（profile=${NOTARY_PROFILE}）" || echo "关闭")"
echo "  本地设置覆盖   $([[ "$WITH_LOCAL_SETTINGS" -eq 1 ]] && echo "包含" || echo "排除")"
echo "  输出目录       $OUT_DIR"

# ---------------------------------------------------------------- 发布
if [[ "$SKIP_PUBLISH" -eq 1 ]]; then
	info "跳过 dotnet publish，复用 $PUBLISH_DIR"
	[[ -d "$PUBLISH_DIR" ]] || fail "--skip-publish 但找不到 publish 产物：$PUBLISH_DIR"
else
	info "dotnet publish（-r $RID -c ${CONFIGURATION}）"
	rm -rf "$PUBLISH_DIR" "$PUBLISH_OBJ_DIR" "$PUBLISH_BIN_DIR"
	mkdir -p "$PUBLISH_DIR"
	dotnet publish "$ROOT_DIR/MiHomeHub.csproj" \
		--configuration "$CONFIGURATION" \
		--runtime "$RID" \
		--self-contained true \
		--output "$PUBLISH_DIR" \
		--nologo \
		-p:BaseIntermediateOutputPath="$PUBLISH_OBJ_DIR" \
		-p:BaseOutputPath="$PUBLISH_BIN_DIR" \
		-p:ApplicationIcon= \
		-p:Version="$VERSION" \
		-p:AssemblyVersion="$ASM_VERSION" \
		-p:FileVersion="$ASM_VERSION" \
		-p:InformationalVersion="$VERSION"
fi

[[ -x "$PUBLISH_DIR/$APP_NAME" ]] || fail "发布产物里没有可执行文件：$PUBLISH_DIR/$APP_NAME"

# ---------------------------------------------------------------- 组装 .app
info "组装 $APP_NAME.app"
rm -rf "$STAGE_DIR"
mkdir -p "$APP_DIR/Contents/MacOS" "$APP_DIR/Contents/Resources"

cp -R "$PUBLISH_DIR/." "$APP_DIR/Contents/MacOS/"

# Contents/MacOS/ 里只允许出现可执行代码：codesign 把这个目录里的每个文件都当成
# 嵌套可执行文件，settings.json / .pdb 这类非代码文件放进去会直接签不了名
# （报 "code object is not signed at all / In subcomponent: ..."）。
# 所以把它们挪到 Contents/Resources/，原位留一个符号链接：
# 程序照旧能在 AppContext.BaseDirectory（= Contents/MacOS）里找到这些文件。
while IFS= read -r -d '' extra; do
	name="$(basename "$extra")"
	[[ "$name" == "$APP_NAME" ]] && continue
	rm -f "$APP_DIR/Contents/Resources/$name"
	mv "$extra" "$APP_DIR/Contents/Resources/$name"
	ln -s "../Resources/$name" "$APP_DIR/Contents/MacOS/$name"
done < <(find "$APP_DIR/Contents/MacOS" -maxdepth 1 -type f -print0)

# 权威的设置文件放在 Resources/（用仓库根的那份），MacOS/ 里是它的软链接
cp -f "$ROOT_DIR/$APP_NAME.settings.json" "$APP_DIR/Contents/Resources/$APP_NAME.settings.json"
ln -sfn "../Resources/$APP_NAME.settings.json" "$APP_DIR/Contents/MacOS/$APP_NAME.settings.json"

# 本地覆盖设置默认不进包：它是 gitignore 的私有后台地址，可能含密钥
rm -f "$APP_DIR/Contents/Resources/$APP_NAME.settings.local.json" \
      "$APP_DIR/Contents/MacOS/$APP_NAME.settings.local.json"
if [[ "$WITH_LOCAL_SETTINGS" -eq 1 ]]; then
	if [[ -f "$ROOT_DIR/$APP_NAME.settings.local.json" ]]; then
		cp -f "$ROOT_DIR/$APP_NAME.settings.local.json" "$APP_DIR/Contents/Resources/$APP_NAME.settings.local.json"
		ln -sfn "../Resources/$APP_NAME.settings.local.json" "$APP_DIR/Contents/MacOS/$APP_NAME.settings.local.json"
	else
		echo "提示：--with-local-settings 给了，但仓库里没有 $APP_NAME.settings.local.json，跳过。"
	fi
fi

# GPL-3.0-only：源码与版权声明随包分发
for doc in LICENSE NOTICE THIRD-PARTY-NOTICES.md; do
	[[ -f "$ROOT_DIR/$doc" ]] || fail "缺少随包文件：$doc"
	cp "$ROOT_DIR/$doc" "$APP_DIR/Contents/Resources/$doc"
done

# 图标
info "生成图标"
bash "$SCRIPT_DIR/make-icon.sh" --input "$ROOT_DIR/Assets/logo-256.png" --output "$APP_DIR/Contents/Resources/$ICON_NAME"

# ---------------------------------------------------------------- Info.plist
info "渲染 Info.plist"
sed -e "s|@@CFBundleDisplayName@@|$APP_NAME|g" \
	-e "s|@@CFBundleExecutable@@|$APP_NAME|g" \
	-e "s|@@CFBundleIconFile@@|$ICON_NAME|g" \
	-e "s|@@CFBundleIdentifier@@|$BUNDLE_ID|g" \
	-e "s|@@CFBundleName@@|$APP_NAME|g" \
	-e "s|@@CFBundleShortVersionString@@|$VERSION|g" \
	-e "s|@@CFBundleVersion@@|$VERSION|g" \
	-e "s|@@LSMinimumSystemVersion@@|$MIN_MACOS|g" \
	-e "s|@@NSHumanReadableCopyright@@|Copyright (C) 2026 Feiyu. GPL-3.0-only.|g" \
	"$SCRIPT_DIR/Info.plist.in" > "$APP_DIR/Contents/Info.plist"
plutil -lint "$APP_DIR/Contents/Info.plist" >/dev/null || fail "生成的 Info.plist 不合法"

# ---------------------------------------------------------------- 签名
info "签名（Hardened Runtime + entitlements）"
SIGN_ARGS=(--force --sign "$SIGN_IDENTITY" --options runtime --entitlements "$SCRIPT_DIR/entitlements.plist")
if [[ "$SIGN_IDENTITY" == "-" ]]; then
	# ad-hoc 签名没有签名者身份，不能带时间戳
	SIGN_ARGS+=(--timestamp=none)
else
	SIGN_ARGS+=(--timestamp)
fi
codesign "${SIGN_ARGS[@]}" "$APP_DIR/Contents/MacOS/$APP_NAME"
codesign "${SIGN_ARGS[@]}" "$APP_DIR"
codesign --verify --deep --strict --verbose=1 "$APP_DIR"

# ---------------------------------------------------------------- 公证（.app）
if [[ "$NOTARIZE" -eq 1 ]]; then
	info "公证 .app"
	APP_ZIP="$STAGE_DIR/$APP_NAME-$VERSION-$RID-notarize.zip"
	rm -f "$APP_ZIP"
	ditto -c -k --sequesterRsrc --keepParent "$APP_DIR" "$APP_ZIP"
	xcrun notarytool submit "$APP_ZIP" --keychain-profile "$NOTARY_PROFILE" --wait
	xcrun stapler staple "$APP_DIR"
fi

# ---------------------------------------------------------------- DMG
info "生成 DMG"
mkdir -p "$OUT_DIR"
DMG_STAGE="$STAGE_DIR/dmg"
rm -rf "$DMG_STAGE"
mkdir -p "$DMG_STAGE"
cp -R "$APP_DIR" "$DMG_STAGE/"
rm -f "$DMG_PATH"
hdiutil create -volname "$APP_NAME" -srcfolder "$DMG_STAGE" -ov -format UDZO "$DMG_PATH"
[[ -f "$DMG_PATH" ]] || fail "DMG 没有产出：$DMG_PATH"

# 公证必须分两步：.app 先公证并 staple，DMG 生成后单独公证并 staple
if [[ "$NOTARIZE" -eq 1 ]]; then
	info "公证 DMG"
	# Apple 要求磁盘映像本身也要有签名，否则 notarytool 会拒收
	if [[ "$SIGN_IDENTITY" == "-" ]]; then
		codesign --force --sign - --timestamp=none "$DMG_PATH"
	else
		codesign --force --sign "$SIGN_IDENTITY" --timestamp "$DMG_PATH"
	fi
	DMG_ZIP="$STAGE_DIR/$DMG_NAME-notarize.zip"
	rm -f "$DMG_ZIP"
	ditto -c -k --sequesterRsrc --keepParent "$DMG_PATH" "$DMG_ZIP"
	xcrun notarytool submit "$DMG_ZIP" --keychain-profile "$NOTARY_PROFILE" --wait
	xcrun stapler staple "$DMG_PATH"
fi

# ---------------------------------------------------------------- 更新清单 + 校验清单
info "写更新清单与校验清单"
DMG_SHA="$(shasum -a 256 "$DMG_PATH" | awk '{print $1}')"

NOTES=""
# macOS 的更新说明必须来自仓库里 macOS 专属的说明文件：
# 根目录 update.json 是 Windows 版的清单（里面是 .exe 与 Windows 的更新说明），
# 拿它的 notes 去填 macOS 清单，用户在 macOS 上会看到 Windows 的变更说明，
# 而且这段文字只存在于仓库之外 → 下次从分支重建就对不上了。
MAC_NOTES_FILE="$ROOT_DIR/build/macos/release-notes-macos.md"
if [[ -f "$MAC_NOTES_FILE" ]]; then
	NOTES="$(cat "$MAC_NOTES_FILE")"
else
	echo "提示：找不到 $MAC_NOTES_FILE，macOS 更新清单的 notes 将留空。"
fi

# 兼容旧路径：说明文件缺失时退回根清单的 notes（Windows 版行为不变）
ROOT_MANIFEST="$ROOT_DIR/update.json"
if [[ -z "$NOTES" && -f "$ROOT_MANIFEST" ]] && command -v python3 >/dev/null 2>&1; then
	NOTES="$(python3 - "$ROOT_MANIFEST" <<'PY' 2>/dev/null || true
import json, sys
try:
    with open(sys.argv[1], encoding="utf-8-sig") as fh:
        data = json.load(fh)
except Exception:
    sys.exit(0)
sys.stdout.write(str(data.get("notes", "")))
PY
)"
fi

MANIFEST_PATH="$OUT_DIR/$MANIFEST_NAME"
if command -v python3 >/dev/null 2>&1; then
	python3 - "$MANIFEST_PATH" "$VERSION" "$DMG_NAME" "$DMG_SHA" "$NOTES" "$CHANNEL" <<'PY'
import json, sys
out, version, setup, sha, notes, channel = sys.argv[1:7]
manifest = {
    "version": version,
    "setup": setup,
    "sha256": sha,
    "notes": notes,
    "mandatory": False,
    "channel": channel,
    "mirrors": [],
}
with open(out, "w", encoding="utf-8") as fh:
    json.dump(manifest, fh, ensure_ascii=False, indent=4)
    fh.write("\n")
PY
else
	echo "提示：没有 python3，更新清单的 notes 留空。"
	cat > "$MANIFEST_PATH" <<EOF
{
    "version": "$VERSION",
    "setup": "$DMG_NAME",
    "sha256": "$DMG_SHA",
    "notes": "",
    "mandatory": false,
    "channel": "$CHANNEL",
    "mirrors": []
}
EOF
fi

SUMS_PATH="$OUT_DIR/$SUMS_NAME"
printf '%s  %s\n' "$DMG_SHA" "$DMG_NAME" > "$SUMS_PATH"

# .app 也复制到输出目录（CI 里作为 Actions artifact 上传；Release 资产只挂 .dmg）
# 按 RID 分目录：arm64 和 x64 的 .app 同名，放在同一个输出目录里会互相覆盖
# （本机是 Apple Silicon 时，最后跑的那个 RID 决定用户拿到的架构）。
APP_OUT_DIR="$OUT_DIR/$RID"
mkdir -p "$APP_OUT_DIR"
rm -rf "$APP_OUT_DIR/$APP_NAME.app"
cp -R "$APP_DIR" "$APP_OUT_DIR/$APP_NAME.app"

# ---------------------------------------------------------------- 汇总
echo ""
echo "产物："
echo "  $APP_OUT_DIR/$APP_NAME.app"
echo "  $DMG_PATH"
echo "  $MANIFEST_PATH"
echo "  $SUMS_PATH"
echo ""
echo "SHA256："
printf '%s  %s\n' "$DMG_SHA" "$(basename "$DMG_PATH")"
echo ""
codesign -dv --verbose=2 "$APP_DIR" 2>&1 | sed -n '1,12p' | sed 's/^/  /'
echo ""
echo "提示：默认未公证。首次打开需 右键 → 打开，或执行 xattr -dr com.apple.quarantine <app>"
