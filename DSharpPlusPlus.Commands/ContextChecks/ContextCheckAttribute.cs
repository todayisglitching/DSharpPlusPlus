using System;

namespace DSharpPlusPlus.Commands.ContextChecks;

[AttributeUsage(AttributeTargets.All, Inherited = false, AllowMultiple = true)]
public abstract class ContextCheckAttribute : Attribute;
