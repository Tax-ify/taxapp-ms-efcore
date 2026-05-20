// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using Microsoft.EntityFrameworkCore.Design.Internal;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Design;
using Microsoft.EntityFrameworkCore.Scaffolding;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.EntityFrameworkCore.Tools.MigrationMetadataRegenerator;

internal sealed class MigrationMetadataRegeneratorService(RegeneratorOptions options, ConsoleReportHandler reporter)
{
    private readonly RegeneratorOptions _options = options;
    private readonly ConsoleReportHandler _reporter = reporter;

    public int Run()
    {
        ValidateOptions();

        if (!_options.NoBuild)
        {
            ProjectMetadata.Build(_options.Project, _options.Configuration, _options.Framework, _reporter);
            if (_options.StartupProject is not null
                && !string.Equals(
                    _options.StartupProject.FullName,
                    _options.Project.FullName,
                    StringComparison.OrdinalIgnoreCase))
            {
                ProjectMetadata.Build(_options.StartupProject, _options.Configuration, _options.Framework, _reporter);
            }
        }

        var projectMetadata = ProjectMetadata.Load(_options.Project, _options.Configuration, _options.Framework, _reporter);
        var startupMetadata = _options.StartupProject is null
            ? projectMetadata
            : ProjectMetadata.Load(_options.StartupProject, _options.Configuration, _options.Framework, _reporter);

        _reporter.OnVerbose($"Project assembly: {projectMetadata.TargetPath}");
        _reporter.OnVerbose($"Startup assembly: {startupMetadata.TargetPath}");

        RegisterAssemblyResolution(projectMetadata, startupMetadata);

        var assembly = Assembly.LoadFrom(projectMetadata.TargetPath);
        var startupAssembly = string.Equals(
                projectMetadata.TargetPath,
                startupMetadata.TargetPath,
                StringComparison.OrdinalIgnoreCase)
            ? assembly
            : Assembly.LoadFrom(startupMetadata.TargetPath);

        var operationReporter = new OperationReporter(_reporter);
        var contextOperations = new DbContextOperations(
            operationReporter,
            assembly,
            startupAssembly,
            project: _options.Project.FullName,
            projectDir: projectMetadata.ProjectDir,
            rootNamespace: projectMetadata.RootNamespace,
            language: projectMetadata.Language,
            nullable: projectMetadata.Nullable,
            args: Array.Empty<string>());
        var servicesBuilder = new DesignTimeServicesBuilder(
            assembly,
            startupAssembly,
            operationReporter,
            args: Array.Empty<string>());

        using var context = contextOperations.CreateContext(_options.Context);
        context.Database.SetConnectionString(_options.Connection);

        var services = servicesBuilder.Build(context);
        EnsureRelational(services);

        var migrationsAssembly = services.GetRequiredService<IMigrationsAssembly>();
        var idGenerator = services.GetRequiredService<IMigrationsIdGenerator>();

        var (fromId, toId, toRegenerate) = ResolveMigrationRange(migrationsAssembly, idGenerator);

        if (toRegenerate.Count == 0)
        {
            _reporter.OnInformation($"No migrations to regenerate between '{fromId}' and '{toId}'.");
            return 0;
        }

        _reporter.OnInformation(
            $"Plan: bring scratch DB to '{fromId}', then regenerate Designer for {toRegenerate.Count} migration(s) through '{toId}'.");
        foreach (var id in toRegenerate)
        {
            _reporter.OnVerbose($"  + {id}");
        }

        if (_options.DryRun)
        {
            _reporter.OnInformation("--dry-run set; not connecting to the database or writing files.");
            return 0;
        }

        Directory.CreateDirectory(_options.Output.FullName);

        var migrator = services.GetRequiredService<IMigrator>();
        _reporter.OnInformation($"Migrating scratch database to baseline '{fromId}'...");
        migrator.Migrate(fromId);

        var written = new List<string>();
        foreach (var migrationId in toRegenerate)
        {
            _reporter.OnInformation($"Applying migration '{migrationId}'...");
            migrator.Migrate(migrationId);

            var path = WriteDesignerForCurrentDatabase(services, context, migrationId, migrationsAssembly, idGenerator);
            written.Add(path);
            _reporter.OnInformation($"  wrote {path}");
        }

        PrintSummary(written);
        return 0;
    }

    private void ValidateOptions()
    {
        if (!_options.Project.Exists)
        {
            throw new InvalidOperationException($"Project file not found: '{_options.Project.FullName}'.");
        }

        if (_options.StartupProject is not null && !_options.StartupProject.Exists)
        {
            throw new InvalidOperationException($"Startup project file not found: '{_options.StartupProject.FullName}'.");
        }

        if (string.IsNullOrWhiteSpace(_options.Connection))
        {
            throw new InvalidOperationException("--connection is required and must be non-empty.");
        }

        if (string.IsNullOrWhiteSpace(_options.From) || string.IsNullOrWhiteSpace(_options.To))
        {
            throw new InvalidOperationException("--from and --to are required.");
        }
    }

