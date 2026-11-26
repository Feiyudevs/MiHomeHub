// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiHomeHub.Models;
using MiHomeHub.Services;

namespace MiHomeHub.ViewModels;

/// <summary>
/// 全局快捷键：录制组合键 → 绑定一串控制卡片，由 <see cref="GlobalHotkeyService"/>
/// 的键盘钩子全局触发。绑定存在 hotkeys.json，钩子在窗口收起进托盘后依然工作。
/// 一条快捷键可以放多张控制卡片（关灯 / 开灯 / 执行场景 / 关自动化），按下后按列表顺序逐条执行。
/// </summary>
public partial class MainViewModel : ObservableObject
{
    /// <summary>录制按钮的默认文案。</summary>
    private const string RecordHintText = "点击录制，再按下组合键（可 F1 + 任意键）";
    private const string EmptyKeyText = "未设置";

    private readonly List<HotkeyBinding> _hotkeyBindings = new();
    private HotkeyCombo _hotkeyDraftCombo = default;

    /// <summary>编辑的是哪条绑定：null = 新建；非空 = 更新这条。</summary>
    private string? _editingHotkeyId;

    /// <summary>编辑弹窗自己挂上：保存成功 / 取消时由 VM 通知关窗。</summary>
    public Action? CloseHotkeyEditorRequested { get; set; }

    /// <summary>启动装载期：把持久化值写回属性时不要触发保存 / 写日志。</summary>
    private bool _hotkeyLoading;

    /// <summary>已绑定的快捷键行（顺序即绑定先后）。</summary>
    public ObservableCollection<HotkeyItemVm> HotkeyItems { get; } = new();

    /// <summary>编辑卡里的控制卡片：顺序即执行顺序。</summary>
    public ObservableCollection<HotkeyDraftStepVm> HotkeyDraftSteps { get; } = new();

    /// <summary>全局快捷键总开关。</summary>
    [ObservableProperty] private bool _hotkeysEnabled = true;

    /// <summary>键盘钩子是否装上：装不上时整页给一条警示。</summary>
    [ObservableProperty] private bool _hotkeyHookAvailable = true;

    /// <summary>新增快捷键编辑卡是否展开。</summary>
    [ObservableProperty] private bool _isHotkeyEditorOpen;

    /// <summary>编辑弹窗标题：新建 / 编辑。</summary>
    [ObservableProperty] private string _hotkeyEditorTitle = "新建快捷键";

    /// <summary>弹窗头部标题输入框：保存后成为列表卡片名称；默认取第一个加入的目标名。</summary>
    [ObservableProperty] private string _hotkeyDraftTitle = "";

    [ObservableProperty] private string _hotkeyDraftKeyText = EmptyKeyText;
    [ObservableProperty] private bool _isRecordingHotkey;

    /// <summary>编辑卡里的提示行（错误 / 成功都用同一行，靠颜色区分）。</summary>
    [ObservableProperty] private string _hotkeyHint = "";
    [ObservableProperty] private bool _hotkeyHintIsError;
    [ObservableProperty] private bool _hasHotkeyHint;

    /// <summary>最近一次触发的反馈。</summary>
    [ObservableProperty] private string _hotkeyLastText = "";
    [ObservableProperty] private bool _hasHotkeyLast;

    [ObservableProperty] private bool _hasHotkeys;

    /// <summary>列表标题 / 状态卡里的计数。</summary>
    public string HotkeyCountText => $"{HotkeyItems.Count} 个组合键";

    /// <summary>右栏「运行状态」里的钩子状态。</summary>
    public string HotkeyHookStateText => HotkeyHookAvailable ? "已就绪" : "未安装";

    partial void OnHotkeyHookAvailableChanged(bool value) => OnPropertyChanged(nameof(HotkeyHookStateText));

    // ===== 编辑卡里的控制卡片 =====

    public bool HasHotkeyDraftSteps => HotkeyDraftSteps.Count > 0;

    public string HotkeyDraftStepCountText => $"{HotkeyDraftSteps.Count} 张卡片";

    /// <summary>卡片列表下方的一行说明。</summary>
    public string HotkeyDraftStepMeta => HotkeyDraftSteps.Count == 0
        ? ""
        : $"共 {HotkeyDraftSteps.Count} 步 · 从上到下依次执行";

    /// <summary>设备与场景都没有候选时给提示。</summary>
    public bool HasNoHotkeyTarget =>
        !_allDevices.Any(d => d.IsControllable)
        && SceneItems.Count == 0;

