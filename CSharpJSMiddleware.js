// CSharpJS Library
class CSharpJS {
    static async {
        // Create class-like structure with proper inheritance
        class class extends Function {
            constructor(definition) {
                super();
                const classObj = function(...args) {
                    if (this instanceof classObj) {
                        if (definition.constructor) {
                            definition.constructor.apply(this, args);
                        }
                    }
                };
                
                Object.assign(classObj.prototype, definition);
                return classObj;
            }
        }

        // Task-like Promise wrapper
        class Task {
            constructor(promise) {
                this._promise = promise;
            }

            static Run(fn) {
                return new Task(Promise.resolve().then(() => fn()));
            }

            static Delay(ms) {
                return new Task(new Promise(resolve => setTimeout(resolve, ms)));
            }

            async Wait() {
                return await this._promise;
            }

            static WhenAll(tasks) {
                return new Task(Promise.all(tasks.map(t => t._promise)));
            }

            static WhenAny(tasks) {
                return new Task(Promise.race(tasks.map(t => t._promise)));
            }
        }

        // Dictionary implementation
        class Dictionary {
            constructor() {
                this._map = new Map();
            }

            Add(key, value) {
                if (this._map.has(key)) {
                    throw new Error('Key already exists');
                }
                this._map.set(key, value);
            }

            Remove(key) {
                return this._map.delete(key);
            }

            TryGetValue(key, outValue = {}) {
                if (this._map.has(key)) {
                    outValue.value = this._map.get(key);
                    return true;
                }
                return false;
            }

            ContainsKey(key) {
                return this._map.has(key);
            }

            get Count() {
                return this._map.size;
            }
        }

        // Queue implementation
        class Queue {
            constructor() {
                this._items = [];
            }

            Enqueue(item) {
                this._items.push(item);
            }

            Dequeue() {
                if (this.Count === 0) {
                    throw new Error('Queue is empty');
                }
                return this._items.shift();
            }

            TryDequeue(outValue = {}) {
                if (this.Count > 0) {
                    outValue.value = this._items.shift();
                    return true;
                }
                return false;
            }

            get Count() {
                return this._items.length;
            }
        }

        // Dead Letter Router implementation
        class DeadLetterRouter {
            constructor() {
                this._handlers = new Dictionary();
                this._policies = new Dictionary();
                this._deadLetterQueue = new Queue();
                this._retryQueues = new Dictionary();
                this._cancellationToken = { cancelled: false };
                this._startRetryProcessor();
            }

            async _startRetryProcessor() {
                while (!this._cancellationToken.cancelled) {
                    await this._processRetryQueues();
                    await Task.Delay(1000).Wait();
                }
            }

            RegisterHandler(routingKey, handler, retryPolicy) {
                this._handlers.Add(routingKey, handler);
                this._policies.Add(routingKey, retryPolicy);
                this._retryQueues.Add(routingKey, new Queue());
            }

            async RouteMessage(message) {
                let handlerOut = {};
                if (!this._handlers.TryGetValue(message.RoutingKey, handlerOut)) {
                    await this._handleUnroutableMessage(message);
                    return;
                }

                try {
                    const success = await handlerOut.value(message);
                    if (!success) {
                        await this._handleFailedMessage(message);
                    }
                } catch (ex) {
                    message.ErrorDetails = ex.message;
                    await this._handleFailedMessage(message);
                }
            }

            async _handleFailedMessage(message) {
                let policyOut = {};
                if (!this._policies.TryGetValue(message.RoutingKey, policyOut)) {
                    this._moveToDeadLetterQueue(message);
                    return;
                }

                if (this._shouldRetry(message, policyOut.value)) {
                    await this._enqueueForRetry(message, policyOut.value);
                } else {
                    this._moveToDeadLetterQueue(message);
                }
            }

            _shouldRetry(message, policy) {
                if (message.RetryCount >= policy.MaxRetries)
                    return false;

                if (message.TimeToLive) {
                    const age = Date.now() - message.CreatedAt;
                    if (age > message.TimeToLive)
                        return false;
                }

                return true;
            }

            async _enqueueForRetry(message, policy) {
                message.RetryCount++;
                const delay = this._calculateRetryDelay(message.RetryCount, policy);
                await Task.Delay(delay).Wait();

                let queueOut = {};
                if (this._retryQueues.TryGetValue(message.RoutingKey, queueOut)) {
                    queueOut.value.Enqueue(message);
                }
            }

            _calculateRetryDelay(retryCount, policy) {
                if (!policy.UseExponentialBackoff)
                    return policy.InitialDelay;

                const delay = policy.InitialDelay * Math.pow(policy.BackoffMultiplier, retryCount - 1);
                return Math.min(delay, policy.MaxDelay);
            }

            async _processRetryQueues() {
                this._retryQueues._map.forEach(async (queue, routingKey) => {
                    let messageOut = {};
                    while (queue.TryDequeue(messageOut)) {
                        await this.RouteMessage(messageOut.value);
                    }
                });
            }

            _moveToDeadLetterQueue(message) {
                this._deadLetterQueue.Enqueue(message);
            }

            async _handleUnroutableMessage(message) {
                message.ErrorDetails = 'No handler registered for routing key';
                this._moveToDeadLetterQueue(message);
            }

            GetStats() {
                return {
                    TotalDeadLetters: this._deadLetterQueue.Count,
                    RetryQueueSize: Array.from(this._retryQueues._map.values())
                        .reduce((total, queue) => total + queue.Count, 0)
                };
            }

            Dispose() {
                this._cancellationToken.cancelled = true;
            }
        }

        // Example usage
        const router = new DeadLetterRouter();

        router.RegisterHandler("order.process", 
            async (msg) => {
                console.log(`Processing: ${msg.Content}`);
                return true;
            },
            {
                MaxRetries: 3,
                InitialDelay: 1000,
                BackoffMultiplier: 2,
                MaxDelay: 60000,
                UseExponentialBackoff: true
            }
        );

        await router.RouteMessage({
            Content: "Order #1",
            RoutingKey: "order.process",
            CreatedAt: Date.now(),
            RetryCount: 0,
            TimeToLive: 5 * 60 * 1000 // 5 minutes
        });

        // Get stats
        const stats = router.GetStats();
        console.log(`Total dead letters: ${stats.TotalDeadLetters}`);
        console.log(`Retry queue size: ${stats.RetryQueueSize}`);
    }
}

// Export for different module systems
if (typeof module !== 'undefined' && module.exports) {
    module.exports = CSharpJS;
} else if (typeof define === 'function' && define.amd) {
    define([], () => CSharpJS);
} else {
    window.CSharpJS = CSharpJS;
}
