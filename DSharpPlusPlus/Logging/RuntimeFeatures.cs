using System;

namespace DSharpPlusPlus.Logging;

/// <summary>
/// Contains runtime feature switches for trace logging.
/// </summary>
public static class RuntimeFeatures
{
    /// <summary>
    /// Specifies whether bot and webhook tokens are being anonymized. Defaults to true.
    /// </summary>
    public static bool AnonymizeTokens 
        => !AppContext.TryGetSwitch("DSharpPlusPlus.Trace.AnonymizeTokens", out bool value) || value;

    /// <summary>
    /// Specifies whether snowflake IDs and message contents are being anonymized. Defaults to false.
    /// </summary>
    /// <remarks>
    /// Note that enabling this switch may significantly reduce the quality of debugging data.
    /// </remarks>
    public static bool AnonymizeContents
        => AppContext.TryGetSwitch("DSharpPlusPlus.Trace.AnonymizeContents", out bool value) && value;

    /// <summary>
    /// Specifies whether rest requests should be logged. Defaults to true.
    /// </summary>
    public static bool EnableRestRequestLogging
        => !AppContext.TryGetSwitch("DSharpPlusPlus.Trace.EnableRestRequestLogging", out bool value) || value;

    /// <summary>
    /// Specifies whether inbound gateway payloads should be logged. Defaults to true.
    /// </summary>
    public static bool EnableInboundGatewayLogging
        => !AppContext.TryGetSwitch("DSharpPlusPlus.Trace.EnableInboundGatewayLogging", out bool value) || value;

    /// <summary>
    /// Specifies whether outbound gateway payloads should be logged. Defaults to true.
    /// </summary>
    public static bool EnableOutboundGatewayLogging
        => !AppContext.TryGetSwitch("DSharpPlusPlus.Trace.EnableOutboundGatewayLogging", out bool value) || value;
}
