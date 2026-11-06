using System;
using System.Collections.Generic;
using MiHome.Models;

namespace MiHome.ViewModels;

public partial class MainViewModel
{
    /// <summary>
    /// 调试用：加载指定数量的虚拟设备，用于定位内存占用是否在 UI 渲染层。
    /// </summary>
    public void LoadTestDevices(int count)
    {
        // 先退出登录，清空现有设备
        Logout();

        // 创建虚拟设备
        var newDevices = new List<MijiaDevice>(count);
        var categories = new[] { "light", "switch", "curtain", "air-conditioner", "outlet" };
        var rooms = new[] { "客厅", "卧室", "厨房", "卫生间", "阳台" };

        for (int i = 0; i < count; i++)
        {
            var d = new MijiaDevice
            {
                Did = $"test_did_{i:D4}",
                Name = $"测试设备 {i + 1}",
                Model = $"test_model_{i % 10}",
                HomeId = "test_home",
                RoomName = rooms[i % rooms.Length],
                SpecType = $"urn:miot-spec-v2:device:{categories[i % categories.Length]}:test:1",
                IsOnline = true,
                IsOn = i % 2 == 0,
            };
            newDevices.Add(d);
        }

        // 加载到主设备列表
        _allDevices = newDevices;
        RebuildRooms();
        ApplyFilter();
        RebuildHomeGroups();
        UpdateStats();
        RefreshSpeakers();

        Log($"测试：已加载 {count} 个虚拟设备");
    }

    /// <summary>
    /// 调试用：清空所有测试设备，回到空态。
    /// </summary>
    public void ClearTestDevices()
    {
        Logout();
        Log("测试：已清空所有虚拟设备");
    }
}
