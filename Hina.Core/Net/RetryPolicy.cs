using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hina.Core.Net
{
    /// <summary>
    /// Simple retry policy with exponential backoff for transient HTTP failures.
    /// </summary>
    public sealed class RetryPolicy
    {
        private readonly int _maxRetries;
        private readonly int _baseDelayMs;
        private readonly ILogger _logger;
        private readonly Random _jitterRng;

        public RetryPolicy(int maxRetries, int baseDelayMs, ILogger? logger = null, Random? jitterRng = null)
        {
            _maxRetries = maxRetries;
            _baseDelayMs = baseDelayMs;
            _logger = logger ?? NullLogger.Instance;
            _jitterRng = jitterRng ?? Random.Shared;
        }

        /// <summary>
        /// Executes <paramref name="action"/> with retry on transient failures.
        /// </summary>
        public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
        {
            int attempt = 0;
            while (true)
            {
                try
                {
                    return await action(ct);
                }
                catch (Exception ex) when (attempt < _maxRetries && IsTransient(ex) && !ct.IsCancellationRequested)
                {
                    attempt++;
                    int delayMs = CalculateDelay(attempt);
                    _logger.LogWarning(ex, "Transient error on attempt {Attempt}/{MaxRetries}, retrying in {DelayMs}ms",
                        attempt, _maxRetries, delayMs);
                    await Task.Delay(delayMs, ct);
                }
                catch (HttpRequestException ex) when (attempt >= _maxRetries && IsTransient(ex))
                {
                    throw new HttpRequestException(
                        $"Request failed after {_maxRetries + 1} attempts: {ex.Message}", ex, ex.StatusCode);
                }
            }
        }

        public int CalculateDelay(int attempt)
        {
            // Exponential backoff: baseDelay * 2^(attempt-1) + jitter
            int exponentialMs = _baseDelayMs * (1 << (attempt - 1));
            int jitterMs = _jitterRng.Next(0, Math.Max(1, exponentialMs / 4));
            return exponentialMs + jitterMs;
        }

        public static bool IsTransient(Exception ex)
        {
            if (ex is TaskCanceledException || ex is OperationCanceledException)
            {
                // Timeout (not user-cancellation) is transient
                return ex.InnerException is TimeoutException;
            }

            if (ex is HttpRequestException httpEx)
            {
                // 5xx server errors are transient; 4xx are not
                if (httpEx.StatusCode.HasValue)
                {
                    int code = (int)httpEx.StatusCode.Value;
                    return code >= 500;
                }

                // No status code means network-level failure (DNS, connection reset, etc.) - transient
                return true;
            }

            return false;
        }
    }
}
