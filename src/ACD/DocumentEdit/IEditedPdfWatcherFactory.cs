namespace ACD.DocumentEdit;

public interface IEditedPdfWatcherFactory
{
    IEditedPdfWatcher Create(DocumentEditWorkspace workspace);
}
