using System;
using System.Diagnostics.CodeAnalysis;

namespace DSharpPlusPlus.Net;

internal sealed class RetryableRatelimitException : Exception
{
    public required TimeSpan ResetAfter { get; set; }

    [SetsRequiredMembers]
    public RetryableRatelimitException(TimeSpan resetAfter) 
        => this.ResetAfter = resetAfter;
}