    /// <summary>启动时装载绑定、挂事件并安装钩子。由构造函数末尾调用。</summary>
    private void InitHotkeys()
    {
        var data = HotkeyStore.Load();
        _hotkeyBindings.AddRange(data.Items);
        // 构造期还没有绑定，直接经由属性写回（装载期内不落盘、不写日志）
        _hotkeyLoading = true;
        HotkeysEnabled = data.Enabled;
        _hotkeyLoading = false;

        GlobalHotkeyService.Triggered += OnHotkeyTriggered;
        GlobalHotkeyService.Captured += OnHotkeyCaptured;
        GlobalHotkeyService.CaptureProgress += OnHotkeyCaptureProgress;
        GlobalHotkeyService.CaptureCancelled += OnHotkeyCaptureCancelled;
        GlobalHotkeyService.Initialize(data.Enabled, _hotkeyBindings);

        // 键盘钩子推迟到界面空闲才装（否则启动那几秒整机按键都要先等本进程 UI 线程）。
        // 这里不改 HotkeyHookAvailable：装完由 SyncHotkeyHookState 按真实结果校准。
        DesktopLog.Write($"hotkeys loaded → {_hotkeyBindings.Count} binding(s), enabled={data.Enabled}");
        RebuildHotkeyItems();
    }

    /// <summary>钩子安装完（或装失败）后同步界面状态；由 App 在空闲时安装完调用。</summary>
    public void SyncHotkeyHookState()
    {
        HotkeyHookAvailable = GlobalHotkeyService.Available;
        DesktopLog.Write($"hotkey hook ready={HotkeyHookAvailable}");
    }

    // ===== 页内交互 =====

    [RelayCommand]
    private void ToggleHotkeys() => HotkeysEnabled = !HotkeysEnabled;

    /// <summary>「智能」页头的快捷键入口：跳到快捷键控制区。</summary>
    [RelayCommand]
    private void OpenHotkeysPage() => SelectedNavIndex = 3;

    partial void OnHotkeysEnabledChanged(bool value)
    {
        if (_hotkeyLoading) return;
        GlobalHotkeyService.SetEnabled(value);
        PersistHotkeys();
        Log(value ? "全局快捷键已启用" : "全局快捷键已停用");
    }

    [RelayCommand]
    private void OpenHotkeyEditor() => BeginNewHotkey();

    /// <summary>打开「新建快捷键」弹窗前的状态准备（由视图在开窗前调用）。</summary>
    public void BeginNewHotkey()
    {
        _editingHotkeyId = null;
        HotkeyEditorTitle = "新建快捷键";
        HotkeyDraftTitle = "";
        IsHotkeyEditorOpen = true;
        _hotkeyDraftCombo = default;
        HotkeyDraftKeyText = EmptyKeyText;
        HotkeyHint = "";
        HasHotkeyHint = false;
        IsRecordingHotkey = false;
        GlobalHotkeyService.EndCapture();
        ClearDraftSteps();
        // 场景 / 自动化清单可能还没拉过：先补一次，选择弹窗里才不会只剩设备
        if (!_scenesLoadedOnce && _client != null) _ = LoadScenesAsync();
    }

    /// <summary>打开「编辑快捷键」弹窗：把已有绑定的组合键与卡片灌进草稿。</summary>
    public void BeginEditHotkey(HotkeyItemVm item)
    {
        _editingHotkeyId = item.Binding.Id;
        HotkeyEditorTitle = "编辑快捷键";
        HotkeyDraftTitle = item.Binding.Title;
        IsHotkeyEditorOpen = true;
        HotkeyHint = "";
        HasHotkeyHint = false;
        IsRecordingHotkey = false;
        GlobalHotkeyService.EndCapture();

        _hotkeyDraftCombo = new HotkeyCombo((HotkeyModifier)item.Binding.Modifiers, item.Binding.VirtualKey);
        HotkeyDraftKeyText = _hotkeyDraftCombo.IsEmpty ? EmptyKeyText : HotkeyKeyMap.Describe(_hotkeyDraftCombo);

        ClearDraftSteps();
        foreach (var step in item.Binding.Steps)
        {
            var row = HotkeyDraftStepVm.Create(step.Type, step.Id, step.Name, step.Kind);
            AttachDraftStep(row);
            HotkeyDraftSteps.Add(row);
        }

        ReindexDraftSteps();
        RefreshDraftStepDetails();
        if (!_scenesLoadedOnce && _client != null) _ = LoadScenesAsync();
    }

