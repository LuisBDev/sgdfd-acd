namespace ACD.DocumentWorkspace;

public sealed class ConversionException : Exception
{
    public ConversionException(string code, Exception? innerException = null)
        : base(code, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}
