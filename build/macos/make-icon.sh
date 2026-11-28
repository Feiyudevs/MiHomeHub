#!/usr/bin/env bash
# 从 Assets/logo-256.png 生成 macOS 的 MiHomeHub.icns（sips 缩放 + iconutil 打包）。
#
# 用法：
#   bash build/macos/make-icon.sh [--input <png>] [--output <icns>]
#
#   --input   源 PNG（默认 <仓库根>/Assets/logo-256.png）
#   --output  输出的 .icns（默认 ${TMPDIR:-/tmp}/MiHomeHub.icns，不污染仓库）
#
# 说明：源图只有 256x256，iconset 里 512/1024 两档是放大出来的（sips -z 允许，
# 画质略有损失但 macOS 接受；换更高分辨率源图即可改善）。
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/../.." && pwd)"

INPUT="$ROOT_DIR/Assets/logo-256.png"
OUTPUT="${TMPDIR:-/tmp}/MiHomeHub.icns"

while [[ $# -gt 0 ]]; do
	case "$1" in
		--input)
			INPUT="${2:?--input 需要一个 PNG 路径}"
			shift 2
			;;
		--output)
			OUTPUT="${2:?--output 需要一个 .icns 路径}"
			shift 2
			;;
		-h|--help)
			sed -n '2,13p' "${BASH_SOURCE[0]}"
			exit 0
			;;
		*)
			echo "未知参数：$1" >&2
			exit 2
			;;
	esac
done

fail() { echo "make-icon.sh: $*" >&2; exit 1; }

command -v sips >/dev/null 2>&1 || fail "缺少 sips（macOS 自带，检查是否装了完整系统）"
command -v iconutil >/dev/null 2>&1 || fail "缺少 iconutil（macOS 自带）"

[[ -f "$INPUT" ]] || fail "找不到源图：$INPUT"

src_w="$(sips -g pixelWidth "$INPUT" | awk '/pixelWidth/ {print $2}')"
src_h="$(sips -g pixelHeight "$INPUT" | awk '/pixelHeight/ {print $2}')"
[[ -n "$src_w" && -n "$src_h" ]] || fail "读不出源图尺寸：$INPUT"
echo "源图：${INPUT}（${src_w}x${src_h}）"
if [[ "$src_w" -lt 1024 ]]; then
	echo "提示：源图小于 1024，512/1024 两档由 sips 放大生成。"
fi

# iconutil 认目录名里的 .iconset 后缀，所以临时目录里再套一层同名子目录
TMP_ROOT="$(mktemp -d "${TMPDIR:-/tmp}/mihomehub-icon.XXXXXX")"
ICONSET="$TMP_ROOT/MiHomeHub.iconset"
mkdir -p "$ICONSET"
cleanup() { rm -rf "$TMP_ROOT"; }
trap cleanup EXIT

# iconset 要求的文件名与像素尺寸（sips -z 传的是 高 宽，这里都是正方形）
sizes=(
	"icon_16x16.png 16"
	"icon_16x16@2x.png 32"
	"icon_32x32.png 32"
	"icon_32x32@2x.png 64"
	"icon_128x128.png 128"
	"icon_128x128@2x.png 256"
	"icon_256x256.png 256"
	"icon_256x256@2x.png 512"
	"icon_512x512.png 512"
	"icon_512x512@2x.png 1024"
)

for entry in "${sizes[@]}"; do
	name="${entry%% *}"
	px="${entry##* }"
	sips -z "$px" "$px" "$INPUT" --out "$ICONSET/$name" >/dev/null
done

mkdir -p "$(dirname "$OUTPUT")"
rm -f "$OUTPUT"
iconutil -c icns "$ICONSET" -o "$OUTPUT"
[[ -f "$OUTPUT" ]] || fail "iconutil 没有产出 $OUTPUT"

echo "图标：$OUTPUT"