    /// <summary>保存 / 取消后把编辑态清干净（弹窗由 <see cref="CloseHotkeyEditorRequested"/> 关掉）。</summary>
    private void ResetHotkeyEditor()
    {
        _editingHotkeyId = null;
        IsHotkeyEditorOpen = false;
        IsRecordingHotkey = false;
        HotkeyDraftTitle = "";
        GlobalHotkeyService.EndCapture();
        _hotkeyDraftCombo = default;
        HotkeyDraftKeyText = EmptyKeyText;
        HotkeyHint = "";
        HasHotkeyHint = false;
        ClearDraftSteps();
    }

    [RelayCommand]
    private void CancelHotkeyEditor()
    {
        ResetHotkeyEditor();
        CloseHotkeyEditorRequested?.Invoke();
    }

    [RelayCommand]
    private void ClearHotkeyDraftSteps() => ClearDraftSteps();

    private void ClearDraftSteps()
    {
        HotkeyDraftSteps.Clear();
        ReindexDraftSteps();
    }

    /// <summary>给弹窗备候选：可控设备（按房间序）+ 手动场景，并带上已选的 id 顺序。</summary>
    public HotkeyTargetPickerRequest BuildHotkeyTargetPickerRequest() => new()
    {
        Devices = OrderDevices(_allDevices.Where(d => d.IsControllable)).ToList(),
        SceneCandidates = SceneItems.ToList(),
        SelectedIds = HotkeyDraftSteps.Select(s => s.Id).ToList(),
    };

    /// <summary>
    /// 弹窗确认后把选中的目标并回卡片列表：已在列表里的保留原来选的动作与对象，
    /// 新勾进来的按默认动作追加，取消勾选的移除；顺序按弹窗里的勾选顺序。
    /// 弹窗不再提供自动化，草稿里已有的自动化卡片原样保留（按原位置插回）。
    /// </summary>
    public void ApplyHotkeyTargetPickerResult(IReadOnlyList<HotkeyStep> picked)
    {
        var existing = new Dictionary<string, HotkeyDraftStepVm>();
        foreach (var s in HotkeyDraftSteps) existing[StepKey(s.Type, s.Id)] = s;

        var pickedKeys = new HashSet<string>();
        foreach (var p in picked) pickedKeys.Add(StepKey(p.Type, p.Id));

        // 自动化不在候选里，这一轮收不到它的勾选项；直接丢掉用户已有的自动化卡片是不行的
        var carried = HotkeyDraftSteps
            .Where(s => s.Type == HotkeyTargetType.Automation
                        && !pickedKeys.Contains(StepKey(s.Type, s.Id)))
            .ToList();

        var ordered = new List<HotkeyDraftStepVm>();
        foreach (var p in picked)
        {
            var key = StepKey(p.Type, p.Id);
            if (existing.TryGetValue(key, out var keep))
            {
                keep.Name = p.Name;
                ordered.Add(keep);
            }
            else
            {
                var created = HotkeyDraftStepVm.Create(p.Type, p.Id, p.Name, p.Kind);
                AttachDraftStep(created);
                ordered.Add(created);
            }
        }

        // 按原草稿位置插回，尽量不打乱用户排好的顺序
        foreach (var c in carried)
            ordered.Insert(Math.Min(HotkeyDraftSteps.IndexOf(c), ordered.Count), c);

        HotkeyDraftSteps.Clear();
        foreach (var s in ordered) HotkeyDraftSteps.Add(s);

        ReindexDraftSteps();
        // 还没手填标题时，默认拿第一个加入的目标当标题（通常就是第一盏灯）
        if (string.IsNullOrWhiteSpace(HotkeyDraftTitle) && ordered.Count > 0)
            HotkeyDraftTitle = ordered[0].Name;
        RefreshDraftStepDetails();
        HotkeyHint = "";
        HasHotkeyHint = false;
    }

    private static string StepKey(HotkeyTargetType type, string id) => $"{type}:{id}";

    private void AttachDraftStep(HotkeyDraftStepVm step)
    {
        step.RemoveRequested = RemoveDraftStep;
        step.MoveUpRequested = MoveDraftStepUp;
        step.MoveDownRequested = MoveDraftStepDown;
    }

    private void RemoveDraftStep(HotkeyDraftStepVm step)
    {
        HotkeyDraftSteps.Remove(step);
        ReindexDraftSteps();
    }

    private void MoveDraftStepUp(HotkeyDraftStepVm step)
    {
        var index = HotkeyDraftSteps.IndexOf(step);
        if (index <= 0) return;
        HotkeyDraftSteps.Move(index, index - 1);
        ReindexDraftSteps();
    }

    private void MoveDraftStepDown(HotkeyDraftStepVm step)
    {
        var index = HotkeyDraftSteps.IndexOf(step);
        if (index < 0 || index >= HotkeyDraftSteps.Count - 1) return;
        HotkeyDraftSteps.Move(index, index + 1);
        ReindexDraftSteps();
    }

