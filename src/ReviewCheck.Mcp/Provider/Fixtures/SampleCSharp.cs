using ReviewCheck.Core;

namespace ReviewCheck.Mcp.Provider.Fixtures;

/// <summary>
/// The MVP-1 fixture (docs/23 §3): one realistic analyzed review — a token-bucket
/// rate limiter — whose blocks all pass <see cref="BlockGuard"/>. It deliberately covers
/// what the R1–R10 recovery commands need: a non-null uncertainty (R3), populated
/// related_block_ids (R4/R5), citations in both "12" and "12-15" formats, seams
/// referencing real block ids, and varied intents.
/// </summary>
public static class SampleCSharp
{
    public static AnalyzedReview Review { get; } = new(
        Title: "Rate limiting per API key (token bucket)",
        Blocks:
        [
            new Block(
                Id: "b1",
                Title: "TokenBucket class",
                Intent: Intent.Definition,
                Code: """
                    public sealed class TokenBucket
                    {
                        private readonly int _capacity;
                        private readonly double _refillPerSecond;
                        private readonly ConcurrentDictionary<string, Bucket> _buckets = new();

                        public bool Allow(string apiKey)
                        {
                            var bucket = _buckets.GetOrAdd(apiKey, _ => new Bucket(_capacity));
                            return bucket.TryTake(_refillPerSecond);
                        }
                    }
                    """,
                Explanation: new Explanation(
                    What: "Declares the token-bucket limiter: one bucket per API key, taken from a concurrent dictionary.",
                    Why: "This is the core of the change — every request is allowed or rejected here.",
                    Link: "Instantiated with the parameters of b2; called by the middleware in b3; behavior pinned by the test in b4.",
                    Citations:
                    [
                        new Citation("RateLimiting/TokenBucket.cs", "1-12"),
                        new Citation("RateLimiting/TokenBucket.cs", "8"),
                    ],
                    Uncertainty: "Bucket.TryTake is not in this diff — I cannot verify the refill arithmetic is monotonic."),
                RelatedBlockIds: ["b2", "b3", "b4"],
                EstimatedMinutes: 4),

            new Block(
                Id: "b2",
                Title: "RateLimitOptions binding",
                Intent: Intent.Config,
                Code: """
                    public sealed class RateLimitOptions
                    {
                        public int Capacity { get; init; } = 100;
                        public double RefillPerSecond { get; init; } = 10.0;
                    }

                    // appsettings.json: { "RateLimit": { "Capacity": 100, "RefillPerSecond": 10 } }
                    """,
                Explanation: new Explanation(
                    What: "Defines the two tunables (capacity, refill rate) and binds them to the RateLimit config section.",
                    Why: "Keeps limits changeable per environment without recompiling.",
                    Link: "Consumed by b1's constructor via the wiring in b3.",
                    Citations: [new Citation("RateLimiting/RateLimitOptions.cs", "1-8")]),
                RelatedBlockIds: ["b1", "b3"],
                EstimatedMinutes: 2),

            new Block(
                Id: "b3",
                Title: "Middleware wiring",
                Intent: Intent.Wiring,
                Code: """
                    services.Configure<RateLimitOptions>(config.GetSection("RateLimit"));
                    services.AddSingleton<TokenBucket>();
                    app.UseMiddleware<RateLimitMiddleware>();
                    """,
                Explanation: new Explanation(
                    What: "Registers the bucket as a singleton and inserts the middleware into the request pipeline.",
                    Why: "A singleton is required: per-request buckets would never accumulate state and the limiter would be a no-op.",
                    Link: "Connects b2 (options) to b1 (bucket); the middleware order relative to auth matters — see the seam below.",
                    Citations: [new Citation("Program.cs", "24-26")],
                    Uncertainty: "UseMiddleware is called before UseAuthentication — unauthenticated requests also consume tokens. Intended?"),
                RelatedBlockIds: ["b1", "b2"],
                EstimatedMinutes: 3),

            new Block(
                Id: "b4",
                Title: "Limiter behavior test",
                Intent: Intent.Test,
                Code: """
                    [Fact]
                    public void Rejects_after_capacity_is_exhausted()
                    {
                        var bucket = new TokenBucket(capacity: 2, refillPerSecond: 0);
                        Assert.True(bucket.Allow("k"));
                        Assert.True(bucket.Allow("k"));
                        Assert.False(bucket.Allow("k"));
                    }
                    """,
                Explanation: new Explanation(
                    What: "Pins the contract of b1: with capacity 2 and no refill, the third call is rejected.",
                    Why: "Guards the only observable behavior of the limiter against future refactors.",
                    Link: "Exercises b1 directly, bypassing the wiring in b3.",
                    Citations: [new Citation("RateLimiting.Tests/TokenBucketTests.cs", "5-13")]),
                RelatedBlockIds: ["b1"],
                EstimatedMinutes: 2),
        ],
        InteractionPoints:
        [
            new InteractionPoint(
                Text: "Middleware order: the limiter (b3) runs before authentication, so unauthenticated traffic drains the buckets of b1.",
                BlockIds: ["b1", "b3"]),
            new InteractionPoint(
                Text: "Defaults in b2 (capacity 100) differ from the values the test in b4 exercises (capacity 2) — the test never covers the shipped configuration.",
                BlockIds: ["b2", "b4"]),
        ],
        EstimatedMinutes: 11);
}
