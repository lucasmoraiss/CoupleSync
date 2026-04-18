using CoupleSync.Api.Contracts.Chat;
using CoupleSync.Api.Validators;

namespace CoupleSync.UnitTests.AiChat;

[Trait("Category", "AiChat")]
public sealed class ChatRequestValidatorTests
{
    private static readonly ChatRequestValidator Validator = new();

    [Fact]
    public void ValidRequest_Passes()
    {
        var request = new ChatRequest("What is my budget?", null);
        var result = Validator.Validate(request);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void EmptyMessage_Fails()
    {
        var request = new ChatRequest("", null);
        var result = Validator.Validate(request);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == "Message");
    }

    [Fact]
    public void MessageTooLong_Fails()
    {
        var request = new ChatRequest(new string('a', 2001), null);
        var result = Validator.Validate(request);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == "Message");
    }

    [Fact]
    public void TooManyHistoryItems_Fails()
    {
        var history = Enumerable.Range(0, 21)
            .Select(_ => new ChatHistoryItem("user", "msg"))
            .ToList();
        var request = new ChatRequest("Hi", history);
        var result = Validator.Validate(request);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == "History");
    }

    [Fact]
    public void InvalidHistoryRole_Fails()
    {
        var history = new List<ChatHistoryItem> { new("assistant", "hello") };
        var request = new ChatRequest("Hi", history);
        var result = Validator.Validate(request);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void HistoryContentTooLong_Fails()
    {
        var history = new List<ChatHistoryItem> { new("user", new string('x', 2001)) };
        var request = new ChatRequest("Hi", history);
        var result = Validator.Validate(request);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void NullHistory_Passes()
    {
        var request = new ChatRequest("Tell me about my spending.", null);
        var result = Validator.Validate(request);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void ValidHistoryWithTwoItems_Passes()
    {
        var history = new List<ChatHistoryItem>
        {
            new("user", "previous question"),
            new("model", "previous answer")
        };
        var request = new ChatRequest("Follow-up", history);
        var result = Validator.Validate(request);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void ExactlyMaxLengthMessage_Passes()
    {
        var request = new ChatRequest(new string('a', 2000), null);
        var result = Validator.Validate(request);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void ExactlyTwentyHistoryItems_Passes()
    {
        var history = Enumerable.Range(0, 20)
            .Select(_ => new ChatHistoryItem("user", "msg"))
            .ToList();
        var request = new ChatRequest("Hi", history);
        var result = Validator.Validate(request);
        Assert.True(result.IsValid);
    }
}
