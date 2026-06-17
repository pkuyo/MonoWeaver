using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace MonoWeaver.ILTests;

internal static class TestAssetBuilder
{
    private static readonly Lazy<IReadOnlyList<string>> CompiledILTests = new(BuildILTests);

    public static string ILTestsDirectory => Path.Combine(AppContext.BaseDirectory, "Tests");

    public static IReadOnlyList<string> EnsureILTestsBuilt()
        => CompiledILTests.Value;

    private static IReadOnlyList<string> BuildILTests()
    {
        var ilDirectory = Path.Combine(AppContext.BaseDirectory, "ILVerification", "ILTests");
        if (!Directory.Exists(ilDirectory))
            throw new DirectoryNotFoundException($"IL source directory was not copied to the test output: {ilDirectory}");

        var ilFiles = Directory.GetFiles(ilDirectory, "*.il")
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (ilFiles.Length == 0)
            throw new InvalidOperationException($"No .il files were found in {ilDirectory}");

        Directory.CreateDirectory(ILTestsDirectory);
        foreach (var staleDll in Directory.GetFiles(ILTestsDirectory, "*.dll"))
            File.Delete(staleDll);

        var ilasm = IlasmResolver.Resolve()
            ?? throw new InvalidOperationException(
                "Could not find ilasm. Set ILASM to the full ilasm.exe path, add ilasm.exe to PATH, " +
                "or restore a runtime.*.microsoft.netcore.ilasm package under the user NuGet package cache.");

        foreach (var ilFile in ilFiles)
        {
            var outputPath = Path.Combine(ILTestsDirectory, Path.GetFileNameWithoutExtension(ilFile) + ".dll");
            RunProcess(ilasm.Path, BuildILAsmArguments(outputPath, ilFile), ilDirectory, $"ilasm source: {ilFile}");
        }

        return Directory.GetFiles(ILTestsDirectory, "*.dll")
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> BuildILAsmArguments(string outputPath, string ilFile)
    {
        var result = new List<string> { "/dll", "/output=" + outputPath };
        result.AddRange(ReadProjectILAsmFlags(ilFile));

        var extraFlags = Environment.GetEnvironmentVariable("ILASM_FLAGS");
        if (!string.IsNullOrWhiteSpace(extraFlags))
            result.AddRange(SplitCommandLine(extraFlags));
        result.Add(ilFile);
        return result;
    }

    private static IEnumerable<string> ReadProjectILAsmFlags(string ilFile)
    {
        var projectPath = Path.ChangeExtension(ilFile, ".ilproj");
        if (!File.Exists(projectPath))
            yield break;

        var document = XDocument.Load(projectPath);
        foreach (var element in document.Descendants("IlasmFlags"))
        {
            var value = element.Value.Replace("$(IlasmFlags)", string.Empty, StringComparison.Ordinal);
            foreach (var flag in SplitCommandLine(value))
                yield return flag;
        }
    }

    private static void RunProcess(string fileName, IReadOnlyList<string> arguments, string workingDirectory, string description)
    {
        using var process = new Process();
        process.StartInfo.FileName = fileName;
        process.StartInfo.WorkingDirectory = workingDirectory;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        process.Start();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode == 0)
            return;

        throw new InvalidOperationException(string.Join(Environment.NewLine,
            $"{description} failed with exit code {process.ExitCode}.",
            "Command:",
            "  " + fileName + " " + string.Join(' ', arguments.Select(QuoteArgument)),
            "stdout:",
            string.IsNullOrWhiteSpace(stdout) ? "  <empty>" : stdout,
            "stderr:",
            string.IsNullOrWhiteSpace(stderr) ? "  <empty>" : stderr));
    }

    private static string? ResolveCommand(string command)
    {
        if (Path.IsPathFullyQualified(command))
            return File.Exists(command) ? command : null;

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var extensions = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : new[] { string.Empty };

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, command.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
                    ? command
                    : command + extension);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> SplitCommandLine(string value)
    {
        var current = new List<char>();
        var inQuotes = false;

        foreach (var ch in value)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (current.Count != 0)
                {
                    yield return new string(current.ToArray());
                    current.Clear();
                }
                continue;
            }

            current.Add(ch);
        }

        if (current.Count != 0)
            yield return new string(current.ToArray());
    }

    private static string QuoteArgument(string argument)
        => argument.Any(char.IsWhiteSpace) ? "\"" + argument.Replace("\"", "\\\"") + "\"" : argument;

    private sealed record IlasmInfo(string Path, string Source);

    private static class IlasmResolver
    {
        public static IlasmInfo? Resolve()
        {
            var fromEnvironment = Environment.GetEnvironmentVariable("ILASM");
            var resolved = ResolveExplicitOrPathCommand(fromEnvironment);
            if (resolved is not null)
                return new IlasmInfo(resolved, "ILASM");

            resolved = ResolveFromNuGetPackageCache();
            if (resolved is not null)
                return new IlasmInfo(resolved, "NuGet package cache");

            resolved = ResolveCommand("ilasm");
            if (resolved is not null)
                return new IlasmInfo(resolved, "PATH");

            resolved = ResolveFromWindowsFramework();
            return resolved is null ? null : new IlasmInfo(resolved, ".NET Framework directory");
        }

        private static string? ResolveExplicitOrPathCommand(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            if (File.Exists(value))
                return value;

            return ResolveCommand(value);
        }

        private static string? ResolveFromNuGetPackageCache()
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(userProfile))
                return null;

            var packagesRoot = Path.Combine(userProfile, ".nuget", "packages");
            if (!Directory.Exists(packagesRoot))
                return null;

            var packageDirectories = Directory.GetDirectories(packagesRoot, "runtime.*.microsoft.netcore.ilasm")
                .OrderByDescending(static path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);

            foreach (var packageDirectory in packageDirectories)
            {
                var candidates = Directory.GetFiles(packageDirectory, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ilasm.exe" : "ilasm", SearchOption.AllDirectories)
                    .OrderByDescending(static path => path, StringComparer.OrdinalIgnoreCase);
                foreach (var candidate in candidates)
                {
                    if (File.Exists(candidate))
                        return candidate;
                }
            }

            return null;
        }

        private static string? ResolveFromWindowsFramework()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return null;

            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (string.IsNullOrWhiteSpace(windows))
                return null;

            var candidates = new[]
            {
                Path.Combine(windows, "Microsoft.NET", "Framework64", "v4.0.30319", "ilasm.exe"),
                Path.Combine(windows, "Microsoft.NET", "Framework", "v4.0.30319", "ilasm.exe"),
                Path.Combine(windows, "Microsoft.NET", "Framework64", "v2.0.50727", "ilasm.exe"),
                Path.Combine(windows, "Microsoft.NET", "Framework", "v2.0.50727", "ilasm.exe"),
            };

            return candidates.FirstOrDefault(File.Exists);
        }
    }
}
