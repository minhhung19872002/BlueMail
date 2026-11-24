using System.Net.Mail;
using System.Text;
using System.Text.Json;
using BlueMail.Application.Common;
using BlueMail.Domain.Contacts;
using BlueMail.Domain.Identity;
using BlueMail.Infrastructure.Persistence;
using BlueMail.MailStore;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueMail.Application.Features.Contacts;

public sealed record ContactDto(Guid Id, string DisplayName, IReadOnlyList<LabeledValue> Emails, IReadOnlyList<LabeledValue> Phones, string? Org, string? Title,
    string? Notes, bool HasPhoto, bool IsAutoSaved, DateTimeOffset? LastContactedAt, IReadOnlyList<Guid> GroupIds);

public static class ContactMapping
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<LabeledValue> Values(string json) => JsonSerializer.Deserialize<List<LabeledValue>>(json, Json) ?? [];

    public static ContactDto ToDto(Contact c, IReadOnlyList<Guid> groups) => new(c.Id, c.DisplayName, Values(c.Emails), Values(c.Phones), c.Org, c.Title, c.Notes,
        c.PhotoSha256 is not null, c.IsAutoSaved, c.LastContactedAt, groups);

    public static ContactCard ToCard(Contact c) => new()
    {
        DisplayName = c.DisplayName, Emails = Values(c.Emails), Phones = Values(c.Phones), Org = c.Org, Title = c.Title, Notes = c.Notes,
    };

    /// <summary>Writes the card's fields onto the row (and the lower-cased address keys used for lookups).</summary>
    public static void Apply(Contact c, ContactCard card)
    {
        var emails = card.Emails.Where(e => MailAddress.TryCreate(e.Value.Trim(), out _))
            .Select(e => e with { Value = e.Value.Trim(), Label = Clip(e.Label, 50) }).DistinctBy(e => e.Value.ToLowerInvariant()).Take(20).ToList();
        var phones = card.Phones.Where(p => p.Value.Trim().Length is > 0 and <= 50).Select(p => p with { Value = p.Value.Trim(), Label = Clip(p.Label, 50) })
            .DistinctBy(p => p.Value).Take(20).ToList();
        c.DisplayName = Clip(card.DisplayName.Trim(), 300) is { Length: > 0 } n ? n : emails.FirstOrDefault()?.Value ?? "";
        c.Emails = JsonSerializer.Serialize(emails, Json);
        c.Phones = JsonSerializer.Serialize(phones, Json);
        c.EmailKeys = emails.Select(e => e.Value.ToLowerInvariant()).ToList();
        c.Org = Clip(card.Org?.Trim(), 300);
        c.Title = Clip(card.Title?.Trim(), 300);
        c.Notes = Clip(card.Notes, 10_000);
    }

    private static string? Clip(string? s, int max) => string.IsNullOrEmpty(s) ? null : s.Length <= max ? s : s[..max];
}

// ---------- list / get / save / delete ----------

public sealed record ListContactsQuery(string? Q, Guid? GroupId, bool Other, int Page, int PageSize) : IRequest<Paged<ContactDto>>;

public sealed class ListContactsHandler(BlueMailDbContext db, ICurrentUser me) : IRequestHandler<ListContactsQuery, Paged<ContactDto>>
{
    public async Task<Paged<ContactDto>> Handle(ListContactsQuery q, CancellationToken ct)
    {
        var (page, size) = Paging.Clamp(q.Page, q.PageSize);
        var query = db.Contacts.AsNoTracking().Where(c => c.UserId == me.UserId && c.IsAutoSaved == q.Other);
        if (q.GroupId is { } g)
        {
            query = query.Where(c => db.ContactGroupMembers.Any(m => m.GroupId == g && m.ContactId == c.Id));
        }

        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var like = "%" + q.Q.Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
            query = query.Where(c => EF.Functions.ILike(EF.Functions.Unaccent(c.DisplayName), EF.Functions.Unaccent(like))
                                     || c.EmailKeys.Any(e => EF.Functions.ILike(e, like)) || EF.Functions.ILike(c.Org ?? "", like));
        }

        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(c => c.DisplayName).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        var ids = rows.Select(r => r.Id).ToList();
        var groups = await db.ContactGroupMembers.AsNoTracking().Where(m => ids.Contains(m.ContactId)).ToListAsync(ct);
        return new Paged<ContactDto>(rows.Select(r => ContactMapping.ToDto(r, groups.Where(x => x.ContactId == r.Id).Select(x => x.GroupId).ToList())).ToList(),
            total, page, size);
    }
}