    private (string FromId, string ToId, List<string> ToRegenerate) ResolveMigrationRange(
        IMigrationsAssembly migrationsAssembly,
        IMigrationsIdGenerator idGenerator)
    {
        var fromId = migrationsAssembly.FindMigrationId(_options.From)
            ?? throw new InvalidOperationException(
                $"Migration '{_options.From}' was not found in the project. Use the full migration id (e.g. 20240517120000_Name) or a unique name.");
        var toId = migrationsAssembly.FindMigrationId(_options.To)
            ?? throw new InvalidOperationException(
                $"Migration '{_options.To}' was not found in the project.");

        if (string.Compare(fromId, toId, StringComparison.Ordinal) > 0)
        {
            throw new InvalidOperationException(
                $"--from '{fromId}' must come before --to '{toId}' in migration timestamp order.");
        }

        var toRegenerate = migrationsAssembly.Migrations.Keys
            .Where(k =>
                string.Compare(k, fromId, StringComparison.Ordinal) > 0
                && string.Compare(k, toId, StringComparison.Ordinal) <= 0)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        return (fromId, toId, toRegenerate);
    }

    private string WriteDesignerForCurrentDatabase(
        IServiceProvider services,
        DbContext context,
        string migrationId,
        IMigrationsAssembly migrationsAssembly,
        IMigrationsIdGenerator idGenerator)
    {
        var migrationType = migrationsAssembly.Migrations[migrationId];

        var databaseModelFactory = services.GetRequiredService<IDatabaseModelFactory>();
        var scaffoldingModelFactory = services.GetRequiredService<IScaffoldingModelFactory>();
        var modelRuntimeInitializer = services.GetRequiredService<IModelRuntimeInitializer>();
        var codeGeneratorSelector = services.GetRequiredService<IMigrationsCodeGeneratorSelector>();

        var databaseModel = databaseModelFactory.Create(
            _options.Connection,
            new DatabaseModelFactoryOptions());
        var model = scaffoldingModelFactory.Create(databaseModel, new ModelReverseEngineerOptions())
            ?? throw new InvalidOperationException(
                $"Scaffolding produced a null model for migration '{migrationId}'.");
        var initializedModel = modelRuntimeInitializer.Initialize(model, designTime: true, validationLogger: null);

        var codeGenerator = codeGeneratorSelector.Select(language: null);
        var migrationName = idGenerator.GetName(migrationId);
        var migrationNamespace = migrationType.Namespace;

        var metadataCode = codeGenerator.GenerateMetadata(
            migrationNamespace,
            context.GetType(),
            migrationName,
            migrationId,
            initializedModel);

        var outputFile = Path.Combine(_options.Output.FullName, migrationId + ".Designer.cs");
        File.WriteAllText(outputFile, metadataCode, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return outputFile;
    }

    private void PrintSummary(IReadOnlyList<string> written)
    {
        Console.Out.WriteLine();
        Console.Out.WriteLine("Migration Designer regeneration summary");
        Console.Out.WriteLine($"  Connection : {Redact(_options.Connection)}");
        Console.Out.WriteLine($"  From       : {_options.From}");
        Console.Out.WriteLine($"  To         : {_options.To}");
        Console.Out.WriteLine($"  Output dir : {_options.Output.FullName}");
        Console.Out.WriteLine($"  Files      : {written.Count}");
        foreach (var path in written)
        {
            Console.Out.WriteLine($"    - {path}");
        }
    }

    private static string Redact(string connectionString)
    {
        var sb = new StringBuilder();
        foreach (var segment in connectionString.Split(';'))
        {
            if (segment.Length == 0)
            {
                continue;
            }

            var eq = segment.IndexOf('=');
            if (eq > 0)
            {
                var key = segment[..eq].Trim();
                if (key.Equals("Password", StringComparison.OrdinalIgnoreCase)
                    || key.Equals("Pwd", StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append(key).Append("=***;");
                    continue;
                }
            }

            sb.Append(segment).Append(';');
        }

        return sb.ToString().TrimEnd(';');
    }

    private static void EnsureRelational(IServiceProvider services)
    {
        var migrator = services.GetService<IMigrator>();
        if (migrator is null)
        {
            var provider = services.GetService<IDatabaseProvider>();
            throw new InvalidOperationException(
                $"The configured database provider '{provider?.Name ?? "unknown"}' is not a relational provider; migrations are not supported.");
        }
    }

    private static void RegisterAssemblyResolution(ProjectMetadata project, ProjectMetadata startup)
    {
        var probeDirs = new List<string>(2)
        {
            Path.GetDirectoryName(startup.TargetPath)!
        };

        if (!string.Equals(project.TargetPath, startup.TargetPath, StringComparison.OrdinalIgnoreCase))
        {
            probeDirs.Add(Path.GetDirectoryName(project.TargetPath)!);
        }

        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            foreach (var dir in probeDirs)
            {
                var candidate = Path.Combine(dir, name.Name + ".dll");
                if (File.Exists(candidate))
                {
                    return context.LoadFromAssemblyPath(candidate);
                }
            }

            return null;
        };
    }
}
