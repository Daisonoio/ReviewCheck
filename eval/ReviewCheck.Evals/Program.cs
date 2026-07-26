using ReviewCheck.Evals;
using ReviewCheck.Llm;
using ReviewCheck.Pipeline;

// The eval RUNNER (docs: eval/README.md). It runs every capability check over the corpus, prints a
// scorecard, writes a JSON report, and exits non-zero on any regression — so CI can gate on it.
//
// Deterministic and offline by construction: the structural tier uses the real pipeline + the facts
// narrator (no key), the LLM-behaviour tier uses a scripted model. No network, no randomness.

var corpusDir = Path.Combine(AppContext.BaseDirectory, "corpus");
var cases = EvalCorpus.Load(corpusDir);

var pipeline = new AnalysisPipeline();
var facts = new FactsNarrator();
var results = new List<CheckResult>();

foreach (var @case in cases)
{
    var analyzed = pipeline.Run(@case.Diff);
    var blocks = await facts.NarrateAsync(analyzed.Blocks);

    // Tier A — structural guarantees over the real pipeline output.
    results.AddRange(Capabilities.Structural(@case, blocks, analyzed));

    // Tier B — LLM-behaviour, driven on one representative block of the richest case.
    if (@case.Name == "discount-pricing")
    {
        var representative = analyzed.Blocks.FirstOrDefault(b => b.Title.Contains("ApplyDiscount"))
                             ?? analyzed.Blocks[0];
        results.AddRange(await Capabilities.LlmBehaviour(@case.Name, representative));
    }
}

var scorecard = Scorecard.From(results);
Console.WriteLine(scorecard.Render());

var reportPath = Path.Combine(Directory.GetCurrentDirectory(), "eval-report.json");
File.WriteAllText(reportPath, scorecard.ToJson());
Console.WriteLine($"Report written to {reportPath}");

return scorecard.AllOk ? 0 : 1;
