using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.Income.Commands;
using CoupleSync.Application.Income.Queries;
using CoupleSync.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CoupleSync.Application.Income;

public sealed class IncomeService
{
    private readonly IIncomeSourceRepository _repository;
    private readonly ICoupleRepository _coupleRepository;
    private readonly IDateTimeProvider _dateTimeProvider;

    private const int MaxSourcesPerUserPerMonth = 20;

    public IncomeService(
        IIncomeSourceRepository repository,
        ICoupleRepository coupleRepository,
        IDateTimeProvider dateTimeProvider)
    {
        _repository = repository;
        _coupleRepository = coupleRepository;
        _dateTimeProvider = dateTimeProvider;
    }

    public async Task<IncomeSourceDto> CreateAsync(
        Guid coupleId,
        Guid userId,
        string month,
        CreateIncomeSourceInput input,
        CancellationToken ct)
    {
        var count = await _repository.CountByUserAndMonthAsync(userId, coupleId, month, ct);
        if (count >= MaxSourcesPerUserPerMonth)
            throw new UnprocessableEntityException(
                "INCOME_SOURCE_LIMIT",
                $"A user may have at most {MaxSourcesPerUserPerMonth} income sources per month.");

        var now = _dateTimeProvider.UtcNow;
        var source = IncomeSource.Create(
            coupleId, userId, month, input.Name, input.Amount, input.Currency, input.IsShared, now);

        try
        {
            await _repository.AddAsync(source, ct);
            await _repository.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (
            ex.InnerException?.Message.Contains("23505") == true
            || ex.InnerException?.Message.Contains("unique", StringComparison.OrdinalIgnoreCase) == true)
        {
            throw new ConflictException(
                "INCOME_SOURCE_DUPLICATE",
                "An income source with this name already exists for this month.");
        }

        return MapToDto(source);
    }

    public async Task<IncomeSourceDto> UpdateAsync(
        Guid coupleId,
        Guid userId,
        Guid sourceId,
        UpdateIncomeSourceInput input,
        CancellationToken ct)
    {
        var source = await _repository.GetByIdAsync(sourceId, coupleId, ct)
            ?? throw new NotFoundException("INCOME_SOURCE_NOT_FOUND", "Income source not found.");

        if (!source.CanBeEditedBy(userId))
            throw new ForbiddenException("INCOME_SOURCE_FORBIDDEN", "You can only edit your own or shared income sources.");

        var now = _dateTimeProvider.UtcNow;
        source.Update(input.Name, input.Amount, input.IsShared, now);
        await _repository.SaveChangesAsync(ct);

        return MapToDto(source);
    }

    public async Task DeleteAsync(
        Guid coupleId,
        Guid userId,
        Guid sourceId,
        CancellationToken ct)
    {
        var source = await _repository.GetByIdAsync(sourceId, coupleId, ct)
            ?? throw new NotFoundException("INCOME_SOURCE_NOT_FOUND", "Income source not found.");

        if (!source.CanBeEditedBy(userId))
            throw new ForbiddenException("INCOME_SOURCE_FORBIDDEN", "You can only delete your own or shared income sources.");

        await _repository.DeleteAsync(source, ct);
        await _repository.SaveChangesAsync(ct);
    }

    public async Task<MonthlyIncomeDto> GetMonthlyIncomeAsync(
        Guid coupleId,
        Guid userId,
        string month,
        CancellationToken ct)
    {
        var sources = await _repository.GetByMonthAsync(coupleId, month, ct);
        var couple = await _coupleRepository.FindByIdWithMembersAsync(coupleId, ct);

        var currentUserName = couple?.Members.FirstOrDefault(m => m.Id == userId)?.Name ?? "Você";
        var partner = couple?.Members.FirstOrDefault(m => m.Id != userId);

        var personalSources = sources
            .Where(s => s.UserId == userId && !s.IsShared)
            .Select(MapToDto)
            .ToList();

        var partnerSources = partner is not null
            ? sources
                .Where(s => s.UserId == partner.Id && !s.IsShared)
                .Select(MapToDto)
                .ToList()
            : new List<IncomeSourceDto>();

        var sharedSources = sources
            .Where(s => s.IsShared)
            .Select(MapToDto)
            .ToList();

        var personalTotal = personalSources.Sum(s => s.Amount);
        var partnerTotal = partnerSources.Sum(s => s.Amount);
        var sharedTotal = sharedSources.Sum(s => s.Amount);

        var personalGroup = new IncomeGroupDto(userId, currentUserName, personalSources, personalTotal);

        var partnerGroup = partner is not null
            ? new IncomeGroupDto(partner.Id, partner.Name, partnerSources, partnerTotal)
            : null;

        var sharedGroup = new IncomeGroupDto(null, null, sharedSources, sharedTotal);

        return new MonthlyIncomeDto(
            month,
            "BRL",
            personalGroup,
            partnerGroup,
            sharedGroup,
            personalTotal + partnerTotal + sharedTotal);
    }

    public async Task<MonthlyIncomeDto> GetCurrentMonthIncomeAsync(
        Guid coupleId,
        Guid userId,
        CancellationToken ct)
    {
        var now = _dateTimeProvider.UtcNow;
        var currentMonth = $"{now.Year:D4}-{now.Month:D2}";
        return await GetMonthlyIncomeAsync(coupleId, userId, currentMonth, ct);
    }

    private static IncomeSourceDto MapToDto(IncomeSource source)
        => new(source.Id, source.UserId, source.Name, source.Amount, source.Currency,
               source.IsShared, source.CreatedAtUtc, source.UpdatedAtUtc);
}
