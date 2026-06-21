using System.Reflection;
using Mono.Cecil;

namespace MonoWeaver.ILTests;

internal sealed class ILTestDataLoader : IDisposable
{
    private const string SpecialTestPrefix = "special.";

    private readonly string _testsDirectory;
    private readonly DefaultAssemblyResolver _resolver;
    private readonly ReaderParameters _readerParameters;
    private readonly List<ModuleDefinition> _openedModules = new();

    public ILTestDataLoader(string testsDirectory)
    {
        _testsDirectory = Path.GetFullPath(testsDirectory);
        _resolver = new DefaultAssemblyResolver();

        AddSearchDirectoryIfExists(_testsDirectory);
        AddSearchDirectoryIfExists(AppContext.BaseDirectory);
        AddSearchDirectoryIfExists(Path.GetDirectoryName(typeof(object).Assembly.Location));

        TryAddAssemblyDirectory("System.Runtime");
        TryAddAssemblyDirectory("System.Console");
        TryAddAssemblyDirectory("System.Private.CoreLib");
        TryAddAssemblyDirectory("netstandard");

        _readerParameters = new ReaderParameters
        {
            AssemblyResolver = _resolver,
            ReadSymbols = false,
            InMemory = true,
        };
    }

    public IReadOnlyList<MethodTestCase> LoadMethodCases()
    {
        var modules = OpenAllTestModules();
        var result = new List<MethodTestCase>();

        foreach (var module in modules)
        {
            var assemblyPath = module.FileName ?? module.Name;
            var assemblyName = Path.GetFileNameWithoutExtension(assemblyPath);

            foreach (var method in EnumerateMethods(module.Types))
            {
                if (!method.HasBody)
                    continue;

                if (!TryParseMethodCase(method, out var friendlyName, out var scope, out var kind, out var expectedErrors))
                    continue;

                var targetMethod = ResolveSpecialTestMethod(method, ref friendlyName);
                result.Add(new MethodTestCase
                {
                    AssemblyPath = assemblyPath,
                    AssemblyName = assemblyName,
                    FriendlyName = friendlyName,
                    MethodName = targetMethod.FullName,
                    Method = targetMethod,
                    Scope = scope,
                    ExpectedKind = kind,
                    ExpectedVerifierErrors = expectedErrors,
                });
            }
        }

        return result;
    }

