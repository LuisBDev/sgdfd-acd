using ACD.DocumentWorkspace;

namespace ACD.Tests.DocumentWorkspace;

public sealed class WorkspaceFileNamesTests : IDisposable
{
    private readonly string _directory = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "acd-tests-" + Guid.NewGuid().ToString("N"))).FullName;

    [Fact]
    public void FindLatestWord_picks_the_newest_docx_ignoring_lock_files()
    {
        var now = DateTime.Now;
        CreateFile("a.docx", now.AddMinutes(-2));
        CreateFile("b.docx", now);
        CreateFile("~$b.docx", now.AddSeconds(1));
        CreateFile("c.pdf", now.AddSeconds(2));

        var latest = WorkspaceFileNames.FindLatestWord(_directory);

        Assert.Equal(Path.Combine(_directory, "b.docx"), latest);
    }

    [Fact]
    public void FindLatestWord_returns_null_for_an_empty_directory()
    {
        Assert.Null(WorkspaceFileNames.FindLatestWord(_directory));
    }

    [Fact]
    public void IsValidFileName_accepts_a_plain_name_with_the_required_extension()
    {
        Assert.True(WorkspaceFileNames.IsValidFileName("x.docx", ".docx"));
    }

    [Theory]
    [InlineData("..\\x.docx")]
    [InlineData("x.doc")]
    public void IsValidFileName_rejects_paths_and_other_extensions(string name)
    {
        Assert.False(WorkspaceFileNames.IsValidFileName(name, ".docx"));
    }

    [Fact]
    public void IsValidFileName_rejects_names_longer_than_180_characters()
    {
        var name = new string('a', 176) + ".docx";

        Assert.False(WorkspaceFileNames.IsValidFileName(name, ".docx"));
    }

    [Fact]
    public void PdfCopyName_replaces_the_word_extension()
    {
        Assert.Equal("OFICIO_2026-1_20261008-204419.pdf", WorkspaceFileNames.PdfCopyName("OFICIO_2026-1_20261008-204419.docx"));
    }

    [Fact]
    public void AlternatePdfCopyName_appends_a_numbered_suffix()
    {
        Assert.Equal("OFICIO_2026-1_20261008-204419 (2).pdf", WorkspaceFileNames.AlternatePdfCopyName("OFICIO_2026-1_20261008-204419.docx"));
    }

    private void CreateFile(string name, DateTime lastWriteTime)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, [1]);
        File.SetLastWriteTime(path, lastWriteTime);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