    /// <summary>拖拽排序落点：把第 from 张卡插到第 to 位（视图按落点算好目标下标后调用）。</summary>
    public void MoveHotkeyDraftStep(int from, int to)
    {
        if (from < 0 || from >= HotkeyDraftSteps.Count) return;
        if (to < 0) to = 0;
        if (to >= HotkeyDraftSteps.Count) to = HotkeyDraftSteps.Count - 1;
        if (from == to) return;
        HotkeyDraftSteps.Move(from, to);
        ReindexDraftSteps();
    }

    private void ReindexDraftSteps()
    {
        for (var i = 0; i < HotkeyDraftSteps.Count; i++)
        {
            HotkeyDraftSteps[i].CanMoveUp = i > 0;
            HotkeyDraftSteps[i].CanMoveDown = i < HotkeyDraftSteps.Count - 1;
        }

        OnPropertyChanged(nameof(HasHotkeyDraftSteps));
        OnPropertyChanged(nameof(HotkeyDraftStepCountText));
        OnPropertyChanged(nameof(HotkeyDraftStepMeta));
    }

    /// <summary>卡片副标题跟着设备开关态 / 自动化启停态刷新。</summary>
    private void RefreshDraftStepDetails()
    {
        foreach (var step in HotkeyDraftSteps)
        {
            switch (step.Type)
            {
                case HotkeyTargetType.Device:
                {
                    var device = FindHotkeyDevice(step.Id);
                    step.DeviceIcon = device?.Icon;
                    step.Detail = device == null
                        ? "当前家庭里找不到这台设备"
                        : !device.IsOnline
                            ? $"{device.RoomName} · 离线"
                            : $"{device.RoomName} · 现在{device.IsOn switch { true => "开着", false => "关着" }}";
                    break;
                }
                case HotkeyTargetType.Scene:
                    step.Detail = "手动场景";
                    break;
                default:
                {
                    var scene = _scenes.FirstOrDefault(s => s.SceneId == step.Id);
                    step.Detail = scene == null
                        ? "自动化未加载"
                        : $"自动化 · 现在{(scene.Enable ? "开启" : "关闭")}";
                    break;
                }
            }
        }
    }

    [RelayCommand]
    private void BeginRecordHotkey()
    {
        if (!HotkeyHookAvailable)
        {
            ShowHint("键盘钩子未安装，录制不了全局快捷键", true);
            return;
        }

        IsRecordingHotkey = true;
        HotkeyDraftKeyText = "请按下组合键…（Esc 取消）";
        HotkeyHint = "";
        HasHotkeyHint = false;
        // 录制期间整段键盘事件都不会传到前台程序
        GlobalHotkeyService.BeginCapture();
    }

    [RelayCommand]
    private void CancelRecordHotkey()
    {
        GlobalHotkeyService.EndCapture();
        IsRecordingHotkey = false;
        HotkeyDraftKeyText = _hotkeyDraftCombo.IsEmpty
            ? EmptyKeyText
            : HotkeyKeyMap.Describe(_hotkeyDraftCombo);
    }

    /// <summary>
    /// 窗口内兜底录制：全局钩子被安全软件挡掉时，前台窗口仍能收到按键，
    /// 由 HotkeysView 的预览 KeyDown 转成组合键后走这里（与钩子回调同一套校验）。
    /// </summary>
    public void ApplyCapturedHotkey(HotkeyCombo combo)
    {
        if (!IsRecordingHotkey) return;
        OnHotkeyCaptured(combo);
    }

    private void OnHotkeyCaptureProgress(HotkeyModifier modifiers) => PreviewCaptureModifiers(modifiers);

    /// <summary>录制进行中刷新「已按下的键」文案（钩子事件与窗口兜底共用）。</summary>
    public void PreviewCaptureModifiers(HotkeyModifier modifiers)
    {
        if (!IsRecordingHotkey) return;
        HotkeyDraftKeyText = HotkeyKeyMap.DescribePending(modifiers);
    }

