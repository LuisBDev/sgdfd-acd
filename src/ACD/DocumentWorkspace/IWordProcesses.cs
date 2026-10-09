namespace ACD.DocumentWorkspace;

public interface IWordProcesses
{
    IReadOnlySet<int> CurrentIds();
    bool IsAutomationInstance(int processId);
    void Kill(int processId);
}
