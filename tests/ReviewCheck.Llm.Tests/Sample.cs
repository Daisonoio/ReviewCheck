using ReviewCheck.Core;
using ReviewCheck.Pipeline;

namespace ReviewCheck.Llm.Tests;

/// <summary>One realistic StructuralBlock, shared by the rubric/prompt/adapter tests.</summary>
internal static class Sample
{
    public static StructuralBlock Block(string? uncertainty = null) => new(
        Id: "b1",
        Title: "Method Greeter.Hello — added",
        Intent: Intent.Definition,
        Code: "public string Hello(string name) => $\"Hello {name}\";",
        Citations: [new Citation("src/Greeter.cs", "5")],
        RelatedBlockIds: ["b2"],
        StructuralFacts: ["defines 'Greeter.Hello' in src/Greeter.cs (lines 3-6)", "used by 'Program.cs — top-level changes'"],
        UncertaintyStructural: uncertainty,
        EstimatedMinutes: 1);

    public const string ValidJson =
        """
        {"what": "Adds a Hello method on Greeter that formats a greeting for the given name.",
         "why": "It provides the greeting used by the program entry point.",
         "link": "Used by 'Program.cs — top-level changes'.",
         "uncertainty_semantic": null}
        """;
}
