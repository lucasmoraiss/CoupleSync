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
        var history = new List<ChatHistoryItem> { new("user", new string('x', 16001)) };
        var request = new ChatRequest("Hi", history);
        var result = Validator.Validate(request);
        Assert.False(result.IsValid);
    }

    // Issue #38, review 2 (I2): an earlier answer comes back whole as history and is longer than a question;
    // refused here, every following question of the conversation was refused. The server cuts it.
    [Theory]
    [InlineData(2001)]
    [InlineData(16000)]
    public void HistoryContentLongerThanAQuestion_Passes(int length)
    {
        var history = new List<ChatHistoryItem> { new("model", new string('x', length)) };
        Assert.True(Validator.Validate(new ChatRequest("Hi", history)).IsValid);
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
