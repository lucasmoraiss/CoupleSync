using CoupleSync.Application.Common.Interfaces;

namespace CoupleSync.UnitTests.Support;

/// <summary>
/// A Pluggy client that is never asked anything: it only records which connections were forgotten, as
/// "forget:{connection}" in <paramref name="steps"/> (the same list the fake repository writes its steps to).
/// </summary>
public sealed class RecordingPluggyClient(List<string> steps) : IPluggyClient
{
    public List<Guid> Forgotten { get; } = new();

    public void ForgetConnection(Guid connectionId)
    {
        Forgotten.Add(connectionId);
        steps.Add($"forget:{connectionId}");
    }

    public Task<PluggyAuth> AuthenticateAsync(string clientId, string clientSecret, CancellationToken ct)
        => throw new InvalidOperationException("Leaving a group never calls Pluggy.");

    public Task<PluggyItem> GetItemAsync(PluggyAuth auth, string itemId, CancellationToken ct)
        => throw new InvalidOperationException("Leaving a group never calls Pluggy.");

    public Task<IReadOnlyList<PluggyAccount>> GetAccountsAsync(PluggyAuth auth, string itemId, CancellationToken ct)
        => throw new InvalidOperationException("Leaving a group never calls Pluggy.");
}
