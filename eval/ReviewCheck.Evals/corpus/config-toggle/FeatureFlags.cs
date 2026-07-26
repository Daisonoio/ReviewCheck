namespace Sample.Config;

/// <summary>Static feature switches — a small, single-block config change.</summary>
public static class FeatureFlags
{
    /// <summary>When true, the new guided-checkout path is used instead of the legacy one.</summary>
    public const bool GuidedCheckoutEnabled = true;

    /// <summary>Maximum number of items a single basket may hold.</summary>
    public const int MaxBasketItems = 100;
}