    private void OnHotkeyCaptured(HotkeyCombo combo)
    {
        IsRecordingHotkey = false;
        if (combo.IsEmpty) return;

        var text = HotkeyKeyMap.Describe(combo);

        // 单键只放行 F1–F24：字母/数字单键会吞掉正常输入
        if (combo.Modifiers == HotkeyModifier.None && !HotkeyKeyMap.IsFunctionKey(combo.VirtualKey))
        {
            _hotkeyDraftCombo = default;
            HotkeyDraftKeyText = EmptyKeyText;
            ShowHint($"{text} 是单键，会拦截正常输入：请加 Ctrl / Alt / Shift / Win，或改用 F1–F24", true);
            return;
        }

        if (FindConflict(combo, _editingHotkeyId) is { } conflict)
        {
            _hotkeyDraftCombo = default;
            HotkeyDraftKeyText = EmptyKeyText;
            ShowHint($"{text} 已被「{StepsLabel(conflict.Steps)}」占用，换一个组合键", true);
            return;
        }

        _hotkeyDraftCombo = combo;
        HotkeyDraftKeyText = text;
        ShowHint($"已录制 {text}，点「保存」完成绑定", false);
    }

    private void OnHotkeyCaptureCancelled()
    {
        IsRecordingHotkey = false;
        HotkeyDraftKeyText = _hotkeyDraftCombo.IsEmpty
            ? EmptyKeyText
            : HotkeyKeyMap.Describe(_hotkeyDraftCombo);
    }

    [RelayCommand]
    private void SaveHotkey()
    {
        if (_hotkeyDraftCombo.IsEmpty)
        {
            ShowHint("先录制一个组合键", true);
            return;
        }

        if (HotkeyDraftSteps.Count == 0)
        {
            ShowHint("先点「选择目标」加几张控制卡片", true);
            return;
        }

        if (FindConflict(_hotkeyDraftCombo, _editingHotkeyId) is { } conflict)
        {
            ShowHint($"该组合键已被「{StepsLabel(conflict.Steps)}」占用", true);
            return;
        }

        var steps = HotkeyDraftSteps
            .Select(s => new HotkeyStep { Type = s.Type, Kind = s.Kind, Id = s.Id, Name = s.Name })
            .ToList();

        var comboText = HotkeyKeyMap.Describe(_hotkeyDraftCombo);
        var label = StepsLabel(steps);
        var title = string.IsNullOrWhiteSpace(HotkeyDraftTitle)
            ? (steps.Count > 0 ? steps[0].Name : "快捷键")
            : HotkeyDraftTitle.Trim();

        if (_editingHotkeyId is { } editId
            && _hotkeyBindings.FirstOrDefault(b => b.Id == editId) is { } existing)
        {
            // 编辑已绑定：只换组合键与卡片，启用 / 停用状态保持不变
            existing.Steps = steps;
            existing.Title = title;
            existing.Modifiers = (int)_hotkeyDraftCombo.Modifiers;
            existing.VirtualKey = _hotkeyDraftCombo.VirtualKey;
            PersistHotkeys();
            RebuildHotkeyItems();
            Log($"更新全局快捷键 {comboText} → {steps.Count} 步「{label}」");
            SetLast($"已更新 {comboText}：{steps.Count} 步「{label}」");
        }
        else
        {
            var binding = new HotkeyBinding
            {
                Id = Guid.NewGuid().ToString("N"),
                Title = title,
                Steps = steps,
                Modifiers = (int)_hotkeyDraftCombo.Modifiers,
                VirtualKey = _hotkeyDraftCombo.VirtualKey,
                Enabled = true,
            };

            _hotkeyBindings.Add(binding);
            PersistHotkeys();
            RebuildHotkeyItems();
            Log($"新增全局快捷键 {comboText} → {steps.Count} 步「{label}」");
            SetLast($"已绑定 {comboText}：{steps.Count} 步「{label}」");
        }

        ResetHotkeyEditor();
        CloseHotkeyEditorRequested?.Invoke();
    }

    /// <summary>删除要二次确认：第一次只把这张卡片切成「确认删除」，3 秒内再点才真删。</summary>
    [RelayCommand]
    private async Task RemoveHotkey(HotkeyItemVm? item)
    {
        if (item == null) return;

        if (!item.IsConfirmingDelete)
        {
            foreach (var other in HotkeyItems) other.IsConfirmingDelete = false;
            item.IsConfirmingDelete = true;
            await Task.Delay(3000);
            item.IsConfirmingDelete = false;
            return;
        }

        var b = item.Binding;
        _hotkeyBindings.RemoveAll(x => x.Id == b.Id);
        PersistHotkeys();
        RebuildHotkeyItems();
        Log($"已删除全局快捷键 {item.ComboText}（{StepsLabel(b.Steps)}）");
    }

    [RelayCommand]
    private void ToggleHotkeyEnabled(HotkeyItemVm? item)
    {
        if (item == null) return;
        item.IsEnabled = !item.IsEnabled;
        item.Binding.Enabled = item.IsEnabled;
        PersistHotkeys();
        Log($"快捷键 {item.ComboText} 已{(item.IsEnabled ? "启用" : "停用")}");
    }

