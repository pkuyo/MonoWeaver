namespace MonoWeaver.ILTests;

internal static class ILCaseData
{
    public static LoadedILCases Value => LazyValue.Value;

    private static readonly Lazy<LoadedILCases> LazyValue = new(Load);

    private static LoadedILCases Load()
    {
        var assemblyPaths = TestAssetBuilder.EnsureILTestsBuilt();
        var loader = new ILTestDataLoader(TestAssetBuilder.ILTestsDirectory);
        return new LoadedILCases
        {
            Loader = loader,
            CompiledAssemblyPaths = assemblyPaths,
            MethodCases = loader.LoadMethodCases(),
        };
    }
}

internal sealed class LoadedILCases
{
    public required ILTestDataLoader Loader { get; init; }
    public required IReadOnlyList<string> CompiledAssemblyPaths { get; init; }
    public required IReadOnlyList<MethodTestCase> MethodCases { get; init; }
}
