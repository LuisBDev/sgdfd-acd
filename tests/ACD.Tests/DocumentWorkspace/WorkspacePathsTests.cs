using ACD.Configuration;
using ACD.DocumentWorkspace;

namespace ACD.Tests.DocumentWorkspace;

public sealed class WorkspacePathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "acd-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Builds_the_remito_directory_under_the_root()
    {
        var paths = new WorkspacePaths(new DocumentWorkspaceOptions { RootDirectory = _root });

        var ok = paths.TryGetRemitoDirectory("2026", "0000121972", out var directory);

        Assert.True(ok);
        Assert.Equal(Path.Combine(_root, "2026", "0000121972"), directory);
    }

    [Theory]
    [InlineData("26", "1")]
    [InlineData("2026", "12a")]
    [InlineData("2026", "..\\x")]
    [InlineData("2026", "12345678901")]
    public void Rejects_invalid_remito_keys(string anio, string numeroEmision)
    {
        var paths = new WorkspacePaths(new DocumentWorkspaceOptions { RootDirectory = _root });

        Assert.False(paths.TryGetRemitoDirectory(anio, numeroEmision, out _));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
