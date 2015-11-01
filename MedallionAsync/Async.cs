﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Medallion.Async
{
    public static class Async
    {
        private static readonly Task<bool> TrueTask = Task.FromResult(true);

        #region ---- WaitAsync ----
        public static Task<bool> WaitAsync(this Task task, TimeSpan timeout)
        {
            Throw.IfNull(task, "task");
            timeout.AssertIsValidTimeout();

            if (task.IsCompleted)
            {
                return TrueTask;
            }

            return InternalWaitAsync(task, timeout);
        }

        private static async Task<bool> InternalWaitAsync(Task task, TimeSpan timeout)
        {
            var timeoutCanceler = new CancellationTokenSource();
            var timeoutTask = Task.Delay(timeout, timeoutCanceler.Token);

            var completed = await Task.WhenAny(task, timeoutTask).ConfigureAwait(false);
            if (completed == task)
            {
                // clean up the timeout
                timeoutCanceler.Cancel();

                // propagate errors
                await completed.ConfigureAwait(false);

                return true;
            }

            return false;
        }
        #endregion

        #region ---- TaskCompletionSource ----
        public static TaskCompletionSource<TResult> CancelWith<TResult>(this TaskCompletionSource<TResult> taskCompletionSource, CancellationToken cancellationToken)
        {
            Throw.IfNull(taskCompletionSource, "taskCompletionSource");

            cancellationToken.Register(state => ((TaskCompletionSource<TResult>)state).TrySetCanceled(), state: taskCompletionSource);

            return taskCompletionSource;
        }

        public static TaskCompletionSource<TResult> TimeoutAfter<TResult>(this TaskCompletionSource<TResult> taskCompletionSource, TimeSpan timeout)
        {
            Throw.IfNull(taskCompletionSource, "taskCompletionSource");
            timeout.AssertIsValidTimeout();
            
            if (timeout != Timeout.InfiniteTimeSpan)
            {
                var timeoutCanceler = new CancellationTokenSource();
                var timeoutTask = Task.Delay(timeout, timeoutCanceler.Token);

                // timeout cancels the source
                timeoutTask.SynchronousContinueWith(
                    (task, state) => ((TaskCompletionSource<TResult>)state).TrySetException(new TimeoutException("Timeout of " + timeout + " expired!")),
                    state: taskCompletionSource,
                    continuationOptions: TaskContinuationOptions.OnlyOnRanToCompletion
                );

                // source cleans up the timeout
                taskCompletionSource.Task.SynchronousContinueWith(
                    (task, state) => ((CancellationTokenSource)state).Cancel(),
                    state: timeoutCanceler
                );
            }

            return taskCompletionSource;
        }
        #endregion

        #region ---- Process ----
        public static Task<int> WaitForExitAsync(this Process process, CancellationToken cancellationToken = default(CancellationToken))
        {
            Throw.IfNull(process, "process");
            process.EnableRaisingEvents = true;

            var taskCompletionSource = new TaskCompletionSource<int>()
                .CancelWith(cancellationToken);

            process.Exited += (sender, args) => taskCompletionSource.TrySetResult(((Process)sender).ExitCode);
            // handle the race condition where the process exits before we add the handler 
            if (process.HasExited && !cancellationToken.IsCancellationRequested)
            {
                return Task.FromResult(process.ExitCode);
            }

            // on cancellation, kill the process
            cancellationToken.Register(
                state => 
                {
                    var processState = ((Process)state);
                    if (!processState.HasExited)
                    {
                        processState.Kill();
                    }
                },
                state: process
            );

            return taskCompletionSource.Task;
        }
        

        #endregion

        #region ---- WaitHandle ----
        public static Task<bool> WaitOneAsync(this WaitHandle waitHandle, TimeSpan? timeout = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            Throw.IfNull(waitHandle, "waitHandle");
            if (timeout.HasValue)
            {
                timeout.Value.AssertIsValidTimeout();
            }

            return InternalWaitOneAsync(waitHandle, timeout.HasValue ? (int)timeout.Value.TotalMilliseconds : Timeout.Infinite, cancellationToken);
        }

        // based on http://www.thomaslevesque.com/2015/06/04/async-and-cancellation-support-for-wait-handles/
        private static async Task<bool> InternalWaitOneAsync(WaitHandle handle, int timeoutMillis, CancellationToken cancellationToken)
        {
            RegisteredWaitHandle registeredHandle = null;
            CancellationTokenRegistration tokenRegistration = default(CancellationTokenRegistration);
            try
            {
                var taskCompletionSource = new TaskCompletionSource<bool>()
                    .CancelWith(cancellationToken);
                registeredHandle = ThreadPool.RegisterWaitForSingleObject(
                    handle,
                    (state, timedOut) => ((TaskCompletionSource<bool>)state).TrySetResult(!timedOut),
                    state: taskCompletionSource,
                    millisecondsTimeOutInterval: timeoutMillis,
                    executeOnlyOnce: true
                );
                return await taskCompletionSource.Task.ConfigureAwait(false);
            }
            finally
            {
                if (registeredHandle != null)
                {
                    // this is different from the referenced site, but I think this is more correct:
                    // the handle passed to unregister is a handle to be signaled, not the one to unregister
                    // (that one is already captured by the registered handle). See
                    // http://referencesource.microsoft.com/#mscorlib/system/threading/threadpool.cs,065408fc096354fd
                    registeredHandle.Unregister(null);
                }
                tokenRegistration.Dispose();
            }
        }
        #endregion

        private static Task SynchronousContinueWith(this Task task, Action<Task, object> continuationFunction, object state, CancellationToken cancellationToken = default(CancellationToken), TaskContinuationOptions continuationOptions = TaskContinuationOptions.None)
        {
            return task.ContinueWith(
                continuationFunction,
                state: state,
                cancellationToken: cancellationToken,
                continuationOptions: TaskContinuationOptions.ExecuteSynchronously | continuationOptions,
                scheduler: TaskScheduler.Default
            );
        }

        private static void AssertIsValidTimeout(this TimeSpan timeout)
        {
            var totalMilliseconds = (long)timeout.TotalMilliseconds;
            if (totalMilliseconds < -1 || totalMilliseconds > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException("timeout");
            }
        }
    }
}
