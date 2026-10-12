using System.Reflection;
using Hotshot.Core.Settings;

namespace Hotshot.Core.Tests;

public sealed class ArchitectureTests
{
    private static readonly string[] NativeDependencies =
        ["Windows", "WinRT", "Microsoft.UI", "Microsoft.Graphics", "Vortice", "NAudio"];

    [Fact]
    public void CoreDoesNotReferenceNativeUiOrMediaAssemblies()
    {
        var references = typeof(AppSettings).Assembly.GetReferencedAssemblies();
        Assert.DoesNotContain(references, reference =>
            NativeDependencies.Any(prefix => reference.Name?.StartsWith(prefix, StringComparison.Ordinal) == true));
    }

    [Fact]
    public void CoreDoesNotDeclareNativeInteropEntryPoints()
    {
        var methods = typeof(AppSettings).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly));
        Assert.DoesNotContain(methods, method => (method.Attributes & MethodAttributes.PinvokeImpl) != 0);
    }
}
