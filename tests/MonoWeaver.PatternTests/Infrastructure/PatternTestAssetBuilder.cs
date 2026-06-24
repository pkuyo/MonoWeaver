using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Policy;
using System.Text;
using System.Threading.Tasks;
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

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var testAssemblyPath = typeof(PatternTestAssetBuilder).Assembly.Location;

        BuildAssembly(sourceRoot, "PatternFixtures", "PatternFixtures",
            references: new[] { testAssemblyPath }, version: null, optimize: true, result);
        BuildAssembly(sourceRoot, "PatternFixtures", "PatternFixtures.Debug",
            references: new[] { testAssemblyPath }, version: null, optimize: false, result);
        BuildAssembly(sourceRoot, "GameAssembly", "GameAssembly",
            references: Array.Empty<string>(), version: null, optimize: true, result);
        BuildAssembly(sourceRoot, "Game.Hooks", "Game.Hooks",
            references: Array.Empty<string>(), version: "2.3.4.5", optimize: true, result);
        BuildAssembly(sourceRoot, "Bad.Hooks", "Bad.Hooks",
            references: Array.Empty<string>(), version: "9.8.7.6", optimize: true, result);
        BuildAssembly(sourceRoot, "First", "First",
            references: Array.Empty<string>(), version: null, optimize: true, result);
        BuildAssembly(sourceRoot, "Second", "Second",
            references: Array.Empty<string>(), version: null, optimize: true, result);

        return result;
    }

    private static void BuildAssembly(string sourceRoot, string sourceDirectoryName, string assemblyName,
        IReadOnlyList<string> references, string? version, bool optimize, Dictionary<string, string> result)
    {
        var assemblySourceDirectory = Path.Combine(sourceRoot, sourceDirectoryName);
        if (!Directory.Exists(assemblySourceDirectory))
            throw new DirectoryNotFoundException($"Pattern source directory does not exist: {assemblySourceDirectory}");

        var sourceFiles = Directory.GetFiles(assemblySourceDirectory, "*.cs")
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (sourceFiles.Length == 0)
            throw new InvalidOperationException($"No .cs files were found in {assemblySourceDirectory}");

        var finalPath = Path.Combine(AssembliesDirectory, assemblyName + ".dll");
        var stampPath = finalPath + ".inputs.sha256";
        var fingerprint = ComputeFingerprint(assemblyName, sourceFiles, references, version, optimize);
        if (File.Exists(finalPath)
            && File.Exists(stampPath)
            && string.Equals(File.ReadAllText(stampPath).Trim(), fingerprint,
                StringComparison.OrdinalIgnoreCase))
        {
            result[assemblyName] = finalPath;
            return;
        }

        var buildDirectory = Path.Combine(AssembliesDirectory, "_build", assemblyName);
        Directory.CreateDirectory(buildDirectory);
        var projectPath = Path.Combine(buildDirectory, assemblyName + ".csproj");
        File.WriteAllText(projectPath, CreateProjectXml(assemblyName, sourceFiles, references, version, optimize));

        var outputDirectory = Path.Combine(buildDirectory, "output");
        Directory.CreateDirectory(outputDirectory);
        RunProcess("dotnet", new[]
        {
            "build", projectPath,
            "--configuration", "Release",
            "--output", outputDirectory,
            "--nologo",
            "--disable-build-servers",
            "-p:UseSharedCompilation=false",
        }, buildDirectory, $"compile pattern fixture assembly '{assemblyName}'");

        var outputPath = Path.Combine(outputDirectory, assemblyName + ".dll");
        if (!File.Exists(outputPath))
            throw new FileNotFoundException($"Expected compiled pattern fixture was not produced: {outputPath}");

        File.Copy(outputPath, finalPath, overwrite: true);
        File.WriteAllText(stampPath, fingerprint + Environment.NewLine);
        result[assemblyName] = finalPath;
    }

    private static string ComputeFingerprint(string assemblyName, IReadOnlyList<string> sourceFiles,
        IReadOnlyList<string> references, string? version, bool optimize)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendText($"assembly={assemblyName}\nversion={version}\noptimize={optimize}\n");

        foreach (var path in sourceFiles.Concat(references)
                     .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
        {
            AppendText(Path.GetFileName(path));
            AppendText("\n");
            using var stream = File.OpenRead(path);
            var buffer = new byte[81920];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
                hash.AppendData(buffer, 0, read);
        }

        return BitConverter.ToString(hash.GetHashAndReset()).Replace("-", "");

        void AppendText(string value)
            => hash.AppendData(Encoding.UTF8.GetBytes(value));
    }

    private static string CreateProjectXml(string assemblyName, IReadOnlyList<string> sourceFiles,
        IReadOnlyList<string> references, string? version, bool optimize)
    {
        var project = new XElement("Project",
            new XAttribute("Sdk", "Microsoft.NET.Sdk"),
            new XElement("PropertyGroup",
                new XElement("TargetFramework", "net10.0"),
                new XElement("OutputType", "Library"),
                new XElement("AssemblyName", assemblyName),
                new XElement("LangVersion", "12"),
                new XElement("Nullable", "enable"),
                new XElement("EnableDefaultCompileItems", "false"),
                new XElement("Optimize", optimize ? "true" : "false"),
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
        process.StartInfo.Environment["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] = "1";
        process.StartInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        process.StartInfo.Arguments = string.Join(" ", arguments);

        process.Start();
        // Drain both redirected streams concurrently. Reading stdout to completion before
        // stderr can deadlock when MSBuild or a compiler fills the other pipe.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WaitAll(stdoutTask, stderrTask);
        var stdout = stdoutTask.Result;
        var stderr = stderrTask.Result;

        if (process.ExitCode == 0)
            return;

        throw new InvalidOperationException(string.Join(Environment.NewLine,
            $"{description} failed with exit code {process.ExitCode}.",
            "Command:",
            "  " + process.StartInfo.FileName + " " + string.Join(" ", arguments.Select(QuoteArgument)),
            "stdout:",
            string.IsNullOrWhiteSpace(stdout) ? "  <empty>" : stdout,
            "stderr:",
            string.IsNullOrWhiteSpace(stderr) ? "  <empty>" : stderr));
    }


    public static bool IsPathFullyQualified(string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;
        string p = path.Replace('/', '\\');

        if (p.StartsWith(@"\\?\"))
        {
            p = p.Substring(4);

            return IsDriveFullyQualified(p);
        }

        return IsDriveFullyQualified(p);
    }

    private static bool IsDriveFullyQualified(string p)
    {
        return p.Length >= 3
            && char.IsLetter(p[0])
            && p[1] == ':'
            && p[2] == '\\';
    }

 
    private static string? ResolveCommand(string command)
    {
        if (IsPathFullyQualified(command))
            return File.Exists(command) ? command : null;

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var extensions = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';')
            : new[] { string.Empty };

        foreach (var directory in path.Split(Path.PathSeparator))
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
        AddSearchDirectoryIfExists(resolver,
            Path.GetDirectoryName(typeof(ArgumenPlan).Assembly.Location));
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
