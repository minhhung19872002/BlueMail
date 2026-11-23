using BlueMail.Application.Common;
using BlueMail.MailStore;
using MediatR;

namespace BlueMail.Application.Features.Mail;

public sealed record GetLabelsQuery(Guid? MailboxId) : IRequest<LabelsDto>;

public sealed record LabelsDto(Guid MailboxId, long UsedBytes, long QuotaBytes, IReadOnlyList<LabelSummary> Labels);

public sealed class GetLabelsHandler(IMailStore store, ICurrentUser me, Infrastructure.Persistence.BlueMailDbContext db)
    : IRequestHandler<GetLabelsQuery, LabelsDto>
{
    public async Task<LabelsDto> Handle(GetLabelsQuery request, CancellationToken ct)
    {
        MailboxAccess access;
        try
        {
            access = await store.OpenAsync(me.TenantId, me.UserId, request.MailboxId, ct);
        }
        catch (MailboxAccessDeniedException)
        {
            // Same answer for "does not exist" and "not yours": no probing of other tenants' ids.
            throw AppException.NotFound("mailbox_not_found");
        }

        var labels = await store.GetLabelsAsync(access, ct);
        var used = await store.GetUsedBytesAsync(access, ct);
        var quota = db.Users.Where(u => u.Id == me.UserId).Select(u => u.QuotaBytes).FirstOrDefault();
        return new LabelsDto(access.MailboxId, used, quota, labels);
    }
}
