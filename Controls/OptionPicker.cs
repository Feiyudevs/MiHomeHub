using System.Collections;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using MiHome.Models;

namespace MiHome.Controls;

/// <summary>
/// 自绘下拉选择：触发器显示当前项，点开是玻璃浮层列表，选中项只上柔色底不改边框。
/// Options 给一组 <see cref="OptionItem"/>，SelectedValue 双向绑到调用方的状态上。
/// </summary>
public partial class OptionPicker : UserControl
{
    public static readonly StyledProperty<IEnumerable?> OptionsProperty =
        AvaloniaProperty.Register<OptionPicker, IEnumerable?>(nameof(Options));

    public static readonly StyledProperty<object?> SelectedValueProperty =
        AvaloniaProperty.Register<OptionPicker, object?>(nameof(SelectedValue),
            defaultBindingMode: BindingMode.TwoWay);

    public IEnumerable? Options
    {
        get => GetValue(OptionsProperty);
        set => SetValue(OptionsProperty, value);
    }

    public object? SelectedValue
    {
        get => GetValue(SelectedValueProperty);
        set => SetValue(SelectedValueProperty, value);
    }

    static OptionPicker()
    {
        OptionsProperty.Changed.AddClassHandler<OptionPicker>((x, _) => x.Rebind());
        SelectedValueProperty.Changed.AddClassHandler<OptionPicker>((x, _) => x.SyncSelection());
    }

    public OptionPicker()
    {
        InitializeComponent();

        Pop.PlacementTarget = Trigger;
        Trigger.Click += (_, _) => Pop.IsOpen = !Pop.IsOpen;

        Pop.Opened += (_, _) =>
        {
            Panel.MinWidth = Trigger.Bounds.Width;
            Trigger.Classes.Add("open");
            // 先让浮层以闭合态挂上树，下一帧再翻到展开态，过渡才有得播
            Dispatcher.UIThread.Post(() => Panel.Classes.Add("open"), DispatcherPriority.Render);
        };
        Pop.Closed += (_, _) =>
        {
            Trigger.Classes.Remove("open");
            Panel.Classes.Remove("open");
        };

        // 行按钮在 ItemTemplate 里，挂事件路由接一次比每条模板写 Click 稳
        Panel.AddHandler(Button.ClickEvent, OnItemClick);

        ActualThemeVariantChanged += (_, _) => SyncTheme();
        SyncTheme();

        Rebind();
    }

    private void SyncTheme()
    {
        if (ActualThemeVariant == ThemeVariant.Light) Panel.Classes.Add("light");
        else Panel.Classes.Remove("light");
    }

    private void Rebind()
    {
        List.ItemsSource = Options;
        SyncSelection();
    }

    private void SyncSelection()
    {
        string? current = null;
        if (Options is IEnumerable items)
        {
            foreach (var raw in items)
            {
                if (raw is not OptionItem option) continue;
                option.IsSelected = Equals(option.Value, SelectedValue);
                if (option.IsSelected) current = option.Label;
            }
        }

        Cur.Text = current ?? "选择";
    }

    private void OnItemClick(object? sender, RoutedEventArgs args)
    {
        if (args.Source is Button { Tag: OptionItem option })
            SetCurrentValue(SelectedValueProperty, option.Value);
        Pop.IsOpen = false;
    }
}
