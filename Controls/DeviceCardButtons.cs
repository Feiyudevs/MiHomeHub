using System;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace MiHome.Controls;

// Dedicated style keys keep every card from subscribing to the unrelated application-wide
// Button.<class> rules and the Fluent button theme. Input, command and automation behavior
// still comes from Button / ToggleButton; the existing card styles supply the appearance.
public sealed class DeviceCardButton : Button
{
    protected override Type StyleKeyOverride => typeof(DeviceCardButton);
}

public sealed class DevicePowerButton : ToggleButton
{
    protected override Type StyleKeyOverride => typeof(DevicePowerButton);
}

public sealed class DeviceMoreButton : Button
{
    protected override Type StyleKeyOverride => typeof(DeviceMoreButton);
}
