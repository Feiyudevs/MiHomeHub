using System;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;

namespace MiHome.Controls;

/// <summary>
/// 设备列表专用：清理回收容器时释放旧数据，保留可回收的 DataTemplate 子树。
/// 默认 ItemsControl 会连 ContentTemplate 一起清掉，从而重新构建整张卡片。
/// </summary>
public sealed class RecyclingItemsControl : ItemsControl
{
    private RetainedDataTemplate? _retainedTemplate;

    protected override Type StyleKeyOverride => typeof(ItemsControl);

    protected override void PrepareContainerForItemOverride(Control container, object? item, int index)
    {
        if (container is not ContentPresenter presenter || ReferenceEquals(container, item))
        {
            base.PrepareContainerForItemOverride(container, item, index);
            return;
        }

        if (ItemTemplate is IRecyclingDataTemplate template)
        {
            if (!ReferenceEquals(_retainedTemplate?.Inner, template))
                _retainedTemplate = new RetainedDataTemplate(this, template);

            presenter.SetCurrentValue(ContentPresenter.ContentTemplateProperty, _retainedTemplate);
        }
        else
        {
            // 模板变化时显式替换旧 wrapper；非可回收模板按普通 ContentPresenter 行为处理。
            presenter.SetCurrentValue(ContentPresenter.ContentTemplateProperty, ItemTemplate);
        }

        // 容器可能来自回收池；不能用基类的 SetIfUnset 跳过数据更新。
        presenter.SetCurrentValue(ContentPresenter.ContentProperty, item);
        base.PrepareContainerForItemOverride(container, item, index);
    }

    protected override void ClearContainerForItemOverride(Control container)
    {
        if (container is ContentPresenter presenter
            && presenter.ContentTemplate is RetainedDataTemplate template
            && ReferenceEquals(template.Owner, this))
        {
            // wrapper 匹配 null 且返回既有 child，因此这一步既解除旧数据绑定，又保留控件树。
            presenter.ClearValue(ContentPresenter.ContentProperty);
            presenter.SetCurrentValue(DataContextProperty, null);
            return;
        }

        base.ClearContainerForItemOverride(container);
    }

    private sealed class RetainedDataTemplate : IRecyclingDataTemplate
    {
        public RecyclingItemsControl Owner { get; }
        public IRecyclingDataTemplate Inner { get; }

        public RetainedDataTemplate(RecyclingItemsControl owner, IRecyclingDataTemplate inner)
        {
            Owner = owner;
            Inner = inner;
        }

        // 强类型 XAML DataTemplate 原本不匹配 null，直接清 Content 也会丢失 child。
        public bool Match(object? data) => data is null || Inner.Match(data);

        public Control? Build(object? data) => Build(data, null);

        public Control? Build(object? data, Control? existing)
        {
            if (data is null)
                return existing;

            return Inner.Build(data, existing);
        }
    }
}
