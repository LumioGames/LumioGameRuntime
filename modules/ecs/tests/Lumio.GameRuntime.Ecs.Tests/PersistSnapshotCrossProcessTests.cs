using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Lumio.GameRuntime.Ecs;
using Xunit;

namespace Lumio.GameRuntime.Ecs.Tests;

public sealed class PersistSnapshotCrossProcessTests
{
    [Fact]
    public void ProcessAWritesAndIndependentProcessBReadsLastMessageWithoutHistory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "lumio-ecs-persist-xproc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "room.persist");
        try
        {
            using SourceWorld source = PersistSnapshotTestSchema.CreatePopulatedWorld(960);
            StorageOperationResult written = EcsPersistSnapshotPipeline.CapturePersist(source.World, path);
            Assert.Equal(StorageOperationStatus.Accepted, written.Status);
            byte[] onDisk = File.ReadAllBytes(path);
            string round1 = PersistSnapshotTestSchema.Sha256Hex(onDisk);
            int parentPid = Environment.ProcessId;
            Console.WriteLine("PARENT_PID=" + parentPid.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("PARENT_PROCESS_NAME=" + Process.GetCurrentProcess().ProcessName);
            Console.WriteLine("FILE_SHA256=" + round1);
            Console.WriteLine("ROUND1_SHA256=" + round1);
            foreach (SourceEntity entity in source.Entities)
            {
                Console.WriteLine(
                    "SOURCE_LAST_MESSAGE_TEXT_" + entity.Entity.Index + "_" + entity.Entity.Generation + "=" + entity.Text);
                Console.WriteLine(
                    "SOURCE_LAST_MESSAGE_TICK_" + entity.Entity.Index + "_" + entity.Entity.Generation + "=" + entity.Tick);
            }

            ProcessStartInfo start = PersistRestoreChildHost.CreateStartInfo(path);
            using var child = Process.Start(start);
            Assert.NotNull(child);
            string stdout = child.StandardOutput.ReadToEnd();
            string stderr = child.StandardError.ReadToEnd();
            Assert.True(child.WaitForExit(120_000), "child process did not exit");
            Console.WriteLine(stdout);
            if (stderr.Length > 0) Console.WriteLine(stderr);
            Assert.True(child.ExitCode == 0, "child exit " + child.ExitCode + " stderr=" + stderr + " stdout=" + stdout);

            int childPid = ReadRequiredInt(stdout, "CHILD_PID=");
            Assert.NotEqual(parentPid, childPid);
            Assert.NotEqual(0, childPid);
            Assert.Contains("RESTORE_STATUS=Accepted", stdout, StringComparison.Ordinal);
            foreach (SourceEntity entity in source.Entities)
            {
                string textKey = "LAST_MESSAGE_TEXT_" + entity.Entity.Index + "_" + entity.Entity.Generation + "=";
                string tickKey = "LAST_MESSAGE_TICK_" + entity.Entity.Index + "_" + entity.Entity.Generation + "=";
                string historyKey = "HISTORY_COUNT_" + entity.Entity.Index + "_" + entity.Entity.Generation + "=";
                Assert.Contains(textKey + entity.Text, stdout, StringComparison.Ordinal);
                Assert.Contains(tickKey + entity.Tick.ToString(CultureInfo.InvariantCulture), stdout, StringComparison.Ordinal);
                Assert.Contains(historyKey + "0", stdout, StringComparison.Ordinal);
            }

            int observedHistoryCount = ReadRequiredInt(stdout, "HISTORY_COUNT=");
            Assert.Equal(0, observedHistoryCount);
            string round2 = ReadRequiredLine(stdout, "ROUND2_SHA256=");
            Assert.Equal(round1, round2);
            Console.WriteLine("ROUND2_SHA256=" + round2);
            Console.WriteLine("CHILD_PID=" + childPid.ToString(CultureInfo.InvariantCulture));
        }
        finally
        {
            try
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static int ReadRequiredInt(string stdout, string prefix)
    {
        string line = ReadRequiredLine(stdout, prefix);
        return int.Parse(line, CultureInfo.InvariantCulture);
    }

    private static string ReadRequiredLine(string stdout, string prefix)
    {
        using var reader = new StringReader(stdout);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
                return line.Substring(prefix.Length);
        }

        throw new InvalidOperationException("missing output prefix " + prefix + " in:\n" + stdout);
    }
}
