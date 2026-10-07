using CoupleSync.Api.Contracts.Goals;
using CoupleSync.Api.Validators;
using CoupleSync.UnitTests.Support;

namespace CoupleSync.UnitTests.Goals;

/// <summary>
/// A11 — PATCH /goals/{id} must accept an update that only changes the saved amount, and the
/// deadline rule of the edit must be the same as the one used on creation ("today or later").
/// </summary>
public sealed class UpdateGoalRequestValidatorTests
{
    private static readonly DateTime Now = new(2026, 10, 5, 15, 30, 0, DateTimeKind.Utc);
    private static readonly FixedDateTimeProvider Clock = new(Now);

    private static bool IsValid(UpdateGoalRequest request) => new UpdateGoalRequestValidator(Clock).Validate(request).IsValid;

    [Fact]
    public void OnlyCurrentAmount_IsAValidUpdate()
        => Assert.True(IsValid(new UpdateGoalRequest(null, null, null, 1500m, null)));

    [Fact]
    public void CurrentAmountZero_IsAValidUpdate()
        => Assert.True(IsValid(new UpdateGoalRequest(null, null, null, 0m, null)));

    [Fact]
    public void NoFieldAtAll_IsRejected()
    {
        var result = new UpdateGoalRequestValidator(Clock).Validate(new UpdateGoalRequest(null, null, null, null, null));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage == "Informe pelo menos um campo para atualizar.");
    }

    [Fact]
    public void DeadlineToday_IsAccepted_LikeOnCreation()
    {
        // Earlier today (already "in the past" by the clock) and midnight of today are both "today".
        var earlierToday = new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc);

        Assert.True(IsValid(new UpdateGoalRequest(null, null, null, null, earlierToday)));
        Assert.True(IsValid(new UpdateGoalRequest("Viagem", null, null, 200m, earlierToday)));

        // Same rule as CreateGoalRequestValidator.
        Assert.True(new CreateGoalRequestValidator(Clock)
            .Validate(new CreateGoalRequest("Viagem", null, 1000m, "BRL", earlierToday)).IsValid);
    }

    [Fact]
    public void DeadlineInTheFuture_IsAccepted()
        => Assert.True(IsValid(new UpdateGoalRequest(null, null, null, null, Now.AddDays(30))));

    [Fact]
    public void DeadlineYesterday_IsRejected_WithTheSameMessageAsCreation()
    {
        var yesterday = new DateTime(2026, 10, 4, 23, 59, 0, DateTimeKind.Utc);

        var update = new UpdateGoalRequestValidator(Clock).Validate(new UpdateGoalRequest(null, null, null, null, yesterday));
        var create = new CreateGoalRequestValidator(Clock).Validate(new CreateGoalRequest("Viagem", null, 1000m, "BRL", yesterday));

        Assert.False(update.IsValid);
        Assert.False(create.IsValid);
        Assert.Equal(
            create.Errors.Single(e => e.PropertyName == nameof(CreateGoalRequest.Deadline)).ErrorMessage,
            update.Errors.Single(e => e.PropertyName == nameof(UpdateGoalRequest.Deadline)).ErrorMessage);
    }

    // "Hoje" do prazo é o dia de Brasília: às 22h de 15/10 em Brasília já é 16/10 em UTC.
    [Fact]
    public void Deadline_TodayInBrasiliaIsAccepted_EvenWhenUtcAlreadyRolledOver()
    {
        var clock = new FixedDateTimeProvider(new DateTime(2026, 10, 16, 1, 0, 0, DateTimeKind.Utc)); // 15/10 22:00 BRT
        var today = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(new UpdateGoalRequestValidator(clock).Validate(new UpdateGoalRequest(null, null, null, null, today)).IsValid);
        Assert.True(new CreateGoalRequestValidator(clock).Validate(new CreateGoalRequest("Viagem", null, 1000m, "BRL", today)).IsValid);
    }

    [Fact]
    public void Deadline_YesterdayInBrasiliaIsRejected_EvenWhenUtcIsStillOnTheSameDay()
    {
        var clock = new FixedDateTimeProvider(new DateTime(2026, 10, 15, 2, 0, 0, DateTimeKind.Utc)); // 14/10 23:00 BRT
        var yesterdayBrt = new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
        var todayBrt = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);

        Assert.False(new CreateGoalRequestValidator(clock).Validate(new CreateGoalRequest("Viagem", null, 1000m, "BRL", yesterdayBrt)).IsValid);
        Assert.True(new CreateGoalRequestValidator(clock).Validate(new CreateGoalRequest("Viagem", null, 1000m, "BRL", todayBrt)).IsValid);
        Assert.False(new UpdateGoalRequestValidator(clock).Validate(new UpdateGoalRequest(null, null, null, null, yesterdayBrt)).IsValid);
    }

    [Fact]
    public void DeadlineNotSent_IsNotValidated()
        => Assert.True(IsValid(new UpdateGoalRequest("Novo título", null, null, null, null)));
}
