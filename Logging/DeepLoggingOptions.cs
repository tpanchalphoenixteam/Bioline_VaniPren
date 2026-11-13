namespace PH.SapB1.Vaniperen.Logging;

public sealed class DeepLoggingOptions
{
    public bool Enabled { get; set; } = true;

    public bool LogRequestBody { get; set; } = true;

    public bool LogResponseBody { get; set; } = true;

    public bool LogSapPayload { get; set; } = true;

    public int MaxBodyChars { get; set; } = 30000;

    public List<string> ExcludedPaths { get; set; } = new()
    {
        "/swagger",
        "/health"
    };
}