using System.Text.Json;
using BlueMail.Application.Common;
using BlueMail.Domain.Audit;
using BlueMail.Domain.Tenants;
using BlueMail.Infrastructure.Audit;
using BlueMail.Infrastructure.Persistence;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueMail.Application.Features.Admin.Policies;

public sealed record RoutingCondition(string? SenderContains = null, string? RecipientContains = null, string? SubjectContains = null, bool? HasAttachment = null);

public sealed record RoutingAction(string Type, string? Address = null);

/// <summary>What a message looks like to routing rules.</summary>
public sealed record RoutedMessage(string? Sender, IReadOnlyList<string> Recipients, string? Subject, bool HasAttachment);

/// <summary>
/// Administrator routing rules (PROMPT 7.4): inbound BCC (archive copy), REDIRECT, REJECT, QUARANTINE; outbound BCC and REJECT.
/// Conditions are case-insensitive "contains" tests on sender, any recipient and subject, plus "has attachment".
/// </summary>
public sealed class RoutingEngine(BlueMailDbContext db)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static bool Matches(RoutingCondition c, RoutedMessage m)
    {
        static bool Has(string? field, string? needle) => needle is null || (field ?? "").Contains(needle, StringComparison.OrdinalIgnoreCase);
        return Has(m.Sender, c.SenderContains)
               && (c.RecipientContains is null || m.Recipients.Any(r => r.Contains(c.RecipientContains, StringComparison.OrdinalIgnoreCase)))
               && Has(m.Subject, c.SubjectContains)
               && (c.HasAttachment is null || c.HasAttachment == m.HasAttachment);
    }

    public async Task<IReadOnlyList<RoutingAction>> EvaluateAsync(Guid tenant, RoutingDirection direction, RoutedMessage m, CancellationToken ct)
    {
        var rules = await db.RoutingRules.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.TenantId == tenant && r.Direction == direction && r.IsEnabled && r.DeletedAt == null).OrderBy(r => r.SortOrder).ToListAsync(ct);
        var actions = new List<RoutingAction>();
        foreach (var r in rules)
        {
            var c = JsonSerializer.Deserialize<RoutingCondition>(r.Condition, Json) ?? new RoutingCondition();
            if (Matches(c, m) && JsonSerializer.Deserialize<RoutingAction>(r.Action, Json) is { } a)
            {
                actions.Add(a);
            }
        }

        return actions;
    }
}

// ---------- policies API ----------

public sealed record PolicyValueDto(string Key, string Type, JsonElement Default, JsonElement? TenantValue, JsonElement? UnitValue);

public sealed record ListPoliciesQuery(Guid? OrgUnitId) : IRequest<IReadOnlyList<PolicyValueDto>>;

public sealed class ListPoliciesHandler(BlueMailDbContext db) : IRequestHandler<ListPoliciesQuery, IReadOnlyList<PolicyValueDto>>
{
    public async Task<IReadOnlyList<PolicyValueDto>> Handle(ListPoliciesQuery q, CancellationToken ct)
    {
        var rows = await db.Policies.AsNoTracking().Where(p => p.OrgUnitId == null || p.OrgUnitId == q.OrgUnitId).ToListAsync(ct);
        JsonElement? Get(string key, Guid? unit) => rows.FirstOrDefault(r => r.Key == key && r.OrgUnitId == unit) is { } r ? JsonDocument.Parse(r.Value).RootElement.Clone() : null;
        return PolicyKeys.All.Select(d => new PolicyValueDto(d.Key, d.Type, JsonSerializer.SerializeToElement(d.Default), Get(d.Key, null),
            q.OrgUnitId is null ? null : Get(d.Key, q.OrgUnitId))).ToList();
    }
}

public sealed record SetPolicyCommand(string Key, Guid? OrgUnitId, JsonElement? Value) : IRequest<Unit>;

/// <summary>Sets (or clears, with a null value) a policy for the tenant or one unit, checked against the key's type.</summary>
public sealed class SetPolicyHandler(BlueMailDbContext db, ICurrentUser me, AuditWriter audit) : IRequestHandler<SetPolicyCommand, Unit>
{
    public async Task<Unit> Handle(SetPolicyCommand c, CancellationToken ct)
    {
        var def = PolicyKeys.Find(c.Key) ?? throw AppException.NotFound("policy_unknown");
        if (c.OrgUnitId is { } unit && !await db.OrgUnits.AnyAsync(o => o.Id == unit, ct))
        {
            throw AppException.NotFound("org_unit_not_found");
        }

        var v = c.Value;
        var ok = v is null || v.Value.ValueKind == JsonValueKind.Null || def.Type switch
        {
            "int" => v.Value.ValueKind == JsonValueKind.Number && v.Value.TryGetInt32(out var i) && i is >= 0 and <= 100_000,
            "bool" => v.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "list" => v.Value.ValueKind == JsonValueKind.Array && v.Value.GetArrayLength() <= 1000
                      && v.Value.EnumerateArray().All(x => x.ValueKind == JsonValueKind.String && x.GetString()!.Length is > 0 and <= 320),
            "text" => v.Value.ValueKind == JsonValueKind.String && v.Value.GetString()!.Length <= 10_000,
            _ => false,
        };
        if (!ok)
        {
            throw AppException.BadRequest("policy_value_invalid");
        }

        var row = await db.Policies.FirstOrDefaultAsync(p => p.Key == c.Key && p.OrgUnitId == c.OrgUnitId, ct);
        var old = row?.Value;
        if (v is null || v.Value.ValueKind == JsonValueKind.Null)
        {
            if (row is not null)
            {
                db.Policies.Remove(row);
            }
        }
        else
        {
            row ??= db.Policies.Add(new Policy { TenantId = me.TenantId, OrgUnitId = c.OrgUnitId, Key = c.Key }).Entity;
            row.Value = v.Value.GetRawText();
        }

        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("policy.set", AuditResult.Success, me.TenantId, me.UserId, me.Email, me.Ip, me.UserAgent, "policy",
            c.Key + (c.OrgUnitId is null ? "" : "@" + c.OrgUnitId), old, v?.GetRawText()), ct);
        return Unit.Value;
    }
}

