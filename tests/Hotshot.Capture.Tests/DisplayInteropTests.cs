using System.Runtime.InteropServices;
using Hotshot.Interop;

namespace Hotshot.Capture.Tests;

public sealed class DisplayInteropTests
{
    [Fact]
    public void ColorAndTopologyPacketsMatchWindowsSdkAbi()
    {
        Assert.Equal(20, Marshal.SizeOf<NativeDisplayConfiguration.Header>());
        Assert.Equal(20, Marshal.SizeOf<NativeDisplayConfiguration.Source>());
        Assert.Equal(48, Marshal.SizeOf<NativeDisplayConfiguration.Target>());
        Assert.Equal(72, Marshal.SizeOf<NativeDisplayConfiguration.Path>());
        Assert.Equal(64, Marshal.SizeOf<NativeDisplayConfiguration.Mode>());
        Assert.Equal(84, Marshal.SizeOf<NativeDisplayConfiguration.SourceName>());
        Assert.Equal(36, Marshal.SizeOf<NativeDisplayConfiguration.AdvancedColor>());
        Assert.Equal(32, Marshal.OffsetOf<NativeDisplayConfiguration.AdvancedColor>("ActiveColorMode").ToInt32());
    }
}
