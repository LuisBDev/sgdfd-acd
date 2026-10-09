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
    public void FindLatestWord_orders_by_max_of_creation_and_last_write()
    {
        var day = new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Local);
        CreateFile("a.docx", creationTime: day.AddHours(9), changedTime: day.AddHours(10));
        CreateFile("b.docx", creationTime: day.AddHours(11), changedTime: day.AddHours(8));

        var latest = WorkspaceFileNames.FindLatestWord(_directory);

        Assert.Equal(Path.Combine(_directory, "b.docx"), latest);
    }

    [Fact]
    public void FindLatestWord_breaks_ties_by_name_descending()
    {
        var changedAt = new DateTime(2026, 10, 9, 8, 15, 0, DateTimeKind.Local);
        CreateFile("x_20261009-081240.docx", changedAt);
        CreateFile("x_20261009-075123.docx", changedAt);

        var latest = WorkspaceFileNames.FindLatestWord(_directory);

        Assert.Equal(Path.Combine(_directory, "x_20261009-081240.docx"), latest);
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

    private void CreateFile(string name, DateTime changedAt) =>
        CreateFile(name, changedAt, changedAt);

    private void CreateFile(string name, DateTime creationTime, DateTime changedTime)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, [1]);
        File.SetCreationTime(path, creationTime);
        File.SetLastWriteTime(path, changedTime);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
