#!/usr/bin/env bash
# MiHomeHub · macOS 测试包交付脚本（**唯一入口**）
#
# 每次要出 macOS 测试包，就跑这个脚本，不要手搓打包命令、不要另开目录。
# 它做三件事，保证每轮结果完全一致：
#   1. 打包（build/macos/package-macos.sh）→ publish/release/<版本>/
#   2. 把打包好的 .app **覆盖**到固定测试目录 output/macos-build-next/<rid>/MiHomeHub.app
#      （rsync --delete：旧文件会被清掉，不会出现「上一版残留在这个 .app 里」）
#   3. 把当前版本的 dmg / 更新清单 / SHA256SUMS 放到 output/macos-build-next/ 根下，
#      其它版本的同名文件移进 output/macos-build-next/old/（不删除，只挪走）
#
# 用法：
#   bash build/macos/publish-test-build.sh                 # 两个架构都打
#   bash build/macos/publish-test-build.sh --rid osx-arm64 # 只打 Apple Silicon
#   bash build/macos/publish-test-build.sh --no-package    # 只重新同步（复用已有产物）
#
# 发版顺序（别搞反）：
#   1. 先 commit（工作区干净）
#   2. 再跑本脚本打包
#   3. 脚本会比对包内 MiHomeHub.dll 的版本戳（<版本>+<HEAD SHA>）与当前 HEAD，
#      对不上直接失败退出 —— 免得发出一个溯源到旧提交的包。
#      上一版 1.0.12 就是「先打包后提交」，溯源戳停在父提交上，验收被判失败。
#
# 固定测试路径（用户手测就看这两个）：
#   Apple Silicon : output/macos-build-next/osx-arm64/MiHomeHub.app
#   Intel         : output/macos-build-next/osx-x64/MiHomeHub.app
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/../.." && pwd)"
cd "$ROOT_DIR"

TEST_ROOT="$ROOT_DIR/output/macos-build-next"
OLD_DIR="$TEST_ROOT/old"
RIDS=("osx-arm64" "osx-x64")
DO_PACKAGE=1

while [[ $# -gt 0 ]]; do
  case "$1" in
    --rid) RIDS=("$2"); shift 2 ;;
    --no-package) DO_PACKAGE=0; shift ;;
    -h|--help) sed -n '2,22p' "${BASH_SOURCE[0]}"; exit 0 ;;
    *) echo "未知参数：$1" >&2; exit 2 ;;
  esac
done

VERSION="$(sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' MiHomeHub.csproj | head -1)"
if [[ -z "$VERSION" ]]; then echo "读不到 MiHomeHub.csproj 里的 <Version>" >&2; exit 1; fi
RELEASE_DIR="$ROOT_DIR/publish/release/$VERSION"

# 当前提交（版本溯源用）。打包前必须已经提交：脚本会拿它和包内的版本戳对账。
HEAD_SHA="$(git rev-parse HEAD 2>/dev/null || true)"
if [[ -z "$HEAD_SHA" ]]; then
  echo "读不到 git HEAD（不是 git 仓库？）" >&2
  exit 1
fi
if [[ -n "$(git status --porcelain 2>/dev/null || true)" ]]; then
  echo "注意：工作区有未提交改动。包里的版本戳只记录 HEAD，"
  echo "      未提交的改动不会体现在版本戳里 —— 正式发版前请先提交。"
fi

echo "版本：$VERSION"
echo "提交：$HEAD_SHA"
mkdir -p "$TEST_ROOT" "$OLD_DIR"

for rid in "${RIDS[@]}"; do
  if [[ $DO_PACKAGE -eq 1 ]]; then
    echo ""
    echo "=== 打包 $rid ==="
    bash build/macos/package-macos.sh --rid "$rid" --output-dir "$RELEASE_DIR"
  fi

  SRC_APP="$RELEASE_DIR/$rid/MiHomeHub.app"
  if [[ ! -d "$SRC_APP" ]]; then
    echo "缺少产物：$SRC_APP" >&2
    exit 1
  fi

  # 版本溯源校验（硬断言）：
  # 包里的 MiHomeHub.dll 带着编译时的 InformationalVersion（<版本>+<提交 SHA>）。
  # 它必须等于当前 HEAD —— 否则说明「先打包、后提交」了，发出去的包溯源到旧提交，
  # 验收时会被判失败（上一轮 1.0.12 就是这么翻车的）。所以这里直接失败，不产出半成品。
  DLL="$SRC_APP/Contents/MacOS/MiHomeHub.dll"
  if [[ ! -f "$DLL" ]]; then
    echo "缺少程序集：$DLL" >&2
    exit 1
  fi
  FOUND="$(strings -a "$DLL" | grep -oE "[0-9]+\.[0-9]+\.[0-9]+(\.[0-9]+)?\+[0-9a-f]{40}" | head -1 || true)"
  EXPECTED="$VERSION+$HEAD_SHA"
  if [[ "$FOUND" != "$EXPECTED" ]]; then
    echo "" >&2
    echo "版本溯源不匹配，拒绝产出：" >&2
    echo "  包内实际：${FOUND:-（没找到版本戳）}" >&2
    echo "  期望值　：$EXPECTED" >&2
    echo "  当前 HEAD：$HEAD_SHA" >&2
    echo "  → 说明这个包是在「提交之前」打的。请先提交改动，再重新跑本脚本。" >&2
    exit 1
  fi
  echo "版本溯源一致：$FOUND"

  DEST_APP="$TEST_ROOT/$rid/MiHomeHub.app"
  mkdir -p "$TEST_ROOT/$rid"
  echo ""
  echo "=== 覆盖测试包 → $DEST_APP ==="
  rsync -a --delete "$SRC_APP/" "$DEST_APP/"
  xattr -dr com.apple.quarantine "$DEST_APP" 2>/dev/null || true
  codesign --verify --deep --strict "$DEST_APP"
  echo "签名校验通过：$DEST_APP"
done

# 当前版本的安装包 / 清单 / 校验和 → 测试目录根下
echo ""
echo "=== 同步当前版本的安装包与清单 ==="
for rid in "${RIDS[@]}"; do
  for pat in "MiHomeHub-Setup-$VERSION-$rid.dmg" "update-macos-$rid.json" "SHA256SUMS-macos-$rid.txt"; do
    if [[ -f "$RELEASE_DIR/$pat" ]]; then
      cp -f "$RELEASE_DIR/$pat" "$TEST_ROOT/$pat"
      echo "  $pat"
    fi
  done
done

# 旧版本的安装包挪进 old/（只挪不删，避免误删）。
# 注意：只有 dmg 的文件名里带版本号；update-macos-*.json / SHA256SUMS-*.txt 每次都会被上面覆盖成最新的，
# 所以这里只清理 dmg。
moved=0
while IFS= read -r f; do
  base="$(basename "$f")"
  case "$base" in
    MiHomeHub-Setup-*)
      if [[ "$base" != *"$VERSION"* ]]; then
        mv "$f" "$OLD_DIR/$(basename "$f").$(date +%Y%m%d%H%M%S)"
        moved=$((moved + 1))
      fi
      ;;
  esac
done < <(find "$TEST_ROOT" -maxdepth 1 -type f -name 'MiHomeHub-Setup-*.dmg' 2>/dev/null || true)
[[ $moved -gt 0 ]] && echo "  旧版本安装包已挪到 ${OLD_DIR}（$moved 个）"

echo ""
echo "===== 手测用这个 ====="
for rid in "${RIDS[@]}"; do
  echo "  $rid : output/macos-build-next/$rid/MiHomeHub.app"
done
echo "  版本：$VERSION"
