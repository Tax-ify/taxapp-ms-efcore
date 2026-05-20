// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO;

namespace Microsoft.EntityFrameworkCore.Tools.MigrationMetadataRegenerator;

internal sealed class RegeneratorOptions
{
    public required FileInfo Project { get; init; }
    public FileInfo? StartupProject { get; init; }
    public required string Context { get; init; }
    public required string Connection { get; init; }
    public required string From { get; init; }
    public required string To { get; init; }
    public required DirectoryInfo Output { get; init; }
    public string Configuration { get; init; } = "Debug";
    public string? Framework { get; init; }
    public bool NoBuild { get; init; }
    public bool DryRun { get; init; }
    public bool Verbose { get; init; }
}
