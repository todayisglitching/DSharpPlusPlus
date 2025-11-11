using System;

namespace DSharpPlusPlus.Commands.ContextChecks;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Delegate)]
public class RequireApplicationOwnerAttribute : ContextCheckAttribute;
