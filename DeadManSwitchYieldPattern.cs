//For the record, l learned this pattern from Unity 5.> and python

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DeadManYieldPattern
{
    public enum SwitchState
    {
        Active,
        Warning,
        Critical,
        Triggered,
        Maintenance,
        Disabled,
        Error
    }

    public class StateTransition
    {
        public SwitchState PreviousState { get; set; }
        public SwitchState CurrentState { get; set; }
        public DateTime Timestamp { get; set; }
        public TimeSpan TimeSinceLastHeartbeat { get; set; }
        public string Message { get; set; }
    }

    public class DeadManSwitch : IDisposable
    {
        private readonly TimeSpan _warningThreshold;
        private readonly TimeSpan _criticalThreshold;
        private readonly TimeSpan _timeout;
        private readonly Action _emergencyAction;
        private DateTime _lastHeartbeat;
        private SwitchState _currentState;
        private bool _isDisposed;
        private readonly CancellationTokenSource _cts;

        public DeadManSwitch(
            TimeSpan warningThreshold,
            TimeSpan criticalThreshold,
            TimeSpan timeout,
            Action emergencyAction)
        {
            _warningThreshold = warningThreshold;
            _criticalThreshold = criticalThreshold;
            _timeout = timeout;
            _emergencyAction = emergencyAction;
            _lastHeartbeat = DateTime.UtcNow;
            _currentState = SwitchState.Active;
            _cts = new CancellationTokenSource();
        }

        // Main state monitoring sequence
        public IEnumerable<StateTransition> MonitorState()
        {
            while (!_isDisposed)
            {
                var timeSinceHeartbeat = DateTime.UtcNow - _lastHeartbeat;
                var previousState = _currentState;
                var transition = new StateTransition
                {
                    PreviousState = previousState,
                    TimeSinceLastHeartbeat = timeSinceHeartbeat,
                    Timestamp = DateTime.UtcNow
                };

                // Determine new state based on time since last heartbeat
                _currentState = DetermineState(timeSinceHeartbeat);
                transition.CurrentState = _currentState;

                // Add contextual message
                transition.Message = GenerateStateMessage(_currentState, timeSinceHeartbeat);

                // Execute actions for the current state
                ExecuteStateAction(_currentState);

                yield return transition;
                Thread.Sleep(1000); // Check state every second
            }
        }

        // State history monitoring
        public IEnumerable<StateTransition> GetStateHistory(TimeSpan duration)
        {
            var startTime = DateTime.UtcNow;
            while (DateTime.UtcNow - startTime < duration && !_isDisposed)
            {
                var timeSinceHeartbeat = DateTime.UtcNow - _lastHeartbeat;
                var state = DetermineState(timeSinceHeartbeat);

                yield return new StateTransition
                {
                    CurrentState = state,
                    Timestamp = DateTime.UtcNow,
                    TimeSinceLastHeartbeat = timeSinceHeartbeat,
                    Message = $"Historical state at {DateTime.UtcNow}"
                };

                Thread.Sleep(1000);
            }
        }

        // Heartbeat monitoring sequence
        public IEnumerable<TimeSpan> MonitorHeartbeat()
        {
            while (!_isDisposed)
            {
                var timeSinceHeartbeat = DateTime.UtcNow - _lastHeartbeat;
                yield return timeSinceHeartbeat;
                Thread.Sleep(100); // Check heartbeat more frequently
            }
        }

        // State transition sequence
        public IEnumerable<SwitchState> GetStateTransitions(SwitchState targetState)
        {
            var currentState = _currentState;
            var transitions = CalculateTransitionPath(currentState, targetState);

            foreach (var state in transitions)
            {
                _currentState = state;
                yield return state;
                Thread.Sleep(500); // Simulate transition time
            }
        }

        private IEnumerable<SwitchState> CalculateTransitionPath(SwitchState from, SwitchState to)
        {
            // Simplified transition path - in real implementation, this would be more complex
            yield return from;
            if (from != to)
            {
                if (from != SwitchState.Active)
                    yield return SwitchState.Active;
                yield return to;
            }
        }

        private SwitchState DetermineState(TimeSpan timeSinceHeartbeat)
        {
            if (_currentState == SwitchState.Maintenance || _currentState == SwitchState.Disabled)
                return _currentState;

            return timeSinceHeartbeat switch
            {
                var t when t >= _timeout => SwitchState.Triggered,
                var t when t >= _criticalThreshold => SwitchState.Critical,
                var t when t >= _warningThreshold => SwitchState.Warning,
                _ => SwitchState.Active
            };
        }

        private string GenerateStateMessage(SwitchState state, TimeSpan timeSinceHeartbeat)
        {
            return state switch
            {
                SwitchState.Active => $"System active. Last heartbeat: {timeSinceHeartbeat.TotalSeconds:F1}s ago",
                SwitchState.Warning => $"Warning: No heartbeat for {timeSinceHeartbeat.TotalSeconds:F1}s",
                SwitchState.Critical => $"Critical: System timeout imminent. {timeSinceHeartbeat.TotalSeconds:F1}s since last heartbeat",
                SwitchState.Triggered => $"Emergency: Dead Man's Switch triggered after {timeSinceHeartbeat.TotalSeconds:F1}s",
                SwitchState.Maintenance => "System under maintenance",
                SwitchState.Disabled => "System disabled",
                SwitchState.Error => "System error",
                _ => "Unknown state"
            };
        }

        private void ExecuteStateAction(SwitchState state)
        {
            if (state == SwitchState.Triggered)
                _emergencyAction?.Invoke();
        }

        public void SendHeartbeat()
        {
            if (_isDisposed) return;
            _lastHeartbeat = DateTime.UtcNow;
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            _cts.Cancel();
            _cts.Dispose();
        }
    }

    // Example usage
    class Program
    {
        static async Task Main()
        {
            using var deadManSwitch = new DeadManSwitch(
                warningThreshold: TimeSpan.FromSeconds(5),
                criticalThreshold: TimeSpan.FromSeconds(8),
                timeout: TimeSpan.FromSeconds(10),
                emergencyAction: () => Console.WriteLine("🚨 Emergency procedures initiated!")
            );

            // Start state monitoring task
            _ = Task.Run(() =>
            {
                foreach (var transition in deadManSwitch.MonitorState())
                {
                    Console.WriteLine($"{transition.Timestamp:HH:mm:ss} - {transition.Message}");
                }
            });

            // Start heartbeat monitoring task
            _ = Task.Run(() =>
            {
                foreach (var heartbeatAge in deadManSwitch.MonitorHeartbeat())
                {
                    if (heartbeatAge.TotalSeconds % 1 == 0) // Log every second
                        Console.WriteLine($"Heartbeat age: {heartbeatAge.TotalSeconds:F1}s");
                }
            });

            // Simulate normal operation
            Console.WriteLine("Starting normal operation...");
            for (int i = 0; i < 3; i++)
            {
                deadManSwitch.SendHeartbeat();
                await Task.Delay(2000);
            }

            // Simulate warning condition
            Console.WriteLine("\nSimulating delayed heartbeat...");
            await Task.Delay(6000);
            deadManSwitch.SendHeartbeat();

            // Simulate critical condition
            Console.WriteLine("\nSimulating critical delay...");
            await Task.Delay(9000);

            // State history example
            Console.WriteLine("\nRecording state history...");
            foreach (var state in deadManSwitch.GetStateHistory(TimeSpan.FromSeconds(5)))
            {
                Console.WriteLine($"Historical state: {state.CurrentState} at {state.Timestamp:HH:mm:ss}");
            }

            // Wait for final messages
            await Task.Delay(2000);
        }
    }
}
