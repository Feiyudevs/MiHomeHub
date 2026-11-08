using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MiHome.Models;
using MiHome.ViewModels;

namespace MiHome.Views;

public partial class SceneEditorView : UserControl
{
    /// <summary>拖拽时在进程内传递的格式标识（只在应用内有效，不经过系统剪贴板）。</summary>
    private const string RowDragFormat = "MiHome.SceneEditRow";

    private Point _gripStart;
    private SceneEditActionRow? _dragCandidate;

    /// <summary>当前挂着插入提示的卡片：DragLeave 不保证触发，切目标时主动清上一张。</summary>
    private Control? _hoveredCard;

    /// <summary>当前已订阅「请求打开触发条件弹窗」的视图模型（换 DataContext / 卸载时解绑）。</summary>
    private MainViewModel? _triggerHostVm;

    public SceneEditorView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => BindTriggerHost(DataContext as MainViewModel);
        AttachedToVisualTree += (_, _) => BindTriggerHost(DataContext as MainViewModel);
        DetachedFromVisualTree += (_, _) => BindTriggerHost(null);
    }

    // ===== 触发条件：添加 / 编辑都走独立弹窗 =====

    private void BindTriggerHost(MainViewModel? vm)
    {
        if (ReferenceEquals(_triggerHostVm, vm)) return;
        if (_triggerHostVm != null)
        {
            _triggerHostVm.TriggerEditorOpened -= OnTriggerEditorOpened;
            _triggerHostVm.ActionEditorOpened -= OnActionEditorOpened;
        }
        _triggerHostVm = vm;
        if (_triggerHostVm != null)
        {
            _triggerHostVm.TriggerEditorOpened += OnTriggerEditorOpened;
            _triggerHostVm.ActionEditorOpened += OnActionEditorOpened;
        }
    }

    /// <summary>
    /// 视图模型把设备与条件清单准备好后发这个请求，这里弹出模态窗；
    /// 关窗（确定 / 取消 / Esc / 右上角）都由弹窗和视图模型自己收尾，这里不额外处理。
    /// </summary>
    private async void OnTriggerEditorOpened(object? sender, EventArgs e)
    {
        var vm = _triggerHostVm;
        if (vm == null) return;
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            vm.Log("[TriggerEditor] 编辑面板不在窗口树里，弹不出来");
            return;
        }

        try
        {
            var window = new TriggerEditorWindow(vm);
            window.FitTo(owner);
            await window.ShowDialog(owner);
        }
        catch (Exception ex)
        {
            // 弹窗起不来不该把整个应用带走：收掉编辑态并把原因写进日志面板
            vm.CancelTriggerEditorCommand.Execute(null);
            vm.Log($"[TriggerEditor] 打开弹窗失败：{ex.GetType().Name} {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[TriggerEditor] 打开弹窗失败：{ex}");
        }
    }

    // ===== 添加动作：独立弹窗（与触发条件同一套宿主） =====

    /// <summary>视图模型准备好设备清单后发这个请求，这里弹出模态窗。</summary>
    private async void OnActionEditorOpened(object? sender, EventArgs e)
    {
        var vm = _triggerHostVm;
        if (vm == null) return;
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            vm.Log("[ActionEditor] 编辑面板不在窗口树里，弹不出来");
            return;
        }

        try
        {
            var window = new ActionEditorWindow(vm);
            window.FitTo(owner);
            await window.ShowDialog(owner);
        }
        catch (Exception ex)
        {
            vm.CancelAddActionCommand.Execute(null);
            vm.Log($"[ActionEditor] 打开弹窗失败：{ex.GetType().Name} {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[ActionEditor] 打开弹窗失败：{ex}");
        }
    }

    // ===== 触发条件行：可编辑的行点一下载入编辑器 =====

    private void OnTriggerRowPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control row || row.DataContext is not SceneTriggerRow item) return;
        if (!item.Editable) return;
        // 行尾的删除钮自己处理点击，不要再把整行带进编辑态
        if (e.Source is Visual v && v.FindAncestorOfType<Button>() != null) return;
        if (DataContext is MainViewModel vm) vm.EditTriggerCommand.Execute(item);
    }

    // ===== 延时秒数步进 =====

    private void OnDelayStepDown(object? sender, RoutedEventArgs e) => StepDelay(sender, -10);

    private void OnDelayStepUp(object? sender, RoutedEventArgs e) => StepDelay(sender, 10);

    /// <summary>按 10 秒一档加减，范围与原来 NumericUpDown 一致（0–86400）。</summary>
    private static void StepDelay(object? sender, int delta)
    {
        if (sender is not Control { DataContext: SceneEditActionRow row }) return;
        row.DelaySeconds = Math.Clamp(Math.Round(row.DelaySeconds) + delta, 0, 86400);
    }

    // ===== 拖拽换顺序 =====

    /// <summary>按住左侧手柄才开始记录候选行，避免和卡片里的输入控件抢指针。</summary>
    private void OnRowGripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control grip) return;
        var point = e.GetCurrentPoint(grip);
        if (!point.Properties.IsLeftButtonPressed) return;

        _gripStart = point.Position;
        _dragCandidate = grip.DataContext as SceneEditActionRow;
    }

    /// <summary>
    /// 移动监在整张卡片上（不抓取指针）：拖动时指针可能移出 22px 宽的手柄，
    /// 绑在手柄上就会收不到事件、拖不起来。只有从手柄按下过才会有候选行。
    /// </summary>
    private async void OnRowPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragCandidate == null || sender is not Control card) return;

        var point = e.GetCurrentPoint(card);
        if (!point.Properties.IsLeftButtonPressed)
        {
            _dragCandidate = null;
            return;
        }

        var delta = point.Position - _gripStart;
        if (Math.Abs(delta.X) < 6 && Math.Abs(delta.Y) < 6) return;

        var row = _dragCandidate;
        _dragCandidate = null;
        row.IsDragging = true;

        try
        {
            var data = new DataObject();
            data.Set(RowDragFormat, row);
            await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
        }
        catch
        {
            // 拖拽被系统取消（Esc / 指针丢失）时不必打扰用户
        }
        finally
        {
            row.IsDragging = false;
            // 拖到空白处松手 / Esc 取消时 Drop 不会来，这里兜底把提示擦干净
            if (_hoveredCard != null)
            {
                ClearDropHint(_hoveredCard);
                _hoveredCard = null;
            }
        }
    }

    private void OnRowDragOver(object? sender, DragEventArgs e)
    {
        if (!e.Data.Contains(RowDragFormat)) return;
        if (sender is Control card)
        {
            // DragLeave 在快速划过多张卡时未必逐张触发，切目标前先擦掉上一张
            if (_hoveredCard != null && !ReferenceEquals(_hoveredCard, card))
                ClearDropHint(_hoveredCard);
            _hoveredCard = card;

            // 拖到自己身上不给插入提示：落到自己位置上等于什么也没做
            var isSource = e.Data.Get(RowDragFormat) is SceneEditActionRow s
                           && ReferenceEquals(s, card.DataContext);
            card.Classes.Set("dragover", !isSource);
            var below = e.GetPosition(card).Y > card.Bounds.Height / 2;
            card.Classes.Set("dropafter", !isSource && below);
            card.Classes.Set("dropbefore", !isSource && !below);
        }
        e.DragEffects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void OnRowDragLeave(object? sender, DragEventArgs e)
    {
        if (sender is not Control card) return;
        ClearDropHint(card);
        if (ReferenceEquals(_hoveredCard, card)) _hoveredCard = null;
    }

    /// <summary>清掉拖拽期间挂在卡片上的三个状态类（上下插入线 + 悬停态）。</summary>
    private static void ClearDropHint(Control card)
    {
        card.Classes.Set("dragover", false);
        card.Classes.Set("dropbefore", false);
        card.Classes.Set("dropafter", false);
    }

    private void OnRowDrop(object? sender, DragEventArgs e)
    {
        if (sender is not Control card) return;
        ClearDropHint(card);
        _hoveredCard = null;
        if (e.Data.Get(RowDragFormat) is not SceneEditActionRow source) return;
        if (card.DataContext is not SceneEditActionRow target) return;
        if (DataContext is not MainViewModel vm) return;

        var from = vm.SceneEditRows.IndexOf(source);
        var to = vm.SceneEditRows.IndexOf(target);
        if (from < 0 || to < 0) return;

        // 落点在目标卡片下半部分 → 插到它后面
        if (e.GetPosition(card).Y > card.Bounds.Height / 2) to++;
        if (from < to) to--;

        vm.MoveSceneEditRow(from, to);
        e.Handled = true;
    }
}
