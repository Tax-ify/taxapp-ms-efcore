// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.CommandLine;
using System.IO;
using System.Threading.Tasks;

namespace Microsoft.EntityFrameworkCore.Tools.MigrationMetadataRegenerator;

internal static class Program
{
    public static Task<int> Main(string[] args)
    {
        var projectOption = new Option<FileInfo>("--project")
        {
            Description = "Path to the project (.csproj) that contains the DbContext and migrations.",
            Required = true
        };

        var startupProjectOption = new Option<FileInfo?>("--startup-project")
        {
            Description = "Path to the startup project (.csproj). Defaults to --project."
        };

        var contextOption = new Option<string>("--context")
        {
            Description = "Short name of the DbContext type (e.g. AppDbContext).",
            Required = true
        };

        var connectionOption = new Option<string>("--connection")
        {
            Description = "Connection string for the dedicated scratch database. The database will be created if it does not exist.",
            Required = true
        };

        var fromOption = new Option<string>("--from")
        {
            Description = "Migration id (or unique name) representing the last known-good baseline. The scratch DB is migrated to this state before regeneration begins.",
            Required = true
        };

        var toOption = new Option<string>("--to")
        {
            Description = "Migration id (or unique name) representing the last migration to apply and emit a Designer for.",
            Required = true
        };

        var outputOption = new Option<DirectoryInfo>("--output")
        {
            Description = "Folder where regenerated *.Designer.cs files are written. v1 never overwrites project files.",
            Required = true
        };

        var configurationOption = new Option<string>("--configuration")
        {
            Description = "MSBuild configuration to use when building the project (default: Debug).",
            DefaultValueFactory = _ => "Debug"
        };

        var frameworkOption = new Option<string?>("--framework")
        {
            Description = "Target framework moniker for multi-targeted projects (e.g. net9.0)."
        };

        var noBuildOption = new Option<bool>("--no-build")
        {
            Description = "Skip building the project before loading."
        };

        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Validate inputs and print the plan without connecting to the database or writing files."
        };

        var verboseOption = new Option<bool>("--verbose")
        {
            Description = "Enable verbose logging."
        };

        var rootCommand = new RootCommand("Regenerates migration .Designer.cs files into an output folder by applying migrations against a scratch database.")
        {
            projectOption,
            startupProjectOption,
            contextOption,
            connectionOption,
            fromOption,
            toOption,
            outputOption,
            configurationOption,
            frameworkOption,
            noBuildOption,
            dryRunOption,
            verboseOption
        };

        rootCommand.SetAction(parseResult =>
        {
            var options = new RegeneratorOptions
            {
                Project = parseResult.GetValue(projectOption)!,
                StartupProject = parseResult.GetValue(startupProjectOption),
                Context = parseResult.GetValue(contextOption)!,
                Connection = parseResult.GetValue(connectionOption)!,
                From = parseResult.GetValue(fromOption)!,
                To = parseResult.GetValue(toOption)!,
                Output = parseResult.GetValue(outputOption)!,
                Configuration = parseResult.GetValue(configurationOption) ?? "Debug",
                Framework = parseResult.GetValue(frameworkOption),
                NoBuild = parseResult.GetValue(noBuildOption),
                DryRun = parseResult.GetValue(dryRunOption),
                Verbose = parseResult.GetValue(verboseOption)
            };

            var reporter = new ConsoleReportHandler(options.Verbose);

            try
            {
                return new MigrationMetadataRegeneratorService(options, reporter).Run();
            }
            catch (Exception ex)
            {
                reporter.OnError(ex.Message);
                if (options.Verbose)
                {
                    reporter.OnVerbose(ex.ToString());
                }
                return 1;
            }
        });

        return rootCommand.Parse(args).InvokeAsync();
    }
}
