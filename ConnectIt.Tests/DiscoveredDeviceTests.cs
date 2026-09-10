using System.Net;
using ConnectIt.Wpf.Models;

namespace ConnectIt.Tests;

public class DiscoveredDeviceTests
{
    private static DiscoveredDevice Create(string instanceName, string? friendlyName = null) => new()
    {
        InstanceName = instanceName,
        HostName = "host.local",
        Address = IPAddress.Loopback,
        Port = 12345,
        FriendlyName = friendlyName,
    };

    [Fact]
    public void DisplayName_PrefersFriendlyNameWhenPresent()
    {
        var device = Create("MyPC-a1b2._connectit._tcp.local", friendlyName: "MyPC");

        Assert.Equal("MyPC", device.DisplayName);
    }

    [Fact]
    public void DisplayName_FallsBackToFirstLabelOfInstanceNameWhenNoFriendlyName()
    {
        var device = Create("MyPC-a1b2._connectit._tcp.local");

        Assert.Equal("MyPC-a1b2", device.DisplayName);
    }

    [Fact]
    public void DisplayName_FallsBackWhenFriendlyNameIsEmpty()
    {
        var device = Create("MyPC-a1b2._connectit._tcp.local", friendlyName: "");

        Assert.Equal("MyPC-a1b2", device.DisplayName);
    }

    [Fact]
    public void Key_IsInstanceName()
    {
        var device = Create("MyPC-a1b2._connectit._tcp.local");

        Assert.Equal(device.InstanceName, device.Key);
    }
}
