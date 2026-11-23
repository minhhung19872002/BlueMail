using System.Text.Json;
using BlueMail.Application.Common;
using BlueMail.Domain.Mail;
using BlueMail.Infrastructure.Persistence;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueMail.Application.Features.Rules;

/// <summary>Inbox category rules (PROMPT 6.6): system defaults are visible, the tenant's own are editable by administrators.</summary>
public sealed record CategoryRuleDto(int Id, string? FromDomain, string? ListId, string Category, bool IsSystem, bool IsEnabled);

public sealed record ListCategoryRulesQuery : IRequest<IReadOnlyList<CategoryRuleDto>>;

public sealed class ListCategoryRulesHandler(BlueMailDbContext db, ICurrentUser me) : IRequestHandler<ListCategoryRulesQuery, IReadOnlyList<CategoryRuleDto>>
{
    private sealed record Match(string? FromDomain, string? ListId);

    public async Task<IReadOnlyList<CategoryRuleDto>> Handle(ListCategoryRulesQuery q, CancellationToken ct)
    {
        var rows = await db.CategoryRules.AsNoTracking().Where(r => r.TenantId == null || r.TenantId == me.TenantId)
            .OrderBy(r => r.TenantId == null).ThenBy(r => r.SortOrder).ToListAsync(ct);
        return rows.Select(r =>
        {
            var m = JsonSerializer.Deserialize<Match>(r.Match, FilterActions.Json);
            return new CategoryRuleDto(r.Id, m?.FromDomain, m?.ListId, r.Category["CATEGORY_".Length..], r.TenantId is null, r.IsEnabled);
        }).ToList();
    }
}

public sealed record AddCategoryRuleCommand(string? FromDomain, string? ListId, string Category) : IRequest<int>;

public sealed class AddCategoryRuleValidator : AbstractValidator<AddCategoryRuleCommand>
{
    public AddCategoryRuleValidator()
    {
        RuleFor(x => x).Must(x => !string.IsNullOrWhiteSpace(x.FromDomain) || !string.IsNullOrWhiteSpace(x.ListId)).WithErrorCode("rule_no_condition");
        RuleFor(x => x.FromDomain).MaximumLength(253).Matches("^[a-zA-Z0-9.-]+$").When(x => !string.IsNullOrWhiteSpace(x.FromDomain)).WithErrorCode(ValidationCodes.InvalidFormat);
        RuleFor(x => x.ListId).MaximumLength(300);
        RuleFor(x => x.Category).Must(c => c is "PRIMARY" or "SOCIAL" or "PROMOTIONS" or "UPDATES" or "FORUMS").WithErrorCode("invalid_option");
    }
}

public sealed class AddCategoryRuleHandler(BlueMailDbContext db, ICurrentUser me) : IRequestHandler<AddCategoryRuleCommand, int>
{
    public async Task<int> Handle(AddCategoryRuleCommand c, CancellationToken ct)
    {
        var next = await db.CategoryRules.Where(r => r.TenantId == me.TenantId).MaxAsync(r => (int?)r.SortOrder, ct) ?? 0;
        var row = new CategoryRule
        {
            TenantId = me.TenantId,
            Match = JsonSerializer.Serialize(new { fromDomain = c.FromDomain?.Trim().ToLowerInvariant() is { Length: > 0 } d ? d : null, listId = c.ListId?.Trim() is { Length: > 0 } l ? l : null }),
            Category = "CATEGORY_" + c.Category,
            SortOrder = next + 1,
        };
        db.CategoryRules.Add(row);
        await db.SaveChangesAsync(ct);
        return row.Id;
    }
}

public sealed record DeleteCategoryRuleCommand(int Id) : IRequest<Unit>;

public sealed class DeleteCategoryRuleHandler(BlueMailDbContext db, ICurrentUser me) : IRequestHandler<DeleteCategoryRuleCommand, Unit>
{
    public async Task<Unit> Handle(DeleteCategoryRuleCommand c, CancellationToken ct) =>
        await db.CategoryRules.Where(r => r.Id == c.Id && r.TenantId == me.TenantId).ExecuteDeleteAsync(ct) == 0 ? throw AppException.NotFound("not_found") : Unit.Value;
}
