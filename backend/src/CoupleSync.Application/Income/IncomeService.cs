using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.Income.Commands;
using CoupleSync.Application.Income.Queries;
using CoupleSync.Domain.Entities;
using CoupleSync.Domain.ValueObjects;
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
                $"Cada pessoa pode ter no máximo {MaxSourcesPerUserPerMonth} fontes de renda por mês.");

        var now = _dateTimeProvider.UtcNow;
        var source = IncomeSource.Create(
            coupleId, userId, month, input.Name, input.Amount, input.Currency, input.IsShared, now, input.IsRecurring ?? true);

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
                "Já existe uma fonte de renda com esse nome neste mês.");
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
            ?? throw new NotFoundException("INCOME_SOURCE_NOT_FOUND", "Fonte de renda não encontrada.");

        if (!source.CanBeEditedBy(userId))
            throw new ForbiddenException("INCOME_SOURCE_FORBIDDEN", "Você só pode editar suas próprias fontes de renda ou as compartilhadas.");

        var now = _dateTimeProvider.UtcNow;
        source.Update(input.Name, input.Amount, input.IsShared, input.IsRecurring, now);
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
            ?? throw new NotFoundException("INCOME_SOURCE_NOT_FOUND", "Fonte de renda não encontrada.");

        if (!source.CanBeEditedBy(userId))
            throw new ForbiddenException("INCOME_SOURCE_FORBIDDEN", "Você só pode excluir suas próprias fontes de renda ou as compartilhadas.");

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

        // A group may have more than two members: every other member is a partner.
        var partners = (couple?.Members ?? [])
            .Where(m => m.Id != userId)
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Id)
            .ToList();

        var personalSources = sources
            .Where(s => s.UserId == userId && !s.IsShared)
            .Select(MapToDto)
            .ToList();

        var sharedSources = sources
            .Where(s => s.IsShared)
            .Select(MapToDto)
            .ToList();

        // One group per partner (new field `partnersIncome`).
        var partnerGroups = partners
            .Select(partner =>
            {
                var partnerSources = sources
                    .Where(s => s.UserId == partner.Id && !s.IsShared)
                    .Select(MapToDto)
                    .ToList();
                return new IncomeGroupDto(partner.Id, partner.Name, partnerSources, SumInReais(partnerSources));
            })
            .ToList();

        var personalTotal = SumInReais(personalSources);
        var partnersTotal = partnerGroups.Sum(g => g.Total);
        var sharedTotal = SumInReais(sharedSources);

        var personalGroup = new IncomeGroupDto(userId, currentUserName, personalSources, personalTotal);

        // Existing field `partnerIncome`: with a single partner it is that partner's group, as
        // before; with several it aggregates all of them so that personal + partner + shared
        // still adds up to the total for clients that only know this field.
        var partnerGroup = partnerGroups.Count switch
        {
            0 => null,
            1 => partnerGroups[0],
            _ => new IncomeGroupDto(
                null,
                string.Join(", ", partnerGroups.Select(g => g.UserName)),
                partnerGroups.SelectMany(g => g.Sources).ToList(),
                partnersTotal)
        };

        var sharedGroup = new IncomeGroupDto(null, null, sharedSources, sharedTotal);

        return new MonthlyIncomeDto(
            month,
            "BRL",
            personalGroup,
            partnerGroup,
            sharedGroup,
            personalTotal + partnersTotal + sharedTotal,
            partnerGroups);
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

    // Sources stored in another currency stay listed but never enter a total in reais.
    private static decimal SumInReais(IEnumerable<IncomeSourceDto> sources)
        => sources.Where(s => CurrencyRules.IsBrl(s.Currency)).Sum(s => s.Amount);

    private static IncomeSourceDto MapToDto(IncomeSource source)
        => new(source.Id, source.UserId, source.Name, source.Amount, source.Currency,
               source.IsShared, source.IsRecurring, source.CreatedAtUtc, source.UpdatedAtUtc);
}
