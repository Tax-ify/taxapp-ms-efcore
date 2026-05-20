// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Microsoft.EntityFrameworkCore.Tools.MigrationMetadataRegenerator;

internal sealed record ProjectMetadata(
    string ProjectPath,
    string ProjectDir,
    string TargetPath,
    string RootNamespace,
    string Language,
    bool Nullable,
    string TargetFramework)
{
    public static ProjectMetadata Load(
        FileInfo project,
        string configuration,
        string? framework,
        ConsoleReportHandler reporter)
    {
        var props = new[] { "TargetPath", "ProjectDir", "RootNamespace", "Language", "Nullable", "TargetFramework" };

        var args = new List<string>
        {
            "msbuild",
            project.FullName,
            "-nologo",
            "-v:quiet",
            $"-p:Configuration={configuration}",
            $"-getProperty:{string.Join(',', props)}"
        };

        if (!string.IsNullOrEmpty(framework))
        {
            args.Add($"-p:TargetFramework={framework}");
        }

        reporter.OnVerbose($"Reading project properties: dotnet {string.Join(' ', args)}");

        var (stdout, stderr, exitCode) = RunProcess("dotnet", args);

        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"Failed to read MSBuild properties from '{project.FullName}'.{Environment.NewLine}{stderr}{Environment.NewLine}{stdout}");
        }

        var values = ParseGetPropertyOutput(stdout, props);

        var targetPath = values["TargetPath"];
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            throw new InvalidOperationException(
                $"MSBuild returned an empty TargetPath for '{project.FullName}'. Build the project first or pass --framework <tfm> for multi-targeted projects.");
        }

        var rootNamespace = values["RootNamespace"];
        if (string.IsNullOrWhiteSpace(rootNamespace))
        {
            rootNamespace = Path.GetFileNameWithoutExtension(project.FullName);
        }

        var language = values["Language"];
        if (string.IsNullOrWhiteSpace(language))
        {
            language = "C#";
        }

        var nullable = string.Equals(values["Nullable"], "enable", StringComparison.OrdinalIgnoreCase)
            || string.Equals(values["Nullable"], "annotations", StringComparison.OrdinalIgnoreCase);

        return new ProjectMetadata(
            ProjectPath: project.FullName,
            ProjectDir: values["ProjectDir"],
            TargetPath: targetPath,
            RootNamespace: rootNamespace,
            Language: language,
            Nullable: nullable,
            TargetFramework: values["TargetFramework"]);
    }

    public static void Build(FileInfo project, string configuration, string? framework, ConsoleReportHandler reporter)
    {
        var args = new List<string>
        {
            "build",
            project.FullName,
            "-nologo",
            "-v:quiet",
            $"-c:{configuration}"
        };

        if (!string.IsNullOrEmpty(framework))
        {
            args.Add($"-f:{framework}");
        }

        reporter.OnInformation($"Building {Path.GetFileName(project.FullName)} ({configuration})...");
        reporter.OnVerbose($"dotnet {string.Join(' ', args)}");

        var (stdout, stderr, exitCode) = RunProcess("dotnet", args);

        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"Build failed for '{project.FullName}'.{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
        }
    }

    private static Dictionary<string, string> ParseGetPropertyOutput(string stdout, IEnumerable<string> propertyNames)
    {
        var trimmed = stdout.Trim();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        if (trimmed.StartsWith('{'))
        {
            using var doc = JsonDocument.Parse(trimmed);
            if (doc.RootElement.TryGetProperty("Properties", out var propsElement))
            {
                foreach (var prop in propsElement.EnumerateObject())
                {
                    result[prop.Name] = prop.Value.GetString() ?? string.Empty;
                }
            }
            else
            {
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    result[prop.Name] = prop.Value.GetString() ?? string.Empty;
                }
            }
        }
        else
        {
            // Newline-separated value list in property-declared order.
            var lines = trimmed.Split(['\r', '\n'], StringSplitOptions.None);
            var i = 0;
            foreach (var name in propertyNames)
            {
                result[name] = i < lines.Length ? lines[i].Trim() : string.Empty;
                i++;
            }
        }

        foreach (var name in propertyNames)
        {
            result.TryAdd(name, string.Empty);
        }

        return result;
    }

    private static (string Stdout, string Stderr, int ExitCode) RunProcess(string fileName, IList<string> args)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();

        return (stdout.ToString(), stderr.ToString(), process.ExitCode);
    }
}
