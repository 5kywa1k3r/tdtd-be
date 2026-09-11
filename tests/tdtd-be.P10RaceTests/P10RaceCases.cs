internal static class P10RaceCases
{
    internal static IReadOnlyList<P10RaceCaseDefinition> All { get; } =
        P10CasCases.Definitions
            .Concat(P10CrashCases.Definitions)
            .Concat(P10SecurityCases.Definitions)
            .Concat(P10CleanCases.Definitions)
            .ToArray();
}
