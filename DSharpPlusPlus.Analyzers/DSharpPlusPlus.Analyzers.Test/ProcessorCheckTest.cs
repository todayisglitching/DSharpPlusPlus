using System.Threading.Tasks;
using DSharpPlusPlus.Analyzers.Commands;
using DSharpPlusPlus.Commands;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using NUnit.Framework;
using Verifier = Microsoft.CodeAnalysis.CSharp.Testing.CSharpAnalyzerVerifier<
    DSharpPlusPlus.Analyzers.Commands.ProcessorCheckAnalyzer,
    Microsoft.CodeAnalysis.Testing.DefaultVerifier
>;

namespace DSharpPlusPlus.Analyzers.Test;

public static class ProcessorCheckTest
{
    [Test]
    public static async Task DiagnosticTestAsync()
    {
        CSharpAnalyzerTest<ProcessorCheckAnalyzer, DefaultVerifier> test
            = Utility.CreateAnalyzerTest<ProcessorCheckAnalyzer>();
        test.TestState.AdditionalReferences.Add(typeof(CommandContext).Assembly);

        test.TestCode = """
                        using System.Threading.Tasks;
                        using DSharpPlusPlus.Commands.Trees.Metadata;
                        using DSharpPlusPlus.Commands.Processors.TextCommands;
                        using DSharpPlusPlus.Commands.Processors.SlashCommands;

                        public class Test 
                        {
                            [AllowedProcessors<SlashCommandProcessor>()]
                            public async Task Tester(TextCommandContext context)
                            {
                                await context.RespondAsync("Tester!");
                            }
                        }
                        """;

        test.ExpectedDiagnostics.Add
        (
            Verifier.Diagnostic()
                .WithLocation(9, 30)
                .WithSeverity(DiagnosticSeverity.Error)
                .WithMessage("All provided processors does not support context 'TextCommandContext'")
        );

        await test.RunAsync();
    }
}