    /// <summary>同组合键只允许一条；编辑时排除自己。</summary>
    private HotkeyBinding? FindConflict(HotkeyCombo combo, string? exceptId = null) =>
        _hotkeyBindings.FirstOrDefault(b => b.Id != exceptId
                                            && b.VirtualKey == combo.VirtualKey
                                            && (HotkeyModifier)b.Modifiers == combo.Modifiers);

    /// <summary>改完绑定要同步给钩子：它在启动时拷了一份快照，不重新下发的话新增/删除不会生效。</summary>
    private void PersistHotkeys()
    {
        GlobalHotkeyService.SetBindings(HotkeysEnabled, _hotkeyBindings);
        HotkeyStore.Save(HotkeysEnabled, _hotkeyBindings);
    }

    private void ShowHint(string message, bool isError)
    {
        HotkeyHint = message;
        HotkeyHintIsError = isError;
        HasHotkeyHint = !string.IsNullOrEmpty(message);
    }

    private void SetLast(string message)
    {
        HotkeyLastText = message;
        HasHotkeyLast = !string.IsNullOrEmpty(message);
    }

    /// <summary>步骤名摘要：1 步直接用名字，多步「前两个 等 N 步」。</summary>
    private static string StepsLabel(IReadOnlyList<HotkeyStep> steps)
    {
        if (steps.Count == 0) return "（没有控制卡片）";
        if (steps.Count == 1) return steps[0].Name;

        var names = string.Join("、", steps.Take(2).Select(s => s.Name));
        return steps.Count > 2 ? $"{names} 等 {steps.Count} 步" : names;
    }

    // ===== 列表刷新 =====

    private void RebuildHotkeyItems()
    {
        HotkeyItems.Clear();
        foreach (var b in _hotkeyBindings)
        {
            var item = new HotkeyItemVm { Binding = b };
            ApplyHotkeyItem(item);
            HotkeyItems.Add(item);
        }

        SignalHotkeyCountChanged();
    }

    /// <summary>
    /// 设备 / 场景换代后只更新行内文本，不重建集合：常驻轮询每拍都会走到这里，
    /// Clear+Add 会让整页行控件反复创建。
    /// </summary>
    private void RefreshHotkeyViews()
    {
        foreach (var item in HotkeyItems) ApplyHotkeyItem(item);
        RefreshDraftStepDetails();
        OnPropertyChanged(nameof(HasNoHotkeyTarget));
    }

    private void ApplyHotkeyItem(HotkeyItemVm item)
    {
        var b = item.Binding;
        var resolved = ResolveHotkeyBinding(b);
        item.TargetText = resolved.Title;
        item.SubText = resolved.Meta;
        item.IsMissing = resolved.Missing > 0;
        item.MissingText = resolved.Missing == 0
            ? ""
            : resolved.Missing >= resolved.Total ? "不可用" : "部分不可用";
        item.ComboText = HotkeyKeyMap.Describe((HotkeyModifier)b.Modifiers, b.VirtualKey);
        item.IsEnabled = b.Enabled;
    }

    private void SignalHotkeyCountChanged()
    {
        HasHotkeys = HotkeyItems.Count > 0;
        OnPropertyChanged(nameof(HotkeyCountText));
    }

    private readonly struct HotkeyBindingInfo
    {
        public HotkeyBindingInfo(string title, string meta, int total, int missing)
        {
            Title = title;
            Meta = meta;
            Total = total;
            Missing = missing;
        }

        public string Title { get; }
        public string Meta { get; }
        public int Total { get; }
        public int Missing { get; }
    }

    private HotkeyBindingInfo ResolveHotkeyBinding(HotkeyBinding b)
    {
        var total = b.Steps.Count;
        var title = string.IsNullOrWhiteSpace(b.Title) ? StepsLabel(b.Steps) : b.Title;
        var missing = b.Steps.Count(IsStepTargetMissing);

        if (total == 1)
        {
            var step = b.Steps[0];
            return new HotkeyBindingInfo(title, SingleStepMeta(step), total, missing);
        }

        // 多步：按动作归类，给出「3 步 · 关闭 2 · 打开 1 · 执行 1」
        var summary = b.Steps
            .GroupBy(s => HotkeyKeyMap.ShortLabel(s.Kind))
            .Select(g => $"{g.Key} {g.Count()}");
        return new HotkeyBindingInfo(title, $"{total} 步 · {string.Join(" · ", summary)}", total, missing);
    }

