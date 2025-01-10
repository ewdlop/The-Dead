using System;
using System.Threading;
using System.Threading.Tasks;

namespace DeadManSwitchSystem
{
    public enum SwitchState
    {
        Active,         // Normal operation
        Warning,        // Heartbeat delayed
        Critical,       // Near timeout
        Triggered,      // Switch activated
        Maintenance,    // Under maintenance
        Disabled,       // Switched off
        Error          // System error
    }

    public enum SwitchEvent
    {
        Heartbeat,          // Regular heartbeat received
        HeartbeatMissed,    // Heartbeat timeout
        Timeout,            // Complete timeout
        Enable,             // Enable the switch
        Disable,            // Disable the switch
        StartMaintenance,   // Enter maintenance mode
        EndMaintenance,     // Exit maintenance mode
        Reset,              // Reset the system
        Error              // Error occurred
    }

    public class DeadManSwitch : IDisposable
    {
        private SwitchState _currentState;
        private readonly Timer _watchdogTimer;
        private DateTime _lastHeartbeat;
        private readonly TimeSpan _warningThreshold;
        private readonly TimeSpan _criticalThreshold;
        private readonly TimeSpan _timeout;
        private readonly Action _emergencyAction;
        private readonly object _stateLock = new object();
        private bool _isDisposed;

        public DeadManSwitch(
            TimeSpan warningThreshold,
            TimeSpan criticalThreshold,
            TimeSpan timeout,
            Action emergencyAction)
        {
            _currentState = SwitchState.Active;
            _warningThreshold = warningThreshold;
            _criticalThreshold = criticalThreshold;
            _timeout = timeout;
            _emergencyAction = emergencyAction;
            _lastHeartbeat = DateTime.UtcNow;

            // Create watchdog timer
            _watchdogTimer = new Timer(
                CheckState,
                null,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1)
            );
        }

        public void ProcessEvent(SwitchEvent eventType)
        {
            lock (_stateLock)
            {
                _currentState = ProcessStateTransition(_currentState, eventType);
                LogStateChange(eventType, _currentState);
            }
        }

        private SwitchState ProcessStateTransition(SwitchState currentState, SwitchEvent eventType)
        {
            return (currentState, eventType) switch
            {
                // Active state transitions
                (SwitchState.Active, SwitchEvent.Heartbeat) => SwitchState.Active,
                (SwitchState.Active, SwitchEvent.HeartbeatMissed) => SwitchState.Warning,
                (SwitchState.Active, SwitchEvent.Disable) => SwitchState.Disabled,
                (SwitchState.Active, SwitchEvent.StartMaintenance) => SwitchState.Maintenance,
                (SwitchState.Active, SwitchEvent.Error) => SwitchState.Error,

                // Warning state transitions
                (SwitchState.Warning, SwitchEvent.Heartbeat) => SwitchState.Active,
                (SwitchState.Warning, SwitchEvent.HeartbeatMissed) => SwitchState.Critical,
                (SwitchState.Warning, SwitchEvent.Disable) => SwitchState.Disabled,
                (SwitchState.Warning, SwitchEvent.Error) => SwitchState.Error,

                // Critical state transitions
                (SwitchState.Critical, SwitchEvent.Heartbeat) => SwitchState.Warning,
                (SwitchState.Critical, SwitchEvent.Timeout) => SwitchState.Triggered,
                (SwitchState.Critical, SwitchEvent.Disable) => SwitchState.Disabled,
                (SwitchState.Critical, SwitchEvent.Error) => SwitchState.Error,

                // Triggered state transitions
                (SwitchState.Triggered, SwitchEvent.Reset) => SwitchState.Active,
                (SwitchState.Triggered, SwitchEvent.Disable) => SwitchState.Disabled,

                // Maintenance state transitions
                (SwitchState.Maintenance, SwitchEvent.EndMaintenance) => SwitchState.Active,
                (SwitchState.Maintenance, SwitchEvent.Error) => SwitchState.Error,

                // Disabled state transitions
                (SwitchState.Disabled, SwitchEvent.Enable) => SwitchState.Active,
                (SwitchState.Disabled, SwitchEvent.Error) => SwitchState.Error,

                // Error state transitions
                (SwitchState.Error, SwitchEvent.Reset) => SwitchState.Active,

                // Default: maintain current state
                _ => currentState
            };
        }

        private void CheckState(object? state)
        {
            if (_isDisposed) return;

            var timeSinceLastHeartbeat = DateTime.UtcNow - _lastHeartbeat;
            var eventType = DetermineEvent(timeSinceLastHeartbeat);

            if (eventType != null)
            {
                ProcessEvent(eventType.Value);
                ExecuteStateAction(_currentState);
            }
        }

        private SwitchEvent? DetermineEvent(TimeSpan elapsed)
        {
            return elapsed switch
            {
                var t when t > _timeout => SwitchEvent.Timeout,
                var t when t > _criticalThreshold => SwitchEvent.HeartbeatMissed,
                var t when t > _warningThreshold => SwitchEvent.HeartbeatMissed,
                _ => null
            };
        }

        private void ExecuteStateAction(SwitchState state)
        {
            switch (state)
            {
                case SwitchState.Warning:
                    Console.WriteLine("WARNING: Heartbeat delayed");
                    break;

                case SwitchState.Critical:
                    Console.WriteLine("CRITICAL: System near timeout");
                    break;

                case SwitchState.Triggered:
                    Console.WriteLine("EMERGENCY: Dead Man's Switch triggered");
                    _emergencyAction?.Invoke();
                    break;

                case SwitchState.Error:
                    Console.WriteLine("ERROR: System malfunction");
                    break;
            }
        }

        public void SendHeartbeat()
        {
            if (_isDisposed) return;

            lock (_stateLock)
            {
                _lastHeartbeat = DateTime.UtcNow;
                ProcessEvent(SwitchEvent.Heartbeat);
            }
        }

        private void LogStateChange(SwitchEvent eventType, SwitchState newState)
        {
            Console.WriteLine($"Event: {eventType}, New State: {newState}");
        }

        public void Dispose()
        {
            if (_isDisposed) return;

            _isDisposed = true;
            _watchdogTimer?.Dispose();
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
                emergencyAction: () => Console.WriteLine("Emergency procedures initiated!")
            );

            // Normal operation
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

            // Simulate maintenance mode
            Console.WriteLine("\nEntering maintenance mode...");
            deadManSwitch.ProcessEvent(SwitchEvent.StartMaintenance);
            await Task.Delay(2000);
            deadManSwitch.ProcessEvent(SwitchEvent.EndMaintenance);

            // Simulate complete timeout
            Console.WriteLine("\nSimulating complete timeout...");
            await Task.Delay(12000);

            // Wait for final messages
            await Task.Delay(1000);
        }
    }
}