public sealed record SaveContactCommand(Guid? Id, string DisplayName, IReadOnlyList<LabeledValue> Emails, IReadOnlyList<LabeledValue> Phones, string? Org,
    string? Title, string? Notes, IReadOnlyList<Guid>? GroupIds) : IRequest<Guid>;

public sealed class SaveContactValidator : AbstractValidator<SaveContactCommand>
{
    public SaveContactValidator()
    {
        RuleFor(x => x).Must(x => !string.IsNullOrWhiteSpace(x.DisplayName) || x.Emails.Count > 0).WithErrorCode("contact_empty");
        RuleFor(x => x.DisplayName).MaximumLength(300).WithErrorCode(ValidationCodes.TooLong);
        RuleForEach(x => x.Emails).Must(e => MailAddress.TryCreate(e.Value.Trim(), out _)).WithErrorCode(ValidationCodes.InvalidFormat);
        RuleFor(x => x.Emails.Count).LessThanOrEqualTo(20);
        RuleFor(x => x.Phones.Count).LessThanOrEqualTo(20);
        RuleFor(x => x.Notes).MaximumLength(10_000).WithErrorCode(ValidationCodes.TooLong);
    }
}

public sealed class SaveContactHandler(BlueMailDbContext db, ICurrentUser me) : IRequestHandler<SaveContactCommand, Guid>
{
    public const int MaxContacts = 25_000;

    public async Task<Guid> Handle(SaveContactCommand c, CancellationToken ct)
    {
        Contact row;
        if (c.Id is { } id)
        {
            row = await db.Contacts.FirstOrDefaultAsync(x => x.Id == id && x.UserId == me.UserId, ct) ?? throw AppException.NotFound("contact_not_found");
        }
        else
        {
            if (await db.Contacts.CountAsync(x => x.UserId == me.UserId, ct) >= MaxContacts)
            {
                throw AppException.BadRequest("too_many_contacts");
            }

            row = new Contact { TenantId = me.TenantId, UserId = me.UserId };
            db.Contacts.Add(row);
        }

        ContactMapping.Apply(row, new ContactCard { DisplayName = c.DisplayName, Emails = c.Emails, Phones = c.Phones, Org = c.Org, Title = c.Title, Notes = c.Notes });
        row.IsAutoSaved = false; // editing an "other contact" makes it a real one
        if (c.GroupIds is { } groups)
        {
            var own = await db.ContactGroups.Where(g => g.UserId == me.UserId && groups.Contains(g.Id)).Select(g => g.Id).ToListAsync(ct);
            await db.SaveChangesAsync(ct);
            await db.ContactGroupMembers.Where(m => m.ContactId == row.Id).ExecuteDeleteAsync(ct);
            db.ContactGroupMembers.AddRange(own.Select(g => new ContactGroupMember { TenantId = me.TenantId, GroupId = g, ContactId = row.Id }));
        }

        await db.SaveChangesAsync(ct);
        return row.Id;
    }
}

public sealed record DeleteContactsCommand(IReadOnlyList<Guid> Ids) : IRequest<int>;

public sealed class DeleteContactsHandler(BlueMailDbContext db, ICurrentUser me) : IRequestHandler<DeleteContactsCommand, int>
{
    public async Task<int> Handle(DeleteContactsCommand c, CancellationToken ct)
    {
        await db.ContactGroupMembers.Where(m => c.Ids.Contains(m.ContactId) && db.Contacts.Any(x => x.Id == m.ContactId && x.UserId == me.UserId)).ExecuteDeleteAsync(ct);
        return await db.Contacts.Where(x => c.Ids.Contains(x.Id) && x.UserId == me.UserId).ExecuteUpdateAsync(x => x.SetProperty(y => y.DeletedAt, DateTimeOffset.UtcNow), ct);
    }
}

// ---------- photo ----------

public sealed record SetContactPhotoCommand(Guid Id, byte[]? Content, string? ContentType) : IRequest<Unit>;