    private bool IsStepTargetMissing(HotkeyStep step) => step.Type switch
    {
        HotkeyTargetType.Scene or HotkeyTargetType.Automation
            => _scenes.FirstOrDefault(s => s.SceneId == step.Id) == null,
        _ => FindHotkeyDevice(step.Id) == null,
    };

    private string SingleStepMeta(HotkeyStep step)
    {
        switch (step.Type)
        {
            case HotkeyTargetType.Device:
            {
                var device = FindHotkeyDevice(step.Id);
                if (device == null) return "当前家庭里没有这台设备";
                var state = !device.IsOnline ? "离线" : device.IsOn ? "开" : "关";
                return $"{HotkeyKeyMap.ShortLabel(step.Kind)}设备 · {device.RoomName} · {state}";
            }
            case HotkeyTargetType.Scene:
                return "执行 · 手动场景";
            default:
            {
                var scene = _scenes.FirstOrDefault(s => s.SceneId == step.Id);
                if (scene == null) return "自动化未加载，进入「智能」页后自动恢复";
                return $"{(scene.Enable ? "开启" : "关闭")}自动化 · 现在{(scene.Enable ? "已开启" : "已关闭")}";
            }
        }
    }

    /// <summary>按 did 找设备实例；虚拟子设备的 did 形如 parent.sN，TargetDid 也能命中。</summary>
    private MijiaDevice? FindHotkeyDevice(string did) =>
        _allDevices.FirstOrDefault(d => d.Did == did || d.TargetDid == did);

    // ===== 全局触发 =====

    private void OnHotkeyTriggered(HotkeyBinding binding) => _ = ExecuteHotkeyAsync(binding);

