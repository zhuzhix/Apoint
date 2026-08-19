using AStockMonitor.Application.Market;
using AStockMonitor.Contracts.Protos;
using AStockMonitor.Domain.Market;
using AStockMonitor.Infrastructure.Observability;
using AStockMonitor.Infrastructure.Configuration;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace AStockMonitor.Api.Services;

public sealed class MarketIngestGrpcService(
    MarketEventProcessor processor,
    IReliableTickPublisher reliablePublisher,
    IOfficialBarBatchWriter officialBarBatchWriter,
    CollectorGatewayRequestAuthenticator authenticator,
    MarketRuntimeState runtimeState,
    MarketOptions marketOptions,
    ILogger<MarketIngestGrpcService> logger) : MarketIngest.MarketIngestBase
{
    public override async Task Connect(
        IAsyncStreamReader<CollectorMessage> requestStream,
        IServerStreamWriter<ServerMessage> responseStream,
        ServerCallContext context)
    {
        string? workerId = null;
        var source = "unknown";
        var accepted = 0L;
        var duplicates = 0L;
        var rejected = 0L;

        try
        {
            authenticator.Require(context.RequestHeaders);
            await foreach (var message in requestStream.ReadAllAsync(context.CancellationToken))
            {
                switch (message.PayloadCase)
                {
                    case CollectorMessage.PayloadOneofCase.Handshake:
                        workerId = message.Handshake.WorkerId;
                        source = string.IsNullOrWhiteSpace(message.Handshake.Source)
                            ? "dongcai-gm"
                            : message.Handshake.Source;
                        runtimeState.RecordCollectorHandshake(
                            workerId,
                            source,
                            message.Handshake.AssignmentVersion);

                        await responseStream.WriteAsync(new ServerMessage
                        {
                            Ack = new IngestAck { WorkerId = workerId }
                        });
                        logger.LogInformation(
                            "Collector connected. WorkerId={WorkerId}, Source={Source}, Assignment={Assignment}",
                            workerId,
                            source,
                            message.Handshake.AssignmentVersion);
                        break;

                    case CollectorMessage.PayloadOneofCase.Tick:
                        if (workerId is null)
                        {
                            rejected++;
                            runtimeState.RecordRejected();
                            continue;
                        }

                        runtimeState.RecordCollectorTick(workerId);
                        var tick = ToDomain(message.Tick, source, workerId);
                        if (string.IsNullOrWhiteSpace(tick.EventId) || string.IsNullOrWhiteSpace(tick.Symbol))
                        {
                            rejected++;
                            runtimeState.RecordRejected();
                            await responseStream.WriteAsync(new ServerMessage
                            {
                                Ack = new IngestAck
                                {
                                    WorkerId = workerId,
                                    EventId = tick.EventId,
                                    WorkerSequence = tick.WorkerSequence,
                                    Stage = AckStage.Rejected,
                                    Reason = "event_id and symbol are required",
                                    AcceptedCount = accepted,
                                    DuplicateCount = duplicates,
                                    RejectedCount = rejected
                                }
                            });
                            break;
                        }

                        if (IsFresh(tick, marketOptions.TickMaxAgeSeconds) &&
                            processor.TryProcess(tick))
                        {
                            accepted++;
                            runtimeState.RecordAccepted(tick.ReceiveTime);
                            AStockObservability.RecordIngest(
                                "accepted",
                                (DateTimeOffset.UtcNow - tick.ReceiveTime).TotalMilliseconds);
                        }
                        else
                        {
                            duplicates++;
                            runtimeState.RecordDuplicate();
                            AStockObservability.RecordIngest("duplicate", 0);
                        }

                        // A duplicate is still appended. The previous attempt
                        // may have updated L0 and then failed before Redis XADD.
                        // MySQL's unique event key makes this replay idempotent.
                        var receipt = await reliablePublisher.PublishAsync(tick, context.CancellationToken);
                        await responseStream.WriteAsync(new ServerMessage
                        {
                            Ack = new IngestAck
                            {
                                WorkerId = workerId,
                                EventId = tick.EventId,
                                WorkerSequence = tick.WorkerSequence,
                                Stage = receipt.Appended ? AckStage.StreamAppended : AckStage.Accepted,
                                StreamId = receipt.StreamId ?? string.Empty,
                                Reason = receipt.Reason ?? string.Empty,
                                AcceptedCount = accepted,
                                DuplicateCount = duplicates,
                                RejectedCount = rejected
                            }
                        });
                        break;

                    case CollectorMessage.PayloadOneofCase.TickBatch:
                        if (workerId is null)
                        {
                            rejected += message.TickBatch.Ticks.Count;
                            runtimeState.RecordRejected();
                            continue;
                        }

                        var sourceBatch = message.TickBatch;
                        if (string.IsNullOrWhiteSpace(sourceBatch.BatchId) ||
                            sourceBatch.Ticks.Count == 0 ||
                            (!string.IsNullOrWhiteSpace(sourceBatch.WorkerId) &&
                             !sourceBatch.WorkerId.Equals(workerId, StringComparison.Ordinal)))
                        {
                            rejected += sourceBatch.Ticks.Count;
                            runtimeState.RecordRejected();
                            await responseStream.WriteAsync(new ServerMessage
                            {
                                TickBatchAck = new TickBatchAck
                                {
                                    BatchId = sourceBatch.BatchId,
                                    ShardId = sourceBatch.ShardId,
                                    RejectedCount = sourceBatch.Ticks.Count,
                                    Stage = AckStage.Rejected,
                                    Reason = "batch_id, ticks and matching worker_id are required"
                                }
                            });
                            break;
                        }

                        var ticks = sourceBatch.Ticks
                            .Select(sourceTick => ToDomain(sourceTick, source, workerId))
                            .ToArray();
                        if (ticks.Any(tick =>
                                string.IsNullOrWhiteSpace(tick.EventId) ||
                                string.IsNullOrWhiteSpace(tick.Symbol)))
                        {
                            rejected += ticks.Length;
                            runtimeState.RecordRejected();
                            await responseStream.WriteAsync(new ServerMessage
                            {
                                TickBatchAck = new TickBatchAck
                                {
                                    BatchId = sourceBatch.BatchId,
                                    ShardId = sourceBatch.ShardId,
                                    RejectedCount = ticks.Length,
                                    Stage = AckStage.Rejected,
                                    Reason = "every Tick requires event_id and symbol"
                                }
                            });
                            break;
                        }

                        foreach (var tickItem in ticks)
                        {
                            runtimeState.RecordCollectorTick(workerId);
                            if (IsFresh(tickItem, marketOptions.TickMaxAgeSeconds))
                                processor.TryProcess(tickItem);
                        }

                        var batchReceipt = await reliablePublisher.PublishBatchAsync(
                            new TickPublishBatch(
                                sourceBatch.BatchId,
                                sourceBatch.ShardId,
                                ticks),
                            context.CancellationToken);
                        if (batchReceipt.Appended)
                        {
                            accepted += batchReceipt.AcceptedCount;
                            duplicates += batchReceipt.DuplicateCount;
                            rejected += batchReceipt.RejectedCount;
                            runtimeState.RecordAccepted(
                                batchReceipt.AcceptedCount,
                                ticks.Max(static tick => tick.ReceiveTime));
                            runtimeState.RecordDuplicate(batchReceipt.DuplicateCount);
                            runtimeState.RecordRejected(batchReceipt.RejectedCount);
                            AStockObservability.RecordIngestBatch(
                                batchReceipt.AcceptedCount,
                                batchReceipt.DuplicateCount,
                                batchReceipt.ExpiredCount,
                                batchReceipt.RejectedCount,
                                ticks.Max(static tick =>
                                    (DateTimeOffset.UtcNow - tick.ReceiveTime).TotalMilliseconds));
                        }
                        else
                        {
                            rejected += ticks.Length;
                            runtimeState.RecordRejected(ticks.Length);
                        }

                        await responseStream.WriteAsync(new ServerMessage
                        {
                            TickBatchAck = new TickBatchAck
                            {
                                BatchId = sourceBatch.BatchId,
                                ShardId = sourceBatch.ShardId,
                                AcceptedCount = batchReceipt.AcceptedCount,
                                DuplicateCount = batchReceipt.DuplicateCount,
                                ExpiredCount = batchReceipt.ExpiredCount,
                                RejectedCount = batchReceipt.Appended
                                    ? batchReceipt.RejectedCount
                                    : ticks.Length,
                                RedisLastId = batchReceipt.LastStreamId ?? string.Empty,
                                Stage = batchReceipt.Appended
                                    ? AckStage.StreamAppended
                                    : AckStage.Rejected,
                                Reason = batchReceipt.Reason ?? string.Empty
                            }
                        });
                        break;

                    case CollectorMessage.PayloadOneofCase.OfficialBar:
                        rejected++;
                        await responseStream.WriteAsync(new ServerMessage
                        {
                            Ack = new IngestAck
                            {
                                WorkerId = workerId ?? string.Empty,
                                EventId = message.OfficialBar.EventId,
                                Stage = AckStage.Rejected,
                                Reason = "official_bar is retired; use official_bar_batch through CollectorGateway",
                                AcceptedCount = accepted,
                                DuplicateCount = duplicates,
                                RejectedCount = rejected
                            }
                        });
                        break;

                    case CollectorMessage.PayloadOneofCase.OfficialBarBatch:
                        var batch = message.OfficialBarBatch;
                        if (workerId is null || string.IsNullOrWhiteSpace(batch.CommandId) ||
                            string.IsNullOrWhiteSpace(batch.BatchId) ||
                            string.IsNullOrWhiteSpace(batch.GatewayId) ||
                            !batch.WorkerId.Equals(workerId, StringComparison.Ordinal) ||
                            batch.RecoveryItemId <= 0 || batch.Bars.Count == 0 ||
                            !Guid.TryParse(batch.CommandId, out var commandId) ||
                            !Guid.TryParse(batch.BatchId, out var batchId))
                        {
                            rejected += batch.Bars.Count;
                            await responseStream.WriteAsync(new ServerMessage
                            {
                                OfficialBarBatchAck = new OfficialBarBatchAck
                                {
                                    CommandId = batch.CommandId,
                                    BatchId = batch.BatchId,
                                    RejectedCount = batch.Bars.Count,
                                    Stage = AckStage.Rejected,
                                    Reason = "command, batch, gateway, worker, recovery item and bars are required"
                                }
                            });
                            break;
                        }
                        try
                        {
                            var result = await officialBarBatchWriter.WriteAsync(new OfficialBarBatchInput(
                                commandId, batchId, batch.GatewayId, workerId, batch.RecoveryItemId,
                                batch.Bars.Select(item => ToCanonical(item, source)).ToArray()),
                                context.CancellationToken);
                            accepted += result.AcceptedCount;
                            duplicates += result.DuplicateCount;
                            rejected += result.RejectedCount;
                            await responseStream.WriteAsync(new ServerMessage
                            {
                                OfficialBarBatchAck = new OfficialBarBatchAck
                                {
                                    CommandId = batch.CommandId,
                                    BatchId = batch.BatchId,
                                    AcceptedCount = result.AcceptedCount,
                                    DuplicateCount = result.DuplicateCount,
                                    RejectedCount = result.RejectedCount,
                                    Stage = result.Applied ? AckStage.StreamAppended : AckStage.Rejected,
                                    Reason = result.Reason ?? string.Empty
                                }
                            });
                        }
                        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
                        {
                            rejected += batch.Bars.Count;
                            await responseStream.WriteAsync(new ServerMessage
                            {
                                OfficialBarBatchAck = new OfficialBarBatchAck
                                {
                                    CommandId = batch.CommandId,
                                    BatchId = batch.BatchId,
                                    RejectedCount = batch.Bars.Count,
                                    Stage = AckStage.Rejected,
                                    Reason = exception.Message
                                }
                            });
                        }
                        break;

                    case CollectorMessage.PayloadOneofCase.Heartbeat:
                        runtimeState.RecordCollectorHeartbeat(
                            message.Heartbeat.WorkerId,
                            message.Heartbeat.QueueDepth,
                            message.Heartbeat.ReceivedCount,
                            message.Heartbeat.PublishedCount,
                            message.Heartbeat.OutboxPendingCount,
                            message.Heartbeat.OutboxSizeBytes,
                            message.Heartbeat.FailedCount);
                        logger.LogDebug(
                            "Collector heartbeat. WorkerId={WorkerId}, QueueDepth={QueueDepth}, Received={Received}",
                            message.Heartbeat.WorkerId,
                            message.Heartbeat.QueueDepth,
                            message.Heartbeat.ReceivedCount);
                        await responseStream.WriteAsync(new ServerMessage
                        {
                            Ack = new IngestAck
                            {
                                WorkerId = message.Heartbeat.WorkerId,
                                AcceptedCount = runtimeState.AcceptedCount,
                                DuplicateCount = runtimeState.DuplicateCount,
                                RejectedCount = runtimeState.RejectedCount
                            }
                        });
                        break;

                    case CollectorMessage.PayloadOneofCase.Metric:
                        runtimeState.RecordCollectorMetric(
                            message.Metric.WorkerId,
                            message.Metric.CpuPercent,
                            message.Metric.MemoryBytes,
                            message.Metric.QueueDepth);
                        break;
                }
            }
        }
        finally
        {
            if (workerId is not null)
            {
                runtimeState.RecordCollectorDisconnected(workerId);
            }
        }

        logger.LogWarning(
            "Collector disconnected. WorkerId={WorkerId}, Accepted={Accepted}, Duplicates={Duplicates}, Rejected={Rejected}",
            workerId,
            accepted,
            duplicates,
            rejected);
    }

    private static AStockMonitor.Domain.Market.TickEvent ToDomain(
        Contracts.Protos.TickEvent sourceTick,
        string source,
        string workerId)
    {
        var eventTime = ToDateTimeOffset(sourceTick.EventTime);
        var receiveTime = ToDateTimeOffset(sourceTick.ReceiveTime);

        return new AStockMonitor.Domain.Market.TickEvent(
            sourceTick.EventId,
            sourceTick.Symbol.Trim().ToUpperInvariant(),
            eventTime,
            receiveTime,
            sourceTick.PriceE6 / 1_000_000m,
            sourceTick.PreCloseE6 == 0 ? null : sourceTick.PreCloseE6 / 1_000_000m,
            sourceTick.CumulativeVolume == 0 ? null : sourceTick.CumulativeVolume,
            sourceTick.CumulativeAmountE4 == 0 ? null : sourceTick.CumulativeAmountE4 / 10_000m,
            sourceTick.LastVolume == 0 ? null : sourceTick.LastVolume,
            sourceTick.LastAmountE4 == 0 ? null : sourceTick.LastAmountE4 / 10_000m,
            sourceTick.BidPrice1E6 == 0 ? null : sourceTick.BidPrice1E6 / 1_000_000m,
            sourceTick.BidVolume1 == 0 ? null : sourceTick.BidVolume1,
            sourceTick.AskPrice1E6 == 0 ? null : sourceTick.AskPrice1E6 / 1_000_000m,
            sourceTick.AskVolume1 == 0 ? null : sourceTick.AskVolume1,
            source,
            workerId,
            sourceTick.SessionId,
            sourceTick.WorkerSequence,
            DateTimeOffset.UtcNow,
            string.IsNullOrWhiteSpace(sourceTick.CollectionMode)
                ? "REALTIME_SUBSCRIPTION"
                : sourceTick.CollectionMode.Trim().ToUpperInvariant(),
            sourceTick.SourcePriority <= 0 ? 300 : sourceTick.SourcePriority);
    }

    private static DateTimeOffset ToDateTimeOffset(Timestamp timestamp)
    {
        return timestamp is null || (timestamp.Seconds == 0 && timestamp.Nanos == 0)
            ? DateTimeOffset.UtcNow
            : timestamp.ToDateTimeOffset();
    }

    private static bool IsFresh(
        AStockMonitor.Domain.Market.TickEvent tick,
        int maxAgeSeconds)
    {
        var now = DateTimeOffset.UtcNow;
        var cutoff = now.AddSeconds(-Math.Max(10, maxAgeSeconds));
        var futureLimit = now.AddSeconds(30);
        return tick.ReceiveTime >= cutoff && tick.ReceiveTime <= futureLimit &&
               tick.EventTime >= cutoff && tick.EventTime <= futureLimit;
    }

    private static CanonicalBarInput ToCanonical(
        OfficialBarEnvelope sourceBar,
        string handshakeSource)
    {
        var bob = ToDateTimeOffset(sourceBar.Bob);
        var eob = ToDateTimeOffset(sourceBar.Eob);
        var tradingDate = DateOnly.TryParse(sourceBar.TradingDate, out var parsedDate)
            ? parsedDate
            : ChinaMarketSession.TradingDate(eob);
        return new CanonicalBarInput(
            sourceBar.EventId,
            sourceBar.Symbol,
            sourceBar.Frequency,
            tradingDate,
            bob,
            eob,
            sourceBar.OpenPriceE6 / 1_000_000m,
            sourceBar.HighPriceE6 / 1_000_000m,
            sourceBar.LowPriceE6 / 1_000_000m,
            sourceBar.ClosePriceE6 / 1_000_000m,
            sourceBar.PreCloseE6 == 0 ? null : sourceBar.PreCloseE6 / 1_000_000m,
            sourceBar.Volume,
            sourceBar.AmountE4 / 10_000m,
            sourceBar.IsClosed,
            ToDateTimeOffset(sourceBar.SourceUpdatedAt),
            string.IsNullOrWhiteSpace(sourceBar.Source) ? handshakeSource : sourceBar.Source,
            sourceBar.RowHash,
            string.IsNullOrWhiteSpace(sourceBar.CollectionMode)
                ? "live"
                : sourceBar.CollectionMode);
    }
}
