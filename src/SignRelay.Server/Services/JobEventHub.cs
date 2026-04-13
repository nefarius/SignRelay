using System.Collections.Concurrent;
using System.Threading.Channels;
using SignRelay.Contracts;

namespace SignRelay.Server.Services;

public sealed class JobEventHub
{
    private readonly ConcurrentDictionary<string, List<ChannelWriter<JobEventPayload>>> _writers = new();

    public JobSubscription Subscribe(string jobId)
    {
        var channel = Channel.CreateUnbounded<JobEventPayload>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        _writers.AddOrUpdate(
            jobId,
            _ => new List<ChannelWriter<JobEventPayload>> { channel.Writer },
            (_, list) =>
            {
                lock (list)
                {
                    list.Add(channel.Writer);
                }

                return list;
            });

        return new JobSubscription(channel.Reader, () => Unsubscribe(jobId, channel.Writer));
    }

    public void Unsubscribe(string jobId, ChannelWriter<JobEventPayload> writer)
    {
        if (!_writers.TryGetValue(jobId, out var list))
            return;

        lock (list)
        {
            list.Remove(writer);
            if (list.Count == 0)
                _writers.TryRemove(jobId, out _);
        }

        writer.TryComplete();
    }

    public void Publish(string jobId, JobEventPayload payload)
    {
        if (!_writers.TryGetValue(jobId, out var list))
            return;

        List<ChannelWriter<JobEventPayload>> snapshot;
        lock (list)
        {
            snapshot = list.ToList();
        }

        foreach (var w in snapshot)
            w.TryWrite(payload);
    }
}

public sealed record JobSubscription(ChannelReader<JobEventPayload> Reader, Action Dispose);
