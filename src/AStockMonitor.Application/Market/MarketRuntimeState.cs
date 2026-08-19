using System.Collections.Concurrent;

namespace AStockMonitor.Application.Market;

/// <summary>
/// 保存当前 API 进程的行情接收计数和采集 Worker 状态；所有读写均支持并发调用。
/// </summary>
public sealed class MarketRuntimeState
{
    private readonly ConcurrentDictionary<string, CollectorRuntimeStatus> _collectors = new(StringComparer.OrdinalIgnoreCase);
    private long _acceptedCount;
    private long _duplicateCount;
    private long _rejectedCount;
    private long _lastReceiveUnixMilliseconds;
    private long _lastIngestUnixMilliseconds;

    public long AcceptedCount => Interlocked.Read(ref _acceptedCount);
    public long DuplicateCount => Interlocked.Read(ref _duplicateCount);
    public long RejectedCount => Interlocked.Read(ref _rejectedCount);

    public DateTimeOffset? LastReceiveTime
    {
        get
        {
            var value = Interlocked.Read(ref _lastReceiveUnixMilliseconds);
            return value == 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(value);
        }
    }

    public DateTimeOffset? LastIngestTime
    {
        get
        {
            var value = Interlocked.Read(ref _lastIngestUnixMilliseconds);
            return value == 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(value);
        }
    }

    public void RecordAccepted(DateTimeOffset receiveTime)
    {
        Interlocked.Increment(ref _acceptedCount);
        Interlocked.Exchange(ref _lastReceiveUnixMilliseconds, receiveTime.ToUnixTimeMilliseconds());
        Interlocked.Exchange(ref _lastIngestUnixMilliseconds, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public void RecordAccepted(long count, DateTimeOffset receiveTime)
    {
        if (count <= 0)
            return;
        Interlocked.Add(ref _acceptedCount, count);
        Interlocked.Exchange(ref _lastReceiveUnixMilliseconds, receiveTime.ToUnixTimeMilliseconds());
        Interlocked.Exchange(ref _lastIngestUnixMilliseconds, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public void RecordDuplicate() => Interlocked.Increment(ref _duplicateCount);
    public void RecordDuplicate(long count)
    {
        if (count > 0)
            Interlocked.Add(ref _duplicateCount, count);
    }
    public void RecordRejected() => Interlocked.Increment(ref _rejectedCount);
    public void RecordRejected(long count)
    {
        if (count > 0)
            Interlocked.Add(ref _rejectedCount, count);
    }

    /// <summary>记录采集 Worker 建立 gRPC 会话并声明数据源和分片版本。</summary>
    public void RecordCollectorHandshake(string workerId, string source, string assignmentVersion)
    {
        var status = _collectors.GetOrAdd(workerId, static id => new CollectorRuntimeStatus(id));
        status.Handshake(source, assignmentVersion);
    }

    public void RecordCollectorHeartbeat(
        string workerId,
        long queueDepth,
        long receivedCount,
        long publishedCount,
        long outboxPendingCount = 0,
        long outboxSizeBytes = 0,
        long failedCount = 0)
    {
        var status = _collectors.GetOrAdd(workerId, static id => new CollectorRuntimeStatus(id));
        status.Heartbeat(
            queueDepth,
            receivedCount,
            publishedCount,
            outboxPendingCount,
            outboxSizeBytes,
            failedCount);
    }

    public void RecordCollectorMetric(
        string workerId,
        double cpuPercent,
        long memoryBytes,
        long queueDepth)
    {
        var status = _collectors.GetOrAdd(workerId, static id => new CollectorRuntimeStatus(id));
        status.Metric(cpuPercent, memoryBytes, queueDepth);
    }

    public void RecordCollectorTick(string workerId)
    {
        if (_collectors.TryGetValue(workerId, out var status))
        {
            status.Touch();
        }
    }

    public void RecordCollectorDisconnected(string workerId)
    {
        if (_collectors.TryGetValue(workerId, out var status))
        {
            status.Disconnect();
        }
    }

    /// <summary>获取按 Worker ID 排序的不可变运行快照。</summary>
    public IReadOnlyCollection<CollectorRuntimeSnapshot> GetCollectors() =>
        _collectors.Values
            .Select(static status => status.Snapshot())
            .OrderBy(static status => status.WorkerId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
}

/// <summary>单个 Python 采集 Worker 的最新运行状态。</summary>
public sealed record CollectorRuntimeSnapshot(
    string WorkerId,
    string Source,
    string AssignmentVersion,
    bool Connected,
    DateTimeOffset ConnectedAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset? LastHeartbeatAt,
    long QueueDepth,
    long ReceivedCount,
    long PublishedCount,
    long OutboxPendingCount,
    long OutboxSizeBytes,
    long FailedCount,
    double CpuPercent,
    long MemoryBytes);

internal sealed class CollectorRuntimeStatus(string workerId)
{
    private readonly object _sync = new();
    private readonly string _workerId = workerId;
    private string _source = "unknown";
    private string _assignmentVersion = "unknown";
    private bool _connected;
    private int _connectionCount;
    private DateTimeOffset _connectedAt = DateTimeOffset.UtcNow;
    private long _lastSeenUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private DateTimeOffset? _lastHeartbeatAt;
    private long _queueDepth;
    private long _receivedCount;
    private long _publishedCount;
    private long _outboxPendingCount;
    private long _outboxSizeBytes;
    private long _failedCount;
    private double _cpuPercent;
    private long _memoryBytes;

    public void Handshake(string source, string assignmentVersion)
    {
        lock (_sync)
        {
            _source = source;
            _assignmentVersion = assignmentVersion;
            if (_connectionCount == 0)
                _connectedAt = DateTimeOffset.UtcNow;
            _connectionCount++;
            _connected = true;
        }

        Touch();
    }

    public void Heartbeat(
        long queueDepth,
        long receivedCount,
        long publishedCount,
        long outboxPendingCount,
        long outboxSizeBytes,
        long failedCount)
    {
        lock (_sync)
        {
            _queueDepth = queueDepth;
            _receivedCount = receivedCount;
            _publishedCount = publishedCount;
            _outboxPendingCount = outboxPendingCount;
            _outboxSizeBytes = outboxSizeBytes;
            _failedCount = failedCount;
            _lastHeartbeatAt = DateTimeOffset.UtcNow;
            _connected = true;
        }

        Touch();
    }

    public void Metric(double cpuPercent, long memoryBytes, long queueDepth)
    {
        lock (_sync)
        {
            _cpuPercent = cpuPercent;
            _memoryBytes = memoryBytes;
            _queueDepth = queueDepth;
            _connected = true;
        }

        Touch();
    }

    public void Touch() => Interlocked.Exchange(
        ref _lastSeenUnixMilliseconds,
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    public void Disconnect()
    {
        lock (_sync)
        {
            _connectionCount = Math.Max(0, _connectionCount - 1);
            _connected = _connectionCount > 0;
        }
    }

    public CollectorRuntimeSnapshot Snapshot()
    {
        lock (_sync)
        {
            return new CollectorRuntimeSnapshot(
                _workerId,
                _source,
                _assignmentVersion,
                _connected,
                _connectedAt,
                DateTimeOffset.FromUnixTimeMilliseconds(Interlocked.Read(ref _lastSeenUnixMilliseconds)),
                _lastHeartbeatAt,
                _queueDepth,
                _receivedCount,
                _publishedCount,
                _outboxPendingCount,
                _outboxSizeBytes,
                _failedCount,
                _cpuPercent,
                _memoryBytes);
        }
    }
}
