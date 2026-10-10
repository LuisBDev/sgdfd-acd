namespace ACD.Files;

public interface IShellLauncher
{
    void Open(string filePath);
    void OpenFolder(string directory);
}
