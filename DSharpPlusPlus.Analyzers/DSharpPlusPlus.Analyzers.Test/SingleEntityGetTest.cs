using System.Threading.Tasks;
using DSharpPlusPlus.Analyzers.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using NUnit.Framework;
using Verifier = Microsoft.CodeAnalysis.CSharp.Testing.CSharpAnalyzerVerifier<
    DSharpPlusPlus.Analyzers.Core.SingleEntityGetRequestAnalyzer,
    Microsoft.CodeAnalysis.Testing.DefaultVerifier
>;

namespace DSharpPlusPlus.Analyzers.Test;

/// <summary>
/// 
/// </summary>
public static class SingleEntityGetTest
{
    [Test]
    public static async Task DiagnosticTestAsync()
    {
        CSharpAnalyzerTest<SingleEntityGetRequestAnalyzer, DefaultVerifier> test =
            Utility.CreateAnalyzerTest<SingleEntityGetRequestAnalyzer>();

        test.TestCode = """
                        using System;
                        using System.Threading.Tasks;
                        using System.Collections.Generic;
                        using DSharpPlusPlus.Entities;

                        public class Test 
                        {
                            public async Task SomeLoopery(IEnumerable<ulong> ids, DiscordChannel channel)
                            {
                                foreach (ulong id in ids)  
                                {
                                    DiscordMessage message = await channel.GetMessageAsync(id);
                                    Console.WriteLine($"Author is: {message.Author.Username}");
                                }
                            }
                        }
                        """;

        test.ExpectedDiagnostics.Add
        (
            Verifier.Diagnostic()
                .WithLocation(12, 44)
                .WithSeverity(DiagnosticSeverity.Info)
                .WithMessage(
                    "Use 'channel.GetMessagesAsync()' outside of the loop instead of 'channel.GetMessageAsync(id)' inside the loop")
        );

        await test.RunAsync();
    }
}
