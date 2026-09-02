using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using Lumio.GameRuntime.Ecs;

namespace Lumio.GameRuntime.Ecs.Tests;

internal static class PersistRestoreChildBootstrap
{
    [ModuleInitializer]
    internal static void RunIfRequested()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("LUMIO_ECS_PERSIST_RESTORE_CHILD"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        int code;
        try
        {
            code = PersistRestoreChildHost.Run();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            code = 1;
        }

        Environment.Exit(code);
    }
}

internal static class PersistRestoreChildHost
{
    internal const string ChildFlagVariable = "LUMIO_ECS_PERSIST_RESTORE_CHILD";
    internal const string SnapshotPathVariable = "LUMIO_ECS_PERSIST_SNAPSHOT_PATH";

    internal static int Run()
    {
        string? path = Environment.GetEnvironmentVariable(SnapshotPathVariable);
        Console.WriteLine("CHILD_PID=" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        if (string.IsNullOrWhiteSpace(path))
        {
            Console.Error.WriteLine("missing snapshot path");
            return 2;
        }

        using DestWorld destination = PersistSnapshotTestSchema.CreateEmptyRunningWorld(961);
        StorageOperationResult restored = EcsPersistSnapshotPipeline.RestorePersist(destination.World, path);
        Console.WriteLine("RESTORE_STATUS=" + restored.Status);
        Console.WriteLine("ACTIVE_ENTITY_COUNT=" + destination.World.ActiveEntityCount.ToString(CultureInfo.InvariantCulture));
        if (!restored.IsSuccess)
        {
            Console.WriteLine("RESTORE_ERROR=" + restored.Error?.Code);
            return 3;
        }

        var entities = new LocalEntityId[destination.World.Budget.MaxEntities];
        StorageOperationResult enumerated = destination.Storage.EnumerateOrdered(default, entities, out int written);
        if (!enumerated.IsSuccess)
        {
            Console.WriteLine("ENUMERATE_ERROR=" + enumerated.Error?.Code);
            return 4;
        }

        Console.WriteLine("ENTITY_COUNT=" + written.ToString(CultureInfo.InvariantCulture));
        int observedHistoryTotal = 0;
        for (int i = 0; i < written; i++)
        {
            LocalEntityId entity = entities[i];
            string text = DecodeText(PersistSnapshotTestSchema.ReadRequired(
                destination.Storage,
                entity,
                PersistSnapshotTestSchema.ChatComponentType,
                PersistSnapshotTestSchema.LastMessageTextField,
                PersistSnapshotTestSchema.LastMessageTextSizeBytes));
            ulong tick = DecodeTick(PersistSnapshotTestSchema.ReadRequired(
                destination.Storage,
                entity,
                PersistSnapshotTestSchema.ChatComponentType,
                PersistSnapshotTestSchema.LastMessageTickField,
                PersistSnapshotTestSchema.LastMessageTickSizeBytes));
            int historyCount = PersistSnapshotTestSchema.ObserveHistoryCount(destination.Storage, entity);
            observedHistoryTotal += historyCount;
            Console.WriteLine(
                "LAST_MESSAGE_TEXT_" + entity.Index.ToString(CultureInfo.InvariantCulture) + "_" +
                entity.Generation.ToString(CultureInfo.InvariantCulture) + "=" + text);
            Console.WriteLine(
                "LAST_MESSAGE_TICK_" + entity.Index.ToString(CultureInfo.InvariantCulture) + "_" +
                entity.Generation.ToString(CultureInfo.InvariantCulture) + "=" +
                tick.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine(
                "HISTORY_COUNT_" + entity.Index.ToString(CultureInfo.InvariantCulture) + "_" +
                entity.Generation.ToString(CultureInfo.InvariantCulture) + "=" +
                historyCount.ToString(CultureInfo.InvariantCulture));
        }

        Console.WriteLine("HISTORY_COUNT=" + observedHistoryTotal.ToString(CultureInfo.InvariantCulture));
        StorageOperationResult recaptured = EcsPersistSnapshotPipeline.CapturePersist(destination.World, out byte[]? round2);
        if (!recaptured.IsSuccess || round2 is null)
        {
            Console.WriteLine("RECAPTURE_ERROR=" + recaptured.Error?.Code);
            return 5;
        }

        Console.WriteLine("ROUND2_SHA256=" + PersistSnapshotTestSchema.Sha256Hex(round2));
        Console.WriteLine("CHILD_PROCESS_NAME=" + Process.GetCurrentProcess().ProcessName);
        return 0;
    }

    internal static ProcessStartInfo CreateStartInfo(string snapshotPath)
    {
        var start = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.Environment[ChildFlagVariable] = "1";
        start.Environment[SnapshotPathVariable] = snapshotPath;

        string assemblyLocation = typeof(PersistRestoreChildHost).Assembly.Location;
        string exe = Path.ChangeExtension(assemblyLocation, ".exe");
        string? processPath = Environment.ProcessPath;
        if (IsRunnableHost(processPath))
        {
            start.FileName = processPath;
        }
        else if (File.Exists(exe))
        {
            start.FileName = exe;
        }
        else
        {
            start.FileName = "dotnet";
            start.ArgumentList.Add(assemblyLocation);
        }

        return start;
    }

    private static bool IsRunnableHost(string? processPath)
    {
        if (string.IsNullOrWhiteSpace(processPath) || !File.Exists(processPath))
            return false;
        string name = Path.GetFileNameWithoutExtension(processPath);
        return !string.Equals(name, "dotnet", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(name, "testhost", StringComparison.OrdinalIgnoreCase);
    }

    private static string DecodeText(byte[] field)
    {
        uint length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(field);
        return Encoding.UTF8.GetString(field, 4, checked((int)length));
    }

    private static ulong DecodeTick(byte[] field) =>
        System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(field);
}
