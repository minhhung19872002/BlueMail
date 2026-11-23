namespace BlueMail.Mime;

/// <summary>
/// Hard limits applied while parsing mail from the network (PROMPT 2.6.1, principle 7). Exceeding a limit
/// never throws: the parser records a <see cref="MimeDefect"/> and degrades (e.g. treats a too-deep
/// multipart as an opaque leaf), so a hostile message cannot make mail disappear or exhaust memory.
/// </summary>
public sealed record MimeLimits
{
    public static readonly MimeLimits Default = new();

    /// <summary>Total bytes of one header block (top level or part).</summary>
    public int MaxHeaderBlockBytes { get; init; } = 256 * 1024;

    /// <summary>Header fields per header block.</summary>
    public int MaxHeaderCount { get; init; } = 1000;

    /// <summary>Nesting depth of multipart / message/rfc822.</summary>
    public int MaxDepth { get; init; } = 50;

    /// <summary>Total MIME entities in one message.</summary>
    public int MaxParts { get; init; } = 10_000;

    /// <summary>A physical line longer than this is split (RFC 5322 allows 998; real mail exceeds it).</summary>
    public int MaxLineBytes { get; init; } = 64 * 1024;
}

public enum MimeDefectKind
{
    BareLineFeed,
    HeaderBlockTooLarge,
    TooManyHeaders,
    HeaderWithoutColon,
    MissingBoundaryParameter,
    MissingClosingBoundary,
    NoPartsFound,
    DepthLimitReached,
    PartLimitReached,
    LineTooLong,
    InvalidBase64,
    InvalidQuotedPrintable,
    UnknownCharset,
    UndeclaredEightBitHeader,
    MalformedEncodedWord,
    UnterminatedQuotedString,
    NulCharacter,
}

/// <summary>Something wrong with the message that the parser repaired or tolerated.</summary>
public sealed record MimeDefect(MimeDefectKind Kind, long Offset, string? Detail = null);