public sealed class SetContactPhotoHandler(BlueMailDbContext db, ICurrentUser me, BlobStore blobs) : IRequestHandler<SetContactPhotoCommand, Unit>
{
    public async Task<Unit> Handle(SetContactPhotoCommand c, CancellationToken ct)
    {
        var row = await db.Contacts.FirstOrDefaultAsync(x => x.Id == c.Id && x.UserId == me.UserId, ct) ?? throw AppException.NotFound("contact_not_found");
        if (c.Content is null)
        {
            row.PhotoSha256 = null;
            row.PhotoContentType = null;
        }
        else
        {
            var type = ImageType(c.Content) ?? throw AppException.BadRequest("photo_not_image");
            if (c.Content.Length > VCard.MaxPhotoBytes)
            {
                throw AppException.BadRequest("photo_too_large");
            }

            var stream = new MemoryStream(c.Content, writable: false);
            var sha = await BlobStore.HashAsync(stream, ct);
            await BlobRefs.EnsureAsync(db, blobs, me.TenantId, sha, stream, ct);

            row.PhotoSha256 = sha;
            row.PhotoContentType = type;
        }

        await db.SaveChangesAsync(ct);
        return Unit.Value;
    }

    /// <summary>JPEG or PNG by magic bytes, whatever the client claims.</summary>
    public static string? ImageType(byte[] b) =>
        b is [0xFF, 0xD8, 0xFF, ..] ? "image/jpeg" : b is [0x89, 0x50, 0x4E, 0x47, ..] ? "image/png" : null;
}

public sealed record GetContactPhotoQuery(Guid Id) : IRequest<(byte[] Content, string ContentType)?>;

public sealed class GetContactPhotoHandler(BlueMailDbContext db, ICurrentUser me, BlobStore blobs) : IRequestHandler<GetContactPhotoQuery, (byte[] Content, string ContentType)?>
{
    public async Task<(byte[] Content, string ContentType)?> Handle(GetContactPhotoQuery q, CancellationToken ct)
    {
        var row = await db.Contacts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == q.Id && x.UserId == me.UserId, ct);
        if (row?.PhotoSha256 is null || row.PhotoContentType is null)
        {
            return null;
        }

        var ms = new MemoryStream();
        await blobs.ReadAsync(me.TenantId, row.PhotoSha256, ms, ct: ct);
        return (ms.ToArray(), row.PhotoContentType);
    }
}

// ---------- groups ----------

public sealed record ContactGroupDto(Guid Id, string Name, int Members);

public sealed record ListContactGroupsQuery : IRequest<IReadOnlyList<ContactGroupDto>>;

public sealed class ListContactGroupsHandler(BlueMailDbContext db, ICurrentUser me) : IRequestHandler<ListContactGroupsQuery, IReadOnlyList<ContactGroupDto>>
{
    public async Task<IReadOnlyList<ContactGroupDto>> Handle(ListContactGroupsQuery q, CancellationToken ct) =>
        await db.ContactGroups.AsNoTracking().Where(g => g.UserId == me.UserId).OrderBy(g => g.Name)
            .Select(g => new ContactGroupDto(g.Id, g.Name,
                db.ContactGroupMembers.Count(m => m.GroupId == g.Id && db.Contacts.Any(c => c.Id == m.ContactId && c.DeletedAt == null))))
            .ToListAsync(ct);
}

public sealed record SaveContactGroupCommand(Guid? Id, string Name) : IRequest<Guid>;

public sealed class SaveContactGroupValidator : AbstractValidator<SaveContactGroupCommand>
{
    public SaveContactGroupValidator() => RuleFor(x => x.Name).NotEmpty().MaximumLength(200).WithErrorCode(ValidationCodes.TooLong);
}

public sealed class SaveContactGroupHandler(BlueMailDbContext db, ICurrentUser me) : IRequestHandler<SaveContactGroupCommand, Guid>
{
    public async Task<Guid> Handle(SaveContactGroupCommand c, CancellationToken ct)
    {
        var name = c.Name.Trim();
        if (await db.ContactGroups.AnyAsync(g => g.UserId == me.UserId && g.Name == name && g.Id != (c.Id ?? Guid.Empty), ct))
        {
            throw AppException.Conflict("group_exists");
        }

        var row = c.Id is { } id
            ? await db.ContactGroups.FirstOrDefaultAsync(g => g.Id == id && g.UserId == me.UserId, ct) ?? throw AppException.NotFound("group_not_found")
            : db.ContactGroups.Add(new ContactGroup { TenantId = me.TenantId, UserId = me.UserId }).Entity;
        row.Name = name;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            throw AppException.Conflict("group_exists"); // parallel create with the same name (unique index)
        }

        return row.Id;
    }
}

public sealed record DeleteContactGroupCommand(Guid Id) : IRequest<Unit>;