    private IReadOnlyList<ModuleDefinition> OpenAllTestModules()
    {
        if (_openedModules.Count != 0)
            return _openedModules;

        if (!Directory.Exists(_testsDirectory))
            throw new DirectoryNotFoundException($"Tests directory does not exist: {_testsDirectory}");

        var dlls = Directory.GetFiles(_testsDirectory, "*.dll")
            .OrderBy(static p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (dlls.Length == 0)
        {
            throw new InvalidOperationException(
                $"No compiled IL test assemblies found in '{_testsDirectory}'. " +
                "Compile ILVerification/ILTests/*.il first, for example with build-iltests.sh and ilasm.");
        }

        foreach (var dll in dlls)
            _openedModules.Add(ModuleDefinition.ReadModule(dll, _readerParameters));

        return _openedModules;
    }

    private static IEnumerable<TypeDefinition> EnumerateTypes(IEnumerable<TypeDefinition> types)
    {
        foreach (var type in types)
        {
            yield return type;

            foreach (var nested in EnumerateTypes(type.NestedTypes))
                yield return nested;
        }
    }

    private static IEnumerable<MethodDefinition> EnumerateMethods(IEnumerable<TypeDefinition> types)
    {
        foreach (var type in EnumerateTypes(types))
        {
            foreach (var method in type.Methods)
                yield return method;
        }
    }

    private static bool TryParseMethodCase(
        MethodDefinition method,
        out string friendlyName,
        out MethodTestScope scope,
        out ExpectedKind kind,
        out IReadOnlyList<string> expectedErrors)
    {
        friendlyName = string.Empty;
        scope = MethodTestScope.Both;
        kind = ExpectedKind.Valid;
        expectedErrors = Array.Empty<string>();

        var methodName = method.Name;
        if (!methodName.Contains('_', StringComparison.Ordinal))
            return false;

        var index = LastIndexOfCaseMarker(methodName, "_Valid");
        if (index < 0)
            index = LastIndexOfCaseMarker(methodName, "_Invalid");
        if (index < 0)
            index = LastIndexOfCaseMarker(methodName, "_Warning");
        if (index < 0)
            return false;

        friendlyName = methodName[..index];
        var suffixParts = methodName[(index + 1)..].Split('_');
        if (friendlyName.EndsWith("_Full", StringComparison.Ordinal))
        {
            scope = MethodTestScope.Full;
            friendlyName = friendlyName[..^"_Full".Length];
        }

        if (suffixParts.Length == 1 && suffixParts[0] == "Valid")
        {
            kind = ExpectedKind.Valid;
            return true;
        }

        if (suffixParts.Length >= 2 && suffixParts[0] == "Invalid")
        {
            kind = ExpectedKind.Invalid;
            expectedErrors = string.Join('_', suffixParts.Skip(1))
                .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return true;
        }

        if (suffixParts.Length >= 1 && suffixParts[0] == "Warning")
        {
            kind = ExpectedKind.Warning;
            expectedErrors = suffixParts.Length >= 2
                ? string.Join('_', suffixParts.Skip(1))
                    .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : Array.Empty<string>();
            return true;
        }

        return false;
    }

    private static int LastIndexOfCaseMarker(string methodName, string marker)
    {
        var index = methodName.LastIndexOf(marker, StringComparison.Ordinal);
        while (index >= 0)
        {
            var next = index + marker.Length;
            if (next == methodName.Length || methodName[next] == '_')
                return index;

            index = index == 0
                ? -1
                : methodName.LastIndexOf(marker, index - 1, StringComparison.Ordinal);
        }

        return -1;
    }

    private static MethodDefinition ResolveSpecialTestMethod(MethodDefinition markerMethod, ref string friendlyName)
    {
        if (!friendlyName.StartsWith(SpecialTestPrefix, StringComparison.Ordinal))
            return markerMethod;

        var specialParams = friendlyName[SpecialTestPrefix.Length..];
        var delimiter = specialParams.IndexOf('.', StringComparison.Ordinal);
        if (delimiter < 0)
            return markerMethod;

        var visibleFriendlyName = specialParams[..delimiter];
        var specialMethodName = specialParams[(delimiter + 1)..];
        friendlyName = visibleFriendlyName;

        var target = markerMethod.DeclaringType.Methods.FirstOrDefault(candidate =>
            candidate.Name == specialMethodName && SameSignature(candidate, markerMethod));

        return target ?? markerMethod;
    }

    private static bool SameSignature(MethodDefinition left, MethodDefinition right)
    {
        if (left.Parameters.Count != right.Parameters.Count)
            return false;

        if (left.GenericParameters.Count != right.GenericParameters.Count)
            return false;

        if (left.ReturnType.FullName != right.ReturnType.FullName)
            return false;

        for (var i = 0; i < left.Parameters.Count; i++)
        {
            if (left.Parameters[i].ParameterType.FullName != right.Parameters[i].ParameterType.FullName)
                return false;
        }

        return true;
    }

    private void TryAddAssemblyDirectory(string assemblySimpleName)
    {
        try
        {
            var asm = Assembly.Load(new AssemblyName(assemblySimpleName));
            AddSearchDirectoryIfExists(Path.GetDirectoryName(asm.Location));
        }
        catch
        {
            // Some runtime assemblies are not loadable by name in all hosts. The resolver can still work
            // with the directories already added above.
        }
    }

    private void AddSearchDirectoryIfExists(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            _resolver.AddSearchDirectory(path);
    }

    public void Dispose()
    {
        foreach (var module in _openedModules)
            module.Dispose();
    }
}
