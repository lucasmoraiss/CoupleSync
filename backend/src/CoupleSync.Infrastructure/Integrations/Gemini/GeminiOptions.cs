namespace CoupleSync.Infrastructure.Integrations.Gemini;

public sealed class GeminiOptions
{
    public string Endpoint { get; set; } = "https://generativelanguage.googleapis.com/v1beta";
    public string Model { get; set; } = "gemini-2.0-flash";
    public int MaxTokens { get; set; } = 1024;
    public bool Enabled { get; set; } = false;
    public string ApiKey { get; set; } = string.Empty;
}