// ---------- routing rules API ----------

public sealed record RoutingRuleDto(Guid Id, string Name, string Direction, RoutingCondition Condition, RoutingAction Action, int SortOrder, bool IsEnabled);

public sealed record ListRoutingRulesQuery : IRequest<IReadOnlyList<RoutingRuleDto>>;

public sealed class ListRoutingRulesHandler(BlueMailDbContext db) : IRequestHandler<ListRoutingRulesQuery, IReadOnlyList<RoutingRuleDto>>
{
    public async Task<IReadOnlyList<RoutingRuleDto>> Handle(ListRoutingRulesQuery q, CancellationToken ct) =>
        (await db.RoutingRules.AsNoTracking().OrderBy(r => r.Direction).ThenBy(r => r.SortOrder).ToListAsync(ct))
        .Select(r => new RoutingRuleDto(r.Id, r.Name, EnumNames.ToUpperSnake(r.Direction.ToString()),
            JsonSerializer.Deserialize<RoutingCondition>(r.Condition, RoutingEngine.Json)!, JsonSerializer.Deserialize<RoutingAction>(r.Action, RoutingEngine.Json)!,
            r.SortOrder, r.IsEnabled)).ToList();
}

public sealed record SaveRoutingRuleCommand(Guid? Id, string Name, string Direction, RoutingCondition Condition, RoutingAction Action, bool IsEnabled) : IRequest<Guid>;

public sealed class SaveRoutingRuleValidator : AbstractValidator<SaveRoutingRuleCommand>
{
    public SaveRoutingRuleValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Direction).Must(d => d is "INBOUND" or "OUTBOUND").WithErrorCode("invalid_option");
        RuleFor(x => x.Action.Type).Must(t => t is "BCC" or "REDIRECT" or "REJECT" or "QUARANTINE").WithErrorCode("invalid_option");
        RuleFor(x => x).Must(x => x.Direction == "INBOUND" || x.Action.Type is "BCC" or "REJECT").WithErrorCode("routing_action_not_outbound");
        RuleFor(x => x.Action.Address).NotEmpty().Must(a => System.Net.Mail.MailAddress.TryCreate(a, out _)).When(x => x.Action.Type is "BCC" or "REDIRECT")
            .WithErrorCode(ValidationCodes.InvalidEmail);
        RuleFor(x => x.Condition).Must(c => c.SenderContains is not null || c.RecipientContains is not null || c.SubjectContains is not null || c.HasAttachment is not null)
            .WithErrorCode("rule_no_condition");
    }
}

public sealed class SaveRoutingRuleHandler(BlueMailDbContext db, ICurrentUser me, AuditWriter audit) : IRequestHandler<SaveRoutingRuleCommand, Guid>
{
    public async Task<Guid> Handle(SaveRoutingRuleCommand c, CancellationToken ct)
    {
        var dir = c.Direction == "INBOUND" ? RoutingDirection.Inbound : RoutingDirection.Outbound;
        var row = c.Id is { } id
            ? await db.RoutingRules.FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw AppException.NotFound("not_found")
            : db.RoutingRules.Add(new RoutingRule
            {
                TenantId = me.TenantId,
                SortOrder = (await db.RoutingRules.Where(r => r.Direction == dir).MaxAsync(r => (int?)r.SortOrder, ct) ?? 0) + 1,
            }).Entity;
        row.Name = c.Name.Trim();
        row.Direction = dir;
        row.Condition = JsonSerializer.Serialize(c.Condition, RoutingEngine.Json);
        row.Action = JsonSerializer.Serialize(c.Action with { Address = c.Action.Address?.Trim().ToLowerInvariant() }, RoutingEngine.Json);
        row.IsEnabled = c.IsEnabled;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("routing.save", AuditResult.Success, me.TenantId, me.UserId, me.Email, me.Ip, me.UserAgent, "routing_rule",
            row.Id.ToString(), NewValueJson: JsonSerializer.Serialize(c)), ct);
        return row.Id;
    }
}

public sealed record DeleteRoutingRuleCommand(Guid Id) : IRequest<Unit>;

public sealed class DeleteRoutingRuleHandler(BlueMailDbContext db, ICurrentUser me, AuditWriter audit) : IRequestHandler<DeleteRoutingRuleCommand, Unit>
{
    public async Task<Unit> Handle(DeleteRoutingRuleCommand c, CancellationToken ct)
    {
        if (await db.RoutingRules.Where(r => r.Id == c.Id).ExecuteUpdateAsync(r => r.SetProperty(x => x.DeletedAt, DateTimeOffset.UtcNow), ct) == 0)
        {
            throw AppException.NotFound("not_found");
        }

        await audit.WriteAsync(new AuditEntry("routing.delete", AuditResult.Success, me.TenantId, me.UserId, me.Email, me.Ip, me.UserAgent, "routing_rule", c.Id.ToString()), ct);
        return Unit.Value;
    }
}