public sealed class DeleteContactGroupHandler(BlueMailDbContext db, ICurrentUser me) : IRequestHandler<DeleteContactGroupCommand, Unit>
{
    public async Task<Unit> Handle(DeleteContactGroupCommand c, CancellationToken ct)
    {
        var n = await db.ContactGroups.Where(g => g.Id == c.Id && g.UserId == me.UserId).ExecuteUpdateAsync(g => g.SetProperty(x => x.DeletedAt, DateTimeOffset.UtcNow), ct);
        if (n == 0)
        {
            throw AppException.NotFound("group_not_found");
        }

        await db.ContactGroupMembers.Where(m => m.GroupId == c.Id).ExecuteDeleteAsync(ct);
        return Unit.Value;
    }
}

// ---------- import / export / merge ----------

public sealed record ImportContactsCommand(string FileName, string Text, Guid? GroupId) : IRequest<ImportContactsResult>;

public sealed record ImportContactsResult(int Created, int Merged, int Skipped);

/// <summary>vCard or CSV (Google, Outlook). A card whose address is already known is merged into that contact.</summary>
public sealed class ImportContactsHandler(BlueMailDbContext db, ICurrentUser me, BlobStore blobs) : IRequestHandler<ImportContactsCommand, ImportContactsResult>
{
    public async Task<ImportContactsResult> Handle(ImportContactsCommand c, CancellationToken ct)
    {
        var isVcf = c.FileName.EndsWith(".vcf", StringComparison.OrdinalIgnoreCase) || c.Text.TrimStart('﻿').StartsWith("BEGIN:VCARD", StringComparison.OrdinalIgnoreCase);
        var cards = isVcf ? VCard.Read(c.Text) : ContactCsv.Read(c.Text);
        if (cards.Count == 0)
        {
            throw AppException.BadRequest("no_contacts_in_file");
        }

        var (created, merged, skipped) = (0, 0, 0);
        foreach (var card in cards.Take(SaveContactHandler.MaxContacts))
        {
            if (string.IsNullOrWhiteSpace(card.DisplayName) && card.Emails.Count == 0)
            {
                skipped++;
                continue;
            }

            var keys = card.Emails.Select(e => e.Value.Trim().ToLowerInvariant()).ToList();
            var existing = keys.Count == 0 ? null
                : await db.Contacts.FirstOrDefaultAsync(x => x.UserId == me.UserId && x.EmailKeys.Any(k => keys.Contains(k)), ct);
            if (existing is not null)
            {
                ContactMapping.Apply(existing, ContactMerge.Combine(ContactMapping.ToCard(existing), card));
                existing.IsAutoSaved = false;
                merged++;
            }
            else
            {
                existing = new Contact { TenantId = me.TenantId, UserId = me.UserId };
                ContactMapping.Apply(existing, card);
                db.Contacts.Add(existing);
                created++;
            }

            if (card.Photo is not null && existing.PhotoSha256 is null)
            {
                var stream = new MemoryStream(card.Photo, writable: false);
                var sha = await BlobStore.HashAsync(stream, ct);
                await BlobRefs.EnsureAsync(db, blobs, me.TenantId, sha, stream, ct);

                existing.PhotoSha256 = sha;
                existing.PhotoContentType = card.PhotoType;
            }

            await db.SaveChangesAsync(ct);
            if (c.GroupId is { } g && await db.ContactGroups.AnyAsync(x => x.Id == g && x.UserId == me.UserId, ct)
                                   && !await db.ContactGroupMembers.AnyAsync(m => m.GroupId == g && m.ContactId == existing.Id, ct))
            {
                db.ContactGroupMembers.Add(new ContactGroupMember { TenantId = me.TenantId, GroupId = g, ContactId = existing.Id });
                await db.SaveChangesAsync(ct);
            }
        }

        return new ImportContactsResult(created, merged, skipped);
    }
}

public static class ContactMerge
{
    /// <summary>Union of addresses and numbers; blank fields filled from the other card; notes kept from both.</summary>
    public static ContactCard Combine(ContactCard a, ContactCard b) => new()
    {
        DisplayName = string.IsNullOrWhiteSpace(a.DisplayName) || a.Emails.Any(e => e.Value == a.DisplayName) ? b.DisplayName : a.DisplayName,
        Emails = a.Emails.Concat(b.Emails).DistinctBy(e => e.Value.Trim().ToLowerInvariant()).ToList(),
        Phones = a.Phones.Concat(b.Phones).DistinctBy(p => new string(p.Value.Where(char.IsAsciiDigit).ToArray())).ToList(),
        Org = a.Org ?? b.Org,
        Title = a.Title ?? b.Title,
        Notes = a.Notes is null ? b.Notes : b.Notes is null || a.Notes.Contains(b.Notes, StringComparison.Ordinal) ? a.Notes : a.Notes + "\n\n" + b.Notes,
    };
}

