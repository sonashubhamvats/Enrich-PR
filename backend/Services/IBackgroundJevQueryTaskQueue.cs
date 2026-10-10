using System.Threading.Channels;

namespace PrPrioritizerService.Services;

public interface IBackgroundJevQueryTaskQueue
{
    ValueTask QueueBackgroundWorkItemAsync(string jobId, CancellationToken cancellationToken = default);
    ValueTask<string> DequeueAsync(CancellationToken cancellationToken);
}

public class BackgroundJevQueryTaskQueue : IBackgroundJevQueryTaskQueue
{
    private readonly Channel<string> _queue;

    public BackgroundJevQueryTaskQueue(int capacity = 100)
    {
        var options = new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait
        };
        _queue = Channel.CreateBounded<string>(options);
    }

    public async ValueTask QueueBackgroundWorkItemAsync(string jobId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            throw new ArgumentNullException(nameof(jobId));
        }

        await _queue.Writer.WriteAsync(jobId, cancellationToken);
    }

    public async ValueTask<string> DequeueAsync(CancellationToken cancellationToken)
    {
        return await _queue.Reader.ReadAsync(cancellationToken);
    }
}
