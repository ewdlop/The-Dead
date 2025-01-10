# The-Dead

## Dead Man Swtich

```csharp
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace DeadManSwitch
{
    public enum SwitchState
    {
        Active,         // Normal operation
        Warning,        // Missing heartbeat
        Critical,       // Near timeout
        Triggered,      // Switch activated
        Maintenance,    // Under maintenance
        Disabled,       // Switched off
        Error          // System error
    }

    public class HeartbeatEvent
    {
        public DateTime Timestamp { get; set; }
        public TimeSpan TimeSinceLastHeartbeat { get; set; }
        public string Source { get; set; }
        public SwitchState State { get; set; }
    }

    public class DeadMansSwitch : IDisposable
    {
        private readonly TimeSpan _warningThreshold;
        private readonly TimeSpan _criticalThreshold;
        private readonly TimeSpan _timeout;
        private readonly Action _emergencyAction;
        private readonly ConcurrentDictionary<string, DateTime> _lastHeartbeats;
        private readonly ConcurrentQueue<HeartbeatEvent> _eventHistory;
        private readonly CancellationTokenSource _cancellation;
        private Timer _watchdogTimer;
        private Timer _healthCheckTimer;
        private volatile bool _isDisposed;
        private readonly object _stateLock = new object();
        private SwitchState _currentState = SwitchState.Active;
        private readonly int _maxHistorySize = 1000;

        public event EventHandler<HeartbeatEvent> OnStateChange;
        public event EventHandler<HeartbeatEvent> OnHeartbeat;
        public event EventHandler<HeartbeatEvent> OnEmergencyAction;

        public DeadMansSwitch(
            TimeSpan warningThreshold,
            TimeSpan criticalThreshold,
            TimeSpan timeout,
            Action emergencyAction)
        {
            _warningThreshold = warningThreshold;
            _criticalThreshold = criticalThreshold;
            _timeout = timeout;
            _emergencyAction = emergencyAction;
            _lastHeartbeats = new ConcurrentDictionary<string, DateTime>();
            _eventHistory = new ConcurrentQueue<HeartbeatEvent>();
            _cancellation = new CancellationTokenSource();

            ValidateConfiguration();
            InitializeTimers();
            StartHealthMonitoring();
        }

        private void ValidateConfiguration()
        {
            if (_warningThreshold >= _criticalThreshold)
                throw new ArgumentException("Warning threshold must be less than critical threshold");
            if (_criticalThreshold >= _timeout)
                throw new ArgumentException("Critical threshold must be less than timeout");
        }

        private void InitializeTimers()
        {
            // Watchdog timer checks system state every second
            _watchdogTimer = new Timer(
                CheckState,
                null,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1)
            );

            // Health check timer runs every minute
            _healthCheckTimer = new Timer(
                PerformHealthCheck,
                null,
                TimeSpan.FromMinutes(1),
                TimeSpan.FromMinutes(1)
            );
        }

        private void StartHealthMonitoring()
        {
            Task.Run(async () =>
            {
                while (!_cancellation.Token.IsCancellationRequested)
                {
                    MonitorSystemResources();
                    await Task.Delay(TimeSpan.FromSeconds(30));
                }
            }, _cancellation.Token);
        }

        private void MonitorSystemResources()
        {
            var process = Process.GetCurrentProcess();
            var memoryUsage = process.WorkingSet64;
            var cpuTime = process.TotalProcessorTime;

            // Log or act on resource usage
            if (memoryUsage > 1_000_000_000) // 1GB
            {
                LogEvent(new HeartbeatEvent
                {
                    Timestamp = DateTime.UtcNow,
                    Source = "ResourceMonitor",
                    State = _currentState,
                    TimeSinceLastHeartbeat = GetTimeSinceLastHeartbeat()
                });
            }
        }

        public void SendHeartbeat(string source = "default")
        {
            if (_isDisposed) return;

            _lastHeartbeats.AddOrUpdate(
                source,
                DateTime.UtcNow,
                (_, __) => DateTime.UtcNow
            );

            var heartbeatEvent = new HeartbeatEvent
            {
                Timestamp = DateTime.UtcNow,
                Source = source,
                State = _currentState,
                TimeSinceLastHeartbeat = TimeSpan.Zero
            };

            LogEvent(heartbeatEvent);
            OnHeartbeat?.Invoke(this, heartbeatEvent);
        }

        private void CheckState(object state)
        {
            if (_isDisposed) return;

            var timeSinceLastHeartbeat = GetTimeSinceLastHeartbeat();
            var newState = DetermineState(timeSinceLastHeartbeat);

            if (newState != _currentState)
            {
                UpdateState(newState, timeSinceLastHeartbeat);
            }
        }

        private TimeSpan GetTimeSinceLastHeartbeat()
        {
            if (!_lastHeartbeats.Any())
                return TimeSpan.MaxValue;

            var mostRecentHeartbeat = _lastHeartbeats.Values.Max();
            return DateTime.UtcNow - mostRecentHeartbeat;
        }

        private SwitchState DetermineState(TimeSpan timeSinceLastHeartbeat)
        {
            if (_currentState == SwitchState.Maintenance || 
                _currentState == SwitchState.Disabled)
                return _currentState;

            if (timeSinceLastHeartbeat >= _timeout)
                return SwitchState.Triggered;
            if (timeSinceLastHeartbeat >= _criticalThreshold)
                return SwitchState.Critical;
            if (timeSinceLastHeartbeat >= _warningThreshold)
                return SwitchState.Warning;

            return SwitchState.Active;
        }

        private void UpdateState(SwitchState newState, TimeSpan timeSinceLastHeartbeat)
        {
            lock (_stateLock)
            {
                if (_currentState == newState) return;

                var previousState = _currentState;
                _currentState = newState;

                var stateChangeEvent = new HeartbeatEvent
                {
                    Timestamp = DateTime.UtcNow,
                    Source = "StateManager",
                    State = newState,
                    TimeSinceLastHeartbeat = timeSinceLastHeartbeat
                };

                LogEvent(stateChangeEvent);
                OnStateChange?.Invoke(this, stateChangeEvent);

                if (newState == SwitchState.Triggered)
                {
                    TriggerEmergencyAction(timeSinceLastHeartbeat);
                }
            }
        }

        private void TriggerEmergencyAction(TimeSpan timeSinceLastHeartbeat)
        {
            try
            {
                _emergencyAction?.Invoke();

                var emergencyEvent = new HeartbeatEvent
                {
                    Timestamp = DateTime.UtcNow,
                    Source = "EmergencyAction",
                    State = SwitchState.Triggered,
                    TimeSinceLastHeartbeat = timeSinceLastHeartbeat
                };

                LogEvent(emergencyEvent);
                OnEmergencyAction?.Invoke(this, emergencyEvent);
            }
            catch (Exception ex)
            {
                // Log emergency action failure
                LogEvent(new HeartbeatEvent
                {
                    Timestamp = DateTime.UtcNow,
                    Source = "Error",
                    State = SwitchState.Error,
                    TimeSinceLastHeartbeat = timeSinceLastHeartbeat
                });
            }
        }

        private void PerformHealthCheck(object state)
        {
            if (_isDisposed) return;

            var healthCheckEvent = new HeartbeatEvent
            {
                Timestamp = DateTime.UtcNow,
                Source = "HealthCheck",
                State = _currentState,
                TimeSinceLastHeartbeat = GetTimeSinceLastHeartbeat()
            };

            LogEvent(healthCheckEvent);
        }

        private void LogEvent(HeartbeatEvent evt)
        {
            _eventHistory.Enqueue(evt);

            // Trim history if needed
            while (_eventHistory.Count > _maxHistorySize)
            {
                _eventHistory.TryDequeue(out _);
            }
        }

        public void EnterMaintenance()
        {
            UpdateState(SwitchState.Maintenance, GetTimeSinceLastHeartbeat());
        }

        public void ExitMaintenance()
        {
            UpdateState(SwitchState.Active, TimeSpan.Zero);
            SendHeartbeat("MaintenanceExit");
        }

        public HeartbeatEvent[] GetEventHistory()
        {
            return _eventHistory.ToArray();
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            _watchdogTimer?.Dispose();
            _healthCheckTimer?.Dispose();
            _cancellation.Cancel();
            _cancellation.Dispose();
        }
    }

    // Example usage
    class Program
    {
        static async Task Main()
        {
            using var deadManSwitch = new DeadMansSwitch(
                warningThreshold: TimeSpan.FromSeconds(5),
                criticalThreshold: TimeSpan.FromSeconds(8),
                timeout: TimeSpan.FromSeconds(10),
                emergencyAction: () => Console.WriteLine("🚨 Emergency procedures initiated!")
            );

            // Subscribe to events
            deadManSwitch.OnStateChange += (s, e) =>
                Console.WriteLine($"State changed to {e.State} at {e.Timestamp:HH:mm:ss}");

            deadManSwitch.OnHeartbeat += (s, e) =>
                Console.WriteLine($"Heartbeat received from {e.Source} at {e.Timestamp:HH:mm:ss}");

            deadManSwitch.OnEmergencyAction += (s, e) =>
                Console.WriteLine($"Emergency action triggered at {e.Timestamp:HH:mm:ss}");

            // Normal operation
            Console.WriteLine("Starting normal operation...");
            for (int i = 0; i < 3; i++)
            {
                deadManSwitch.SendHeartbeat("MainLoop");
                await Task.Delay(2000);
            }

            // Simulate maintenance
            Console.WriteLine("\nEntering maintenance...");
            deadManSwitch.EnterMaintenance();
            await Task.Delay(6000);
            deadManSwitch.ExitMaintenance();

            // Simulate system problem
            Console.WriteLine("\nSimulating system problem...");
            await Task.Delay(12000);

            // Display event history
            Console.WriteLine("\nEvent History:");
            foreach (var evt in deadManSwitch.GetEventHistory())
            {
                Console.WriteLine($"{evt.Timestamp:HH:mm:ss} - {evt.Source}: {evt.State}");
            }
        }
    }
}
```


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