public sealed record ExportContactsQuery(string Format, Guid? GroupId) : IRequest<(byte[] Content, string ContentType, string FileName)>;

public sealed class ExportContactsHandler(BlueMailDbContext db, ICurrentUser me, BlobStore blobs) : IRequestHandler<ExportContactsQuery, (byte[] Content, string ContentType, string FileName)>
{
    public async Task<(byte[] Content, string ContentType, string FileName)> Handle(ExportContactsQuery q, CancellationToken ct)
    {
        var query = db.Contacts.AsNoTracking().Where(c => c.UserId == me.UserId && !c.IsAutoSaved);
        if (q.GroupId is { } g)
        {
            query = query.Where(c => db.ContactGroupMembers.Any(m => m.GroupId == g && m.ContactId == c.Id));
        }

        var rows = await query.OrderBy(c => c.DisplayName).ToListAsync(ct);
        if (q.Format == "csv")
        {
            return (Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(ContactCsv.Write(rows.Select(ContactMapping.ToCard)))).ToArray(), "text/csv", "contacts.csv");
        }

        var cards = new List<ContactCard>();
        foreach (var r in rows)
        {
            var card = ContactMapping.ToCard(r);
            if (r.PhotoSha256 is not null)
            {
                var ms = new MemoryStream();
                await blobs.ReadAsync(me.TenantId, r.PhotoSha256, ms, ct: ct);
                card = card with { Photo = ms.ToArray(), PhotoType = r.PhotoContentType };
            }

            cards.Add(card);
        }

        return (Encoding.UTF8.GetBytes(VCard.Write(cards)), "text/vcard", "contacts.vcf");
    }
}

public sealed record MergeDuplicatesCommand : IRequest<int>;

/// <summary>Contacts sharing an address become one (the oldest keeps its id, groups are carried over).</summary>
public sealed class MergeDuplicatesHandler(BlueMailDbContext db, ICurrentUser me) : IRequestHandler<MergeDuplicatesCommand, int>
{
    public async Task<int> Handle(MergeDuplicatesCommand c, CancellationToken ct)
    {
        var all = await db.Contacts.Where(x => x.UserId == me.UserId).OrderBy(x => x.CreatedAt).ToListAsync(ct);
        var removed = 0;
        var owner = new Dictionary<string, Contact>(StringComparer.Ordinal);
        foreach (var contact in all)
        {
            var keep = contact.EmailKeys.Select(k => owner.GetValueOrDefault(k)).FirstOrDefault(k => k is not null && k.DeletedAt == null);
            if (keep is null)
            {
                foreach (var k in contact.EmailKeys)
                {
                    owner[k] = contact;
                }

                continue;
            }

            ContactMapping.Apply(keep, ContactMerge.Combine(ContactMapping.ToCard(keep), ContactMapping.ToCard(contact)));
            keep.IsAutoSaved &= contact.IsAutoSaved;
            keep.ContactCount += contact.ContactCount;
            keep.LastContactedAt = new[] { keep.LastContactedAt, contact.LastContactedAt }.Max();
            keep.PhotoSha256 ??= contact.PhotoSha256;
            keep.PhotoContentType ??= contact.PhotoContentType;
            foreach (var k in keep.EmailKeys)
            {
                owner[k] = keep;
            }

            var groups = await db.ContactGroupMembers.Where(m => m.ContactId == contact.Id).Select(m => m.GroupId).ToListAsync(ct);
            var already = await db.ContactGroupMembers.Where(m => m.ContactId == keep.Id).Select(m => m.GroupId).ToListAsync(ct);
            db.ContactGroupMembers.AddRange(groups.Except(already).Select(g => new ContactGroupMember { TenantId = me.TenantId, GroupId = g, ContactId = keep.Id }));
            await db.ContactGroupMembers.Where(m => m.ContactId == contact.Id).ExecuteDeleteAsync(ct);
            contact.DeletedAt = DateTimeOffset.UtcNow;
            removed++;
        }

        await db.SaveChangesAsync(ct);
        return removed;
    }
}

// ---------- organisation directory ----------

public sealed record DirectoryEntryDto(string Kind, Guid Id, string Name, string Email, string? OrgUnit);

public sealed record ListDirectoryQuery(string? Q) : IRequest<IReadOnlyList<DirectoryEntryDto>>;

