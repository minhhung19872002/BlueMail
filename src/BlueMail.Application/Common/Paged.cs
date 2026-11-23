namespace BlueMail.Application.Common;

/// <summary>Page of an admin list (PROMPT 5). Lists are always ordered with a unique key last.</summary>
public sealed record Paged<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

public static class Paging
{
    public const int MaxPageSize = 200;

    public static (int Page, int Size) Clamp(int page, int pageSize) =>
        (Math.Max(1, page), Math.Clamp(pageSize, 1, MaxPageSize));
}
