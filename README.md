# The-Dead

## Dead Man Swtich

## Dead Letter

```csharp
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DeadLetterServiceBus
{
    public class Message
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Content { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? LastProcessedAt { get; set; }
        public int RetryCount { get; set; }
        public string ErrorDetails { get; set; }
        public IDictionary<string, object> Headers { get; set; } = new Dictionary<string, object>();
        public TimeSpan? TimeToLive { get; set; }
    }

    public class ProcessingResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public bool RequiresRetry { get; set; }
        public TimeSpan? RetryDelay { get; set; }
    }

    public class DeadLetterBus : IDisposable
    {
        private readonly ConcurrentQueue<Message> _mainQueue = new();
        private readonly ConcurrentQueue<Message> _deadLetterQueue = new();
        private readonly ConcurrentQueue<Message> _retryQueue = new();
        private readonly ConcurrentDictionary<string, Message> _inProcess = new();
        private readonly CancellationTokenSource _cancellation = new();
        private readonly int _maxRetries;
        private readonly TimeSpan _defaultTtl;
        private bool _isDisposed;

        // Event handlers
        public event EventHandler<Message> OnMessageDeadLettered;
        public event EventHandler<Message> OnMessageRetry;
        public event EventHandler<Message> OnMessageSuccess;

        public DeadLetterBus(int maxRetries = 3, TimeSpan? defaultTtl = null)
        {
            _maxRetries = maxRetries;
            _defaultTtl = defaultTtl ?? TimeSpan.FromHours(24);
            StartRetryProcessor();
        }

        private void StartRetryProcessor()
        {
            Task.Run(async () =>
            {
                while (!_cancellation.Token.IsCancellationRequested)
                {
                    if (_retryQueue.TryDequeue(out var message))
                    {
                        var currentTime = DateTime.UtcNow;
                        if (ShouldRetryMessage(message, currentTime))
                        {
                            OnMessageRetry?.Invoke(this, message);
                            _mainQueue.Enqueue(message);
                        }
                        else
                        {
                            MoveToDeadLetter(message, "Max retries exceeded or TTL expired");
                        }
                    }
                    await Task.Delay(100);
                }
            }, _cancellation.Token);
        }

        private bool ShouldRetryMessage(Message message, DateTime currentTime)
        {
            if (message.RetryCount >= _maxRetries)
                return false;

            if (message.TimeToLive.HasValue)
            {
                var messageAge = currentTime - message.CreatedAt;
                if (messageAge > message.TimeToLive.Value)
                    return false;
            }

            return true;
        }

        public void PublishMessage(string content, IDictionary<string, object> headers = null, TimeSpan? ttl = null)
        {
            var message = new Message
            {
                Content = content,
                TimeToLive = ttl ?? _defaultTtl
            };

            if (headers != null)
            {
                foreach (var header in headers)
                    message.Headers[header.Key] = header.Value;
            }

            _mainQueue.Enqueue(message);
        }

        public async Task StartProcessing(Func<Message, Task<ProcessingResult>> messageProcessor)
        {
            while (!_cancellation.Token.IsCancellationRequested)
            {
                try
                {
                    if (_mainQueue.TryDequeue(out var message))
                    {
                        if (_inProcess.TryAdd(message.Id, message))
                        {
                            await ProcessMessage(message, messageProcessor);
                        }
                    }
                    else
                    {
                        await Task.Delay(100);
                    }
                }
                catch (Exception ex)
                {
                    // Log error and continue processing
                    Console.WriteLine($"Error in message processing loop: {ex.Message}");
                }
            }
        }

        private async Task ProcessMessage(Message message, Func<Message, Task<ProcessingResult>> messageProcessor)
        {
            try
            {
                message.LastProcessedAt = DateTime.UtcNow;
                var result = await messageProcessor(message);

                if (result.Success)
                {
                    OnMessageSuccess?.Invoke(this, message);
                    _inProcess.TryRemove(message.Id, out _);
                }
                else
                {
                    HandleFailedMessage(message, result);
                }
            }
            catch (Exception ex)
            {
                HandleFailedMessage(message, new ProcessingResult
                {
                    Success = false,
                    Error = ex.Message,
                    RequiresRetry = true
                });
            }
        }

        private void HandleFailedMessage(Message message, ProcessingResult result)
        {
            message.RetryCount++;
            message.ErrorDetails = result.Error;

            if (result.RequiresRetry && message.RetryCount < _maxRetries)
            {
                if (result.RetryDelay.HasValue)
                {
                    Task.Delay(result.RetryDelay.Value)
                        .ContinueWith(_ => _retryQueue.Enqueue(message));
                }
                else
                {
                    _retryQueue.Enqueue(message);
                }
            }
            else
            {
                MoveToDeadLetter(message, result.Error);
            }

            _inProcess.TryRemove(message.Id, out _);
        }

        private void MoveToDeadLetter(Message message, string reason)
        {
            message.ErrorDetails = reason;
            _deadLetterQueue.Enqueue(message);
            OnMessageDeadLettered?.Invoke(this, message);
        }

        public async Task ProcessDeadLetters(Func<Message, Task<ProcessingResult>> deadLetterProcessor)
        {
            while (!_cancellation.Token.IsCancellationRequested && _deadLetterQueue.TryDequeue(out var message))
            {
                try
                {
                    var result = await deadLetterProcessor(message);
                    if (result.Success)
                    {
                        OnMessageSuccess?.Invoke(this, message);
                    }
                    else
                    {
                        // Log permanent failure
                        Console.WriteLine($"Permanent failure processing dead letter: {message.Id}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error processing dead letter {message.Id}: {ex.Message}");
                }
            }
        }

        public Task<IEnumerable<Message>> GetDeadLetters()
        {
            return Task.FromResult<IEnumerable<Message>>(_deadLetterQueue.ToArray());
        }

        public void Dispose()
        {
            if (_isDisposed) return;

            _cancellation.Cancel();
            _cancellation.Dispose();
            _isDisposed = true;
        }
    }

    // Example usage
    class Program
    {
        static async Task Main()
        {
            using var serviceBus = new DeadLetterBus(
                maxRetries: 3,
                defaultTtl: TimeSpan.FromMinutes(30)
            );

            // Subscribe to events
            serviceBus.OnMessageDeadLettered += (s, m) => 
                Console.WriteLine($"Message dead lettered: {m.Id}");
            
            serviceBus.OnMessageRetry += (s, m) => 
                Console.WriteLine($"Retrying message: {m.Id}, Attempt: {m.RetryCount}");
            
            serviceBus.OnMessageSuccess += (s, m) => 
                Console.WriteLine($"Message processed successfully: {m.Id}");

            // Start message processing
            _ = Task.Run(async () =>
            {
                await serviceBus.StartProcessing(async message =>
                {
                    // Simulate processing
                    await Task.Delay(100);

                    // Simulate random failures
                    if (Random.Shared.Next(2) == 0)
                    {
                        return new ProcessingResult
                        {
                            Success = false,
                            Error = "Random processing error",
                            RequiresRetry = true,
                            RetryDelay = TimeSpan.FromSeconds(1)
                        };
                    }

                    return new ProcessingResult { Success = true };
                });
            });

            // Publish some test messages
            for (int i = 0; i < 5; i++)
            {
                serviceBus.PublishMessage(
                    content: $"Test message {i}",
                    headers: new Dictionary<string, object>
                    {
                        ["priority"] = i,
                        ["timestamp"] = DateTime.UtcNow
                    }
                );

                await Task.Delay(500);
            }

            // Process dead letters after some time
            await Task.Delay(5000);
            await serviceBus.ProcessDeadLetters(async message =>
            {
                Console.WriteLine($"Processing dead letter: {message.Id}");
                await Task.Delay(100);
                return new ProcessingResult { Success = true };
            });

            // Wait for processing to complete
            await Task.Delay(2000);
        }
    }
}
```