    /// <summary>
    /// 按绑定里的步骤顺序逐条下发：设备立刻改本地态再走 <see cref="ToggleDeviceAsync"/>，
    /// 场景走 RunSceneAsync，自动化走 SetSceneEnabledAsync；结束时汇总成一行动作反馈。
    /// </summary>
    private async Task ExecuteHotkeyAsync(HotkeyBinding binding)
    {
        var combo = HotkeyKeyMap.Describe((HotkeyModifier)binding.Modifiers, binding.VirtualKey);
        var hotkeyName = string.IsNullOrWhiteSpace(binding.Title) ? StepsLabel(binding.Steps) : binding.Title;
        DesktopLog.Write($"hotkey triggered → {combo} steps={binding.Steps.Count}");

        if (_client == null || !IsLoggedIn)
        {
            Log($"快捷键 {combo} 未执行：尚未登录米家");
            SetLast($"{combo} 未执行：尚未登录");
            HotkeyToast.Show($"{combo} · {hotkeyName}", "未执行：尚未登录米家", HotkeyToastKind.Failed);
            return;
        }

        if (binding.Steps.Count == 0)
        {
            SetLast($"{combo}：这条绑定没有控制卡片");
            HotkeyToast.Show($"{combo} · {hotkeyName}", "这条快捷键还没有控制卡片", HotkeyToastKind.Failed);
            return;
        }

        var total = binding.Steps.Count;
        var done = 0;
        var missing = 0;
        var offline = 0;
        var busy = 0;
        var failed = 0;
        var needReload = false;
        var deviceOrAutomationChanged = false;
        var sceneRan = false;
        string? singleDetail = null;
        string? singleToast = null;
        var singleKind = HotkeyToastKind.Done;

        foreach (var step in binding.Steps)
        {
            switch (step.Type)
            {
                case HotkeyTargetType.Device:
                {
                    var device = FindHotkeyDevice(step.Id);
                    if (device == null)
                    {
                        missing++;
                        DesktopLog.Write($"快捷键 {combo}：当前家庭里找不到「{step.Name}」，跳过");
                        break;
                    }

                    if (!device.IsOnline)
                    {
                        offline++;
                        DesktopLog.Write($"快捷键 {combo}：{device.Name} 离线，跳过");
                        break;
                    }

                    // 正在下发时先不动本地态：ToggleDeviceAsync 会在 IsBusy 时直接返回
                    if (device.IsBusy)
                    {
                        busy++;
                        break;
                    }

                    var wantOn = step.Kind switch
                    {
                        HotkeyActionKind.DeviceOn => true,
                        HotkeyActionKind.DeviceOff => false,
                        _ => !device.IsOn,
                    };
                    device.IsOn = wantOn;
                    await ToggleDeviceAsync(device);
                    done++;
                    deviceOrAutomationChanged = true;
                    singleDetail = $"{device.Name} · {(device.IsOn ? "开" : "关")}";
                    singleToast = device.Name;
                    singleKind = device.IsOn ? HotkeyToastKind.On : HotkeyToastKind.Off;
                    DesktopLog.Write($"快捷键 {combo}：{device.Name} 已{(wantOn ? "开" : "关")}");
                    break;
                }

                case HotkeyTargetType.Scene:
                {
                    var scene = _scenes.FirstOrDefault(s => s.SceneId == step.Id);
                    if (scene == null)
                    {
                        missing++;
                        needReload = true;
                        DesktopLog.Write($"快捷键 {combo}：场景「{step.Name}」尚未加载，跳过");
                        break;
                    }

                    try
                    {
                        await _client!.RunSceneAsync(scene);
                        done++;
                        sceneRan = true;
                        singleDetail = $"场景「{scene.Name}」已执行";
                        singleToast = singleDetail;
                        singleKind = HotkeyToastKind.Done;
                        DesktopLog.Write($"快捷键 {combo}：场景「{scene.Name}」已执行");
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        DesktopLog.Write($"快捷键执行场景「{scene.Name}」失败：{ex.Message}");
                    }
                    break;
                }

                default:
                {
                    var scene = _scenes.FirstOrDefault(s => s.SceneId == step.Id);
                    if (scene == null)
                    {
                        missing++;
                        needReload = true;
                        DesktopLog.Write($"快捷键 {combo}：自动化「{step.Name}」尚未加载，跳过");
                        break;
                    }

                    var want = step.Kind switch
                    {
                        HotkeyActionKind.AutomationOn => true,
                        HotkeyActionKind.AutomationOff => false,
                        _ => !scene.Enable,
                    };

                    _togglingScenes.Add(scene.SceneId);
                    try
                    {
                        await _client!.SetSceneEnabledAsync(scene, want);
                        scene.Enable = want;
                        foreach (var card in AutomationItems.Where(c => c.Scene.SceneId == scene.SceneId))
                            card.IsEnabled = want;
                        foreach (var card in DrawerAutomations.Where(c => c.Scene.SceneId == scene.SceneId))
                            card.IsEnabled = want;
                        done++;
                        deviceOrAutomationChanged = true;
                        singleDetail = $"自动化「{scene.Name}」已{(want ? "开启" : "关闭")}";
                        singleToast = $"自动化「{scene.Name}」";
                        singleKind = want ? HotkeyToastKind.On : HotkeyToastKind.Off;
                        DesktopLog.Write($"快捷键 {combo}：自动化「{scene.Name}」已{(want ? "开启" : "关闭")}");
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        DesktopLog.Write($"快捷键启停自动化「{scene.Name}」失败：{ex.Message}");
                    }
                    finally
                    {
                        _togglingScenes.Remove(scene.SceneId);
                    }
                    break;
                }
            }
        }

        if (deviceOrAutomationChanged)
        {
            RaiseSceneCounters();
            RefreshHotkeyViews();
        }
        if (sceneRan) await RefreshStatesQuietAsync();
        if (needReload && !_scenesLoadedOnce) _ = LoadScenesAsync();

        var tail = BuildStepTail(missing, offline, busy, failed);
        if (total == 1 && done == 1 && singleDetail != null && tail.Length == 0)
        {
            SetLast(singleDetail);
            Log($"快捷键 {combo} → {singleDetail}");
            HotkeyToast.Show($"{combo} · {hotkeyName}", singleToast ?? singleDetail, singleKind);
            return;
        }

        var summary = done == total
            ? $"{combo} · 已完成 {total} 步"
            : $"{combo} · 已完成 {done}/{total} 步{tail}";
        SetLast(summary);
        Log($"快捷键 {combo} → 完成 {done}/{total} 步{tail}");
        var multiKind = done == total
            ? HotkeyToastKind.Done
            : done == 0 ? HotkeyToastKind.Failed : HotkeyToastKind.Warn;
        HotkeyToast.Show($"{combo} · {hotkeyName}", done == total
            ? $"已按顺序执行 {total} 张控制卡片"
            : $"已完成 {done}/{total} 步{tail}", multiKind);
    }

    /// <summary>「（1 台离线 · 1 个目标不可用 · 1 步失败）」这种跳过说明。</summary>
    private static string BuildStepTail(int missing, int offline, int busy, int failed)
    {
        var parts = new List<string>();
        if (offline > 0) parts.Add($"{offline} 台离线");
        if (busy > 0) parts.Add($"{busy} 台正在操作");
        if (missing > 0) parts.Add($"{missing} 个目标不可用");
        if (failed > 0) parts.Add($"{failed} 步失败");
        return parts.Count == 0 ? "" : $"（{string.Join(" · ", parts)}）";
    }
}
