// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using CommunityToolkit.Mvvm.ComponentModel;
using MiHome.Models;

namespace MiHome.ViewModels;

/// <summary>
/// 快捷键列表行。绑定对象本身留在 <see cref="Binding"/>（持久化 / 钩子匹配都用它），
/// 这里只放界面要显示、且会随设备/场景刷新而变的文本。
/// </summary>
public sealed partial class HotkeyItemVm : ObservableObject
{
    public required HotkeyBinding Binding { get; init; }

    public string Id => Binding.Id;

    /// <summary>目标名称：设备名 / 场景名 / 自动化名。</summary>
    [ObservableProperty] private string _targetText = "";

    /// <summary>副标题：动作 + 房间 / 场景类型 / 启停态，压成一行，列表才不会长成三行。</summary>
    [ObservableProperty] private string _subText = "";

    /// <summary>组合键展示文本，如「Ctrl + Alt + L」。</summary>
    [ObservableProperty] private string _comboText = "";

    /// <summary>这条绑定是否参与全局触发。</summary>
    [ObservableProperty] private bool _isEnabled;

    /// <summary>目标不在当前家庭 / 场景未加载：仍保留绑定，但执行会失败。</summary>
    [ObservableProperty] private bool _isMissing;

    /// <summary>缺失标记的文案：全部找不到「不可用」，只缺一部分「部分不可用」。</summary>
    [ObservableProperty] private string _missingText = "不可用";

    /// <summary>删除二次确认态：第一次点只切到这个状态，3 秒内再点才真删。</summary>
    [ObservableProperty] private bool _isConfirmingDelete;
}
