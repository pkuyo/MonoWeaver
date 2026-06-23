using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace MonoWeaver.PatternTests;

internal static class PatternTestAssetBuilder
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> CompiledAssemblies = new(BuildPatternAssemblies);

    public static string AssembliesDirectory => Path.Combine(AppContext.BaseDirectory, "PatternAssemblies");

    public static string GetAssemblyPath(string assemblyName)
    {
        var assemblies = CompiledAssemblies.Value;
        return assemblies.TryGetValue(assemblyName, out var path)
            ? path
            : throw new FileNotFoundException($"Pattern fixture assembly '{assemblyName}' was not built.");
    }

    private static IReadOnlyDictionary<string, string> BuildPatternAssemblies()
    {
        var sourceRoot = Path.Combine(AppContext.BaseDirectory, "PatternSources");
        if (!Directory.Exists(sourceRoot))
            throw new DirectoryNotFoundException($"Pattern source directory was not copied to the test output: {sourceRoot}");

        Directory.CreateDirectory(AssembliesDirectory);
        foreach (var staleDll in Directory.GetFiles(AssembliesDirectory, "*.dll"))
            File.Delete(staleDll);

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var testAssemblyPath = typeof(PatternTestAssetBuilder).Assembly.Location;

        BuildAssembly(sourceRoot, "PatternFixtures", references: new[] { testAssemblyPath }, version: null, result);
        BuildAssembly(sourceRoot, "GameAssembly", references: Array.Empty<string>(), version: null, result);
        BuildAssembly(sourceRoot, "Game.Hooks", references: Array.Empty<string>(), version: "2.3.4.5", result);
        BuildAssembly(sourceRoot, "Bad.Hooks", references: Array.Empty<string>(), version: "9.8.7.6", result);
        BuildAssembly(sourceRoot, "First", references: Array.Empty<string>(), version: null, result);
        BuildAssembly(sourceRoot, "Second", references: Array.Empty<string>(), version: null, result);

        return result;
    }

    private static void BuildAssembly(string sourceRoot, string assemblyName, IReadOnlyList<string> references,
        string? version, Dictionary<string, string> result)
    {
        var assemblySourceDirectory = Path.Combine(sourceRoot, assemblyName);
        if (!Directory.Exists(assemblySourceDirectory))
            throw new DirectoryNotFoundException($"Pattern source directory does not exist: {assemblySourceDirectory}");

        var sourceFiles = Directory.GetFiles(assemblySourceDirectory, "*.cs")
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (sourceFiles.Length == 0)
            throw new InvalidOperationException($"No .cs files were found in {assemblySourceDirectory}");

        var buildDirectory = Path.Combine(AssembliesDirectory, "_build", assemblyName);
        Directory.CreateDirectory(buildDirectory);
        var projectPath = Path.Combine(buildDirectory, assemblyName + ".csproj");
        File.WriteAllText(projectPath, CreateProjectXml(assemblyName, sourceFiles, references, version));

        RunProcess("dotnet", new[] { "build", projectPath, "--configuration", "Release", "--nologo" },
            buildDirectory, $"compile pattern fixture assembly '{assemblyName}'");

        var outputPath = Path.Combine(buildDirectory, "bin", "Release", "net10.0", assemblyName + ".dll");
        if (!File.Exists(outputPath))
            throw new FileNotFoundException($"Expected compiled pattern fixture was not produced: {outputPath}");

        var finalPath = Path.Combine(AssembliesDirectory, assemblyName + ".dll");
        File.Copy(outputPath, finalPath, overwrite: true);
        result[assemblyName] = finalPath;
    }

    private static string CreateProjectXml(string assemblyName, IReadOnlyList<string> sourceFiles,
        IReadOnlyList<string> references, string? version)
    {
        var project = new XElement("Project",
            new XAttribute("Sdk", "Microsoft.NET.Sdk"),
            new XElement("PropertyGroup",
                new XElement("TargetFramework", "net10.0"),
                new XElement("OutputType", "Library"),
                new XElement("AssemblyName", assemblyName),
                new XElement("LangVersion", "12"),
                new XElement("Nullable", "enable"),
                new XElement("Optimize", "true"),
                new XElement("DebugType", "none"),
                new XElement("Deterministic", "true"),
                version is null ? null : new XElement("Version", version),
                version is null ? null : new XElement("AssemblyVersion", version),
                version is null ? null : new XElement("FileVersion", version)),
            new XElement("ItemGroup",
                sourceFiles.Select(source => new XElement("Compile",
                    new XAttribute("Include", source),
                    new XAttribute("Link", Path.GetFileName(source))))));

        if (references.Count != 0)
        {
            project.Add(new XElement("ItemGroup",
                references.Select(reference => new XElement("Reference",
                    new XAttribute("Include", Path.GetFileNameWithoutExtension(reference)),
                    new XElement("HintPath", reference),
                    new XElement("Private", "false")))));
        }

        return new XDocument(project).ToString();
    }

    private static void RunProcess(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
        string description)
    {
        using var process = new Process();
        process.StartInfo.FileName = ResolveCommand(fileName) ?? fileName;
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
            "  " + process.StartInfo.FileName + " " + string.Join(' ', arguments.Select(QuoteArgument)),
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

    private static string QuoteArgument(string argument)
        => argument.Any(char.IsWhiteSpace) ? "\"" + argument.Replace("\"", "\\\"") + "\"" : argument;
}

internal static class PatternTestModules
{
    public static Mono.Cecil.ModuleDefinition Open(string assemblyName)
    {
        var resolver = new Mono.Cecil.DefaultAssemblyResolver();
        AddSearchDirectoryIfExists(resolver, PatternTestAssetBuilder.AssembliesDirectory);
        AddSearchDirectoryIfExists(resolver, AppContext.BaseDirectory);
        AddSearchDirectoryIfExists(resolver, Path.GetDirectoryName(typeof(object).Assembly.Location));

        return Mono.Cecil.ModuleDefinition.ReadModule(PatternTestAssetBuilder.GetAssemblyPath(assemblyName),
            new Mono.Cecil.ReaderParameters
            {
                AssemblyResolver = resolver,
                ReadSymbols = false,
                InMemory = true,
            });
    }

    private static void AddSearchDirectoryIfExists(Mono.Cecil.DefaultAssemblyResolver resolver, string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            resolver.AddSearchDirectory(path);
    }
}
