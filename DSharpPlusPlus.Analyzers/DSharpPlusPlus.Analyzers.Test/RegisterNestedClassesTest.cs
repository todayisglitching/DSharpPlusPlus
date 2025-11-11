using System.Threading.Tasks;
using DSharpPlusPlus.Analyzers.Commands;
using DSharpPlusPlus.Commands;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using NUnit.Framework;
using Verifier = Microsoft.CodeAnalysis.CSharp.Testing.CSharpAnalyzerVerifier<
    DSharpPlusPlus.Analyzers.Commands.RegisterNestedClassesAnalyzer,
    Microsoft.CodeAnalysis.Testing.DefaultVerifier
>;

namespace DSharpPlusPlus.Analyzers.Test;

public static class RegisterNestedClassesTest
{
    [Test]
    public static async Task TestNormalScenarioAsync()
    {
        CSharpAnalyzerTest<RegisterNestedClassesAnalyzer, DefaultVerifier> test
            = Utility.CreateAnalyzerTest<RegisterNestedClassesAnalyzer>();
        test.TestState.AdditionalReferences.Add(typeof(CommandContext).Assembly);

        test.TestCode = """
                        using System.Threading.Tasks;
                        using DSharpPlusPlus.Commands;

                        public class Test 
                        {
                            public static void Register(CommandsExtension extension) 
                            {
                                extension.AddCommands([typeof(ACommands.BCommands), typeof(ACommands)]);
                            }
                        }

                        [Command("a")]
                        public class ACommands
                        {
                            [Command("b")]
                            public class BCommands 
                            {
                                [Command("c")]
                                public static async ValueTask CAsync(CommandContext context) 
                                {
                                    await context.RespondAsync("C");
                                }
                            } 
                        }
                        """;

        test.ExpectedDiagnostics.Add
        (
            Verifier.Diagnostic()
                .WithLocation(8, 39)
                .WithSeverity(DiagnosticSeverity.Warning)
                .WithMessage("Don't register 'BCommands', register 'ACommands' instead")
        );

        await test.RunAsync();
    }

    [Test]
    public static async Task TestListScenarioAsync()
    {
        CSharpAnalyzerTest<RegisterNestedClassesAnalyzer, DefaultVerifier> test
            = Utility.CreateAnalyzerTest<RegisterNestedClassesAnalyzer>();
        test.TestState.AdditionalReferences.Add(typeof(CommandContext).Assembly);

        test.TestCode = """
                        using System;
                        using System.Threading.Tasks;
                        using System.Collections.Generic;
                        using DSharpPlusPlus.Commands;

                        public class Test 
                        {
                            public static void Register(CommandsExtension extension) 
                            {
                                List<Type> types = new() { typeof(ACommands.BCommands), typeof(ACommands) }; 
                                extension.AddCommands(types);
                            }
                        }

                        [Command("a")]
                        public class ACommands
                        {
                            [Command("b")]
                            public class BCommands 
                            {
                                [Command("c")]
                                public static async ValueTask CAsync(CommandContext context) 
                                {
                                    await context.RespondAsync("C");
                                }
                            } 
                        }
                        """;

        test.ExpectedDiagnostics.Add
        (
            Verifier.Diagnostic()
                .WithLocation(10, 43)
                .WithSeverity(DiagnosticSeverity.Warning)
                .WithMessage("Don't register 'BCommands', register 'ACommands' instead")
        );

        await test.RunAsync();
    }

    [Test]
    public static async Task TestArrayScenarioAsync()
    {
        CSharpAnalyzerTest<RegisterNestedClassesAnalyzer, DefaultVerifier> test
            = Utility.CreateAnalyzerTest<RegisterNestedClassesAnalyzer>();
        test.TestState.AdditionalReferences.Add(typeof(CommandContext).Assembly);

        test.TestCode = """
                        using System;
                        using System.Threading.Tasks;
                        using DSharpPlusPlus.Commands;

                        public class Test 
                        {
                            public static void Register(CommandsExtension extension) 
                            {
                                Type[] types = new[] { typeof(ACommands.BCommands), typeof(ACommands) }; 
                                extension.AddCommands(types);
                            }
                        }

                        [Command("a")]
                        public class ACommands
                        {
                            [Command("b")]
                            public class BCommands 
                            {
                                [Command("c")]
                                public static async ValueTask CAsync(CommandContext context) 
                                {
                                    await context.RespondAsync("C");
                                }
                            } 
                        }
                        """;

        test.ExpectedDiagnostics.Add
        (
            Verifier.Diagnostic()
                .WithLocation(9, 39)
                .WithSeverity(DiagnosticSeverity.Warning)
                .WithMessage("Don't register 'BCommands', register 'ACommands' instead")
        );

        await test.RunAsync();
    }
}
