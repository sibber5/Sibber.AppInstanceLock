using System.Runtime.CompilerServices;
using Xunit;

namespace Sibber.AppInstanceLock.Tests.Shared;

public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1
    ) : base(sourceFilePath, sourceLineNumber)
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows only";
    }
}

public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1
    ) : base(sourceFilePath, sourceLineNumber)
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows only";
    }
}

public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1
    ) : base(sourceFilePath, sourceLineNumber)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) Skip = "Unix only";
    }
}

public sealed class UnixTheoryAttribute : TheoryAttribute
{
    public UnixTheoryAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1
    ) : base(sourceFilePath, sourceLineNumber)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) Skip = "Unix only";
    }
}
