using System.Text.Json;

namespace GameRamCap;

/// <summary>
/// A durable lease for one working-set ceiling owned by GameRamCap. The process creation FILETIME
/// prevents PID reuse, and quota snapshots prevent us from undoing a change made by another tool.
/// </summary>
internal sealed class CeilingLeaseRecord
{
    public int Pid { get; set; }
    public long StartFileTimeUtc { get; set; }
    public QuotaSnapshot Original { get; set; } = new();
    public QuotaSnapshot Applied { get; set; } = new();
    // During an adaptive change both Applied and Pending are accepted. This makes a crash between
    // SetProcessWorkingSetSizeEx and committing the journal recoverable.
    public QuotaSnapshot? Pending { get; set; }
}

internal sealed class QuotaSnapshot
{
    public ulong Min { get; set; }
    public ulong Max { get; set; }
    public uint Flags { get; set; }

    public static QuotaSnapshot From(nuint min, nuint max, uint flags) =>
        new() { Min = (ulong)min, Max = (ulong)max, Flags = flags };

    public bool Matches(nuint min, nuint max, uint flags) =>
        Min == (ulong)min && Max == (ulong)max && Flags == flags;

    public QuotaSnapshot Copy() => new() { Min = Min, Max = Max, Flags = Flags };
}

/// <summary>
/// Cross-process, atomic journal shared by the main application and its watchdog. If the journal
/// cannot be read or written, GameRamCap fails closed and does not apply a new hard ceiling.
/// </summary>
internal static class CeilingJournal
{
    static readonly Mutex Sync = new(false, @"Local\GameRamCap.CeilingJournal.v1");
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    static string FilePath => Path.Combine(ConfigStore.Dir, "owned-ceilings.json");

    static T Locked<T>(Func<T> action)
    {
        bool held = false;
        try
        {
            try { held = Sync.WaitOne(TimeSpan.FromSeconds(10)); }
            catch (AbandonedMutexException) { held = true; }
            if (!held) throw new TimeoutException("Timed out waiting for the ceiling journal.");
            return action();
        }
        finally
        {
            if (held) Sync.ReleaseMutex();
        }
    }

    static List<CeilingLeaseRecord> ReadUnsafe()
    {
        if (!File.Exists(FilePath)) return new List<CeilingLeaseRecord>();
        return JsonSerializer.Deserialize<List<CeilingLeaseRecord>>(File.ReadAllText(FilePath), JsonOptions)
            ?? throw new InvalidDataException("The owned-ceilings journal is empty or invalid.");
    }

    static void WriteUnsafe(List<CeilingLeaseRecord> records)
    {
        Directory.CreateDirectory(ConfigStore.Dir);
        string temp = FilePath + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, records, JsonOptions);
                stream.Flush(true);
            }
            File.Move(temp, FilePath, true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    static CeilingLeaseRecord Copy(CeilingLeaseRecord x) => new()
    {
        Pid = x.Pid,
        StartFileTimeUtc = x.StartFileTimeUtc,
        Original = x.Original.Copy(),
        Applied = x.Applied.Copy(),
        Pending = x.Pending?.Copy(),
    };

    public static IReadOnlyList<CeilingLeaseRecord> ReadAll() => Locked(() =>
        (IReadOnlyList<CeilingLeaseRecord>)ReadUnsafe().Select(Copy).ToList());

    public static CeilingLeaseRecord? Find(int pid, long startFileTimeUtc) => Locked(() =>
        ReadUnsafe().FirstOrDefault(x => x.Pid == pid && x.StartFileTimeUtc == startFileTimeUtc) is { } found
            ? Copy(found) : null);

    public static void Add(CeilingLeaseRecord lease) => Locked(() =>
    {
        var records = ReadUnsafe();
        records.RemoveAll(x => x.Pid == lease.Pid && x.StartFileTimeUtc == lease.StartFileTimeUtc);
        records.Add(Copy(lease));
        WriteUnsafe(records);
        return true;
    });

    public static void BeginUpdate(int pid, long startFileTimeUtc, QuotaSnapshot next) => Locked(() =>
    {
        var records = ReadUnsafe();
        var lease = records.FirstOrDefault(x => x.Pid == pid && x.StartFileTimeUtc == startFileTimeUtc)
            ?? throw new InvalidOperationException($"Missing ownership record for pid {pid}.");
        if (lease.Pending != null)
            throw new InvalidOperationException($"A previous quota update is still pending for pid {pid}.");
        lease.Pending = next.Copy();
        WriteUnsafe(records);
        return true;
    });

    public static void CommitUpdate(int pid, long startFileTimeUtc) => Locked(() =>
    {
        var records = ReadUnsafe();
        var lease = records.FirstOrDefault(x => x.Pid == pid && x.StartFileTimeUtc == startFileTimeUtc)
            ?? throw new InvalidOperationException($"Missing ownership record for pid {pid}.");
        if (lease.Pending == null)
            throw new InvalidOperationException($"No pending quota update for pid {pid}.");
        lease.Applied = lease.Pending;
        lease.Pending = null;
        WriteUnsafe(records);
        return true;
    });

    public static void AbortUpdate(int pid, long startFileTimeUtc) => Locked(() =>
    {
        var records = ReadUnsafe();
        var lease = records.FirstOrDefault(x => x.Pid == pid && x.StartFileTimeUtc == startFileTimeUtc);
        if (lease != null && lease.Pending != null)
        {
            lease.Pending = null;
            WriteUnsafe(records);
        }
        return true;
    });

    public static void Remove(int pid, long startFileTimeUtc) => Locked(() =>
    {
        var records = ReadUnsafe();
        int removed = records.RemoveAll(x => x.Pid == pid && x.StartFileTimeUtc == startFileTimeUtc);
        if (removed > 0) WriteUnsafe(records);
        return true;
    });
}
