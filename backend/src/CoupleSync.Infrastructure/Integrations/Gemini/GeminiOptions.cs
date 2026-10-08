namespace CoupleSync.Infrastructure.Integrations.Gemini;

public sealed class GeminiOptions
{
    public string Endpoint { get; set; } = "https://generativelanguage.googleapis.com/v1beta";
    public string Model { get; set; } = "gemini-flash-latest";
    public const int DefaultThinkingHeadroomTokens = 2000;

    /// <summary>
    /// Room for the "thinking" of the model, added to the output limit of every call of the chain (the thinking
    /// counts in maxOutputTokens). Gemini__ThinkingHeadroomTokens replaces it.
    /// </summary>
    public int ThinkingHeadroomTokens { get; set; } = DefaultThinkingHeadroomTokens;

    public int MaxTokens { get; set; } = 1024;
    public bool Enabled { get; set; } = false;
    public string ApiKey { get; set; } = string.Empty;
}
