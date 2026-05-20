// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using Microsoft.EntityFrameworkCore.Design;

namespace Microsoft.EntityFrameworkCore.Tools.MigrationMetadataRegenerator;

internal sealed class ConsoleReportHandler(bool verbose) : IOperationReportHandler
{
    public int Version => 0;

    public void OnError(string message)
    {
        var prev = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine("error:   " + message);
        }
        finally
        {
            Console.ForegroundColor = prev;
        }
    }

    public void OnWarning(string message)
    {
        var prev = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Error.WriteLine("warn:    " + message);
        }
        finally
        {
            Console.ForegroundColor = prev;
        }
    }

    public void OnInformation(string message)
        => Console.Error.WriteLine("info:    " + message);

    public void OnVerbose(string message)
    {
        if (verbose)
        {
            Console.Error.WriteLine("verbose: " + message);
        }
    }
}
