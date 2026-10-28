using System.Runtime.InteropServices;
using ACD.DocumentWorkspace;

namespace ACD.Tests.DocumentWorkspace;

public sealed class WordDocumentCloserTests
{
    private static readonly string Target = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "acd-close-target.docx"));

    [Fact]
    public async Task Closes_the_exact_document_without_saving_on_an_sta_thread()
    {
        var target = new FakeDocument(Target);
        var other = new FakeDocument(Path.Combine(Path.GetTempPath(), "other.docx"));
        var closer = new WordDocumentCloser(path => path == Target ? target : other);
        Assert.True(await closer.CloseWithoutSavingAsync(Target, CancellationToken.None));
        Assert.Equal(0, target.SaveChanges);
        Assert.Equal(ApartmentState.STA, target.Apartment);
        Assert.Null(other.SaveChanges);
    }

    [Fact]
    public async Task Missing_running_document_does_not_create_or_open_word()
    {
        var closer = new WordDocumentCloser(_ => null);
        Assert.False(await closer.CloseWithoutSavingAsync(Target, CancellationToken.None));
    }

    [Fact]
    public async Task Wrong_document_is_never_closed()
    {
        var other = new FakeDocument(Path.Combine(Path.GetTempPath(), "other.docx"));
        var closer = new WordDocumentCloser(_ => other);
        await Assert.ThrowsAsync<WorkspaceFileChangedException>(() => closer.CloseWithoutSavingAsync(Target, CancellationToken.None));
        Assert.Null(other.SaveChanges);
    }

    [Fact]
    public async Task Busy_word_returns_actionable_failure()
    {
        var closer = new WordDocumentCloser(_ => throw new COMException("Call rejected", unchecked((int)0x80010001)));
        await Assert.ThrowsAsync<WorkspaceFileLockedException>(() => closer.CloseWithoutSavingAsync(Target, CancellationToken.None));
    }

    [Fact]
    public async Task Cancellation_before_close_does_not_touch_the_document()
    {
        var target = new FakeDocument(Target);
        using var cts = new CancellationTokenSource();
        var closer = new WordDocumentCloser(_ => { cts.Cancel(); return target; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => closer.CloseWithoutSavingAsync(Target, cts.Token));
        Assert.Null(target.SaveChanges);
    }

    public sealed class FakeDocument(string fullName)
    {
        public string FullName { get; } = fullName;
        public int? SaveChanges { get; private set; }
        public ApartmentState? Apartment { get; private set; }
        public void Close(int saveChanges)
        {
            SaveChanges = saveChanges;
            Apartment = Thread.CurrentThread.GetApartmentState();
        }
    }
}