/// <summary>People, groups and shared mailboxes of the organisation; administrators can hide people (PROMPT 6.10).</summary>
public sealed class ListDirectoryHandler(BlueMailDbContext db) : IRequestHandler<ListDirectoryQuery, IReadOnlyList<DirectoryEntryDto>>
{
    public async Task<IReadOnlyList<DirectoryEntryDto>> Handle(ListDirectoryQuery q, CancellationToken ct)
    {
        var hidden = db.DirectoryOverrides.Where(o => o.HideFromDirectory).Select(o => o.UserId);
        var term = q.Q?.Trim() ?? "";
        var like = "%" + term.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
        var users = await db.Users.AsNoTracking().Where(u => u.DeletedAt == null && u.Status == UserStatus.Active && !hidden.Contains(u.Id)
                                                             && (term == "" || EF.Functions.ILike(u.PrimaryEmail, like) ||
                                                                 EF.Functions.ILike(EF.Functions.Unaccent(u.DisplayName), EF.Functions.Unaccent(like))))
            .OrderBy(u => u.DisplayName).Take(500)
            .Select(u => new DirectoryEntryDto("USER", u.Id, u.DisplayName, u.PrimaryEmail,
                db.OrgUnits.Where(o => o.Id == u.OrgUnitId).Select(o => o.Name).FirstOrDefault()))
            .ToListAsync(ct);
        var groups = await db.Groups.AsNoTracking().Where(g => g.DeletedAt == null && (term == "" || EF.Functions.ILike(g.Name, like) || EF.Functions.ILike(g.Email, like)))
            .OrderBy(g => g.Name).Take(200).Select(g => new DirectoryEntryDto("GROUP", g.Id, g.Name, g.Email, null)).ToListAsync(ct);
        var shared = await db.SharedMailboxes.AsNoTracking().Where(s => s.DeletedAt == null && (term == "" || EF.Functions.ILike(s.Name, like) || EF.Functions.ILike(s.Email, like)))
            .OrderBy(s => s.Name).Take(200).Select(s => new DirectoryEntryDto("SHARED", s.Id, s.Name, s.Email, null)).ToListAsync(ct);
        return [.. users, .. groups, .. shared];
    }
}

public sealed record SetDirectoryVisibilityCommand(Guid UserId, bool Hidden) : IRequest<Unit>;

public sealed class SetDirectoryVisibilityHandler(BlueMailDbContext db, ICurrentUser me) : IRequestHandler<SetDirectoryVisibilityCommand, Unit>
{
    public async Task<Unit> Handle(SetDirectoryVisibilityCommand c, CancellationToken ct)
    {
        _ = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == c.UserId, ct) ?? throw AppException.NotFound("user_not_found");
        var row = await db.DirectoryOverrides.FirstOrDefaultAsync(o => o.UserId == c.UserId, ct);
        if (row is null)
        {
            db.DirectoryOverrides.Add(new DirectoryOverride { TenantId = me.TenantId, UserId = c.UserId, HideFromDirectory = c.Hidden });
        }
        else
        {
            row.HideFromDirectory = c.Hidden;
        }

        await db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}

// ---------- auto-save ----------

/// <summary>People a user writes to are saved to "Other contacts" and counted (PROMPT 6.10): the count drives "important".</summary>
public sealed class ContactRecorder(BlueMailDbContext db)
{
    public async Task RecordSentAsync(Guid tenant, Guid userId, IEnumerable<BlueMail.Mime.MailboxAddress> recipients, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var r in recipients.DistinctBy(x => x.Address.ToLowerInvariant()).Take(100))
        {
            var key = r.Address.ToLowerInvariant();
            var updated = await db.Contacts.IgnoreQueryFilters()
                .Where(c => c.TenantId == tenant && c.UserId == userId && c.DeletedAt == null && c.EmailKeys.Contains(key))
                .ExecuteUpdateAsync(c => c.SetProperty(x => x.ContactCount, x => x.ContactCount + 1).SetProperty(x => x.LastContactedAt, now), ct);
            if (updated == 0)
            {
                var c = new Contact { TenantId = tenant, UserId = userId, IsAutoSaved = true, ContactCount = 1, LastContactedAt = now };
                ContactMapping.Apply(c, new ContactCard { DisplayName = string.IsNullOrWhiteSpace(r.Name) ? r.Address : r.Name!, Emails = [new LabeledValue(r.Address, null)] });
                db.Contacts.Add(c);
                await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();
            }
        }
    }
}
