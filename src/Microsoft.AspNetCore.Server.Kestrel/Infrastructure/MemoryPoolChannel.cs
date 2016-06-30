// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.AspNetCore.Server.Kestrel.Infrastructure
{
    public class MemoryPoolChannel : IReadableChannel, IWritableChannel, IDisposable
    {
        private static readonly Action _awaitableIsCompleted = () => { };
        private static readonly Action _awaitableIsNotCompleted = () => { };

        private readonly MemoryPool _memory;
        private readonly ManualResetEventSlim _manualResetEvent = new ManualResetEventSlim(false, 0);

        private Action _awaitableState;
        private Exception _awaitableError;

        private MemoryPoolBlock _head;
        private MemoryPoolBlock _tail;

        private bool _completedWriting;
        private bool _completedReading;

        private int _consumingState;
        private object _sync = new object();
        private readonly int _threshold;
        private LimitState _limitState;
        private readonly IThreadPool _threadPool;

        public MemoryPoolChannel(MemoryPool memory, IThreadPool threadPool, int threshold = 10 * 1024)
        {
            _memory = memory;
            _awaitableState = _awaitableIsNotCompleted;
            _threshold = threshold;
            _threadPool = threadPool;
        }

        public bool Completed => _completedWriting;

        public bool IsCompleted => ReferenceEquals(_awaitableState, _awaitableIsCompleted);

        public MemoryPoolIterator BeginWrite()
        {
            const int minimumSize = 2048;

            MemoryPoolBlock block = null;

            if (_tail != null && minimumSize <= _tail.Data.Offset + _tail.Data.Count - _tail.End)
            {
                block = _tail;
            }
            else
            {
                block = _memory.Lease();
            }

            lock (_sync)
            {
                if (_head == null)
                {
                    _head = block;
                }
                else if (block != _tail)
                {
                    Volatile.Write(ref _tail.Next, block);
                    _tail = block;
                }

                return new MemoryPoolIterator(block, block.End);
            }
        }

        public Task EndWriteAsync(MemoryPoolIterator end)
        {
            lock (_sync)
            {
                if (!end.IsDefault)
                {
                    _tail = end.Block;
                    _tail.End = end.Index;

                    // REVIEW: This isn't that efficient, (we can do better)
                    var length = new MemoryPoolIterator(_head).GetLength(end);

                    if (length > _threshold)
                    {
                        _limitState = new LimitState();
                        _limitState.Length = length;
                    }
                }

                Complete();

                return _limitState?.Tcs.Task ?? TaskUtilities.CompletedTask;
            }
        }

        private void Complete()
        {
            var awaitableState = Interlocked.Exchange(
                ref _awaitableState,
                _awaitableIsCompleted);

            _manualResetEvent.Set();

            if (!ReferenceEquals(awaitableState, _awaitableIsCompleted) &&
                !ReferenceEquals(awaitableState, _awaitableIsNotCompleted))
            {
                _threadPool.Run(awaitableState);
            }
        }

        public MemoryPoolSpan BeginRead()
        {
            if (Interlocked.CompareExchange(ref _consumingState, 1, 0) != 0)
            {
                throw new InvalidOperationException("Already consuming input.");
            }

            var start = new MemoryPoolIterator(_head);
            var end = new MemoryPoolIterator(_tail, _tail?.End ?? 0);
            return new MemoryPoolSpan(start, end);
        }

        public void EndRead(MemoryPoolIterator end)
        {
            EndRead(end, end);
        }

        public void EndRead(
            MemoryPoolIterator consumed,
            MemoryPoolIterator examined)
        {
            MemoryPoolBlock returnStart = null;
            MemoryPoolBlock returnEnd = null;

            lock (_sync)
            {
                if (!consumed.IsDefault)
                {
                    var lengthConsumed = new MemoryPoolIterator(_head).GetLength(consumed);

                    if (_limitState != null)
                    {
                        _limitState.Length -= lengthConsumed;

                        // Need to drain down to 1/2 to start the pipe again
                        if (_limitState.Length < (_threshold / 2))
                        {
                            _threadPool.Complete(_limitState.Tcs);
                        }
                    }

                    returnStart = _head;
                    returnEnd = consumed.Block;
                    _head = consumed.Block;
                    _head.Start = consumed.Index;
                }

                if (!examined.IsDefault &&
                    examined.IsEnd &&
                    Completed == false &&
                    _awaitableError == null)
                {
                    _manualResetEvent.Reset();

                    Interlocked.CompareExchange(
                        ref _awaitableState,
                        _awaitableIsNotCompleted,
                        _awaitableIsCompleted);
                }
            }

            while (returnStart != returnEnd)
            {
                var returnBlock = returnStart;
                returnStart = returnStart.Next;
                returnBlock.Pool.Return(returnBlock);
            }

            if (Interlocked.CompareExchange(ref _consumingState, 0, 1) != 1)
            {
                throw new InvalidOperationException("No ongoing consuming operation to complete.");
            }
        }

        public void CompleteAwaiting()
        {
            Complete();
        }

        public void CompleteWriting(Exception error = null)
        {
            lock (_sync)
            {
                _completedWriting = true;

                if (error != null)
                {
                    _awaitableError = error;
                }

                Complete();

                if (_completedReading)
                {
                    Dispose();
                }
            }
        }

        public void CompleteReading()
        {
            lock (_sync)
            {
                _completedReading = true;

                if (_completedWriting)
                {
                    Dispose();
                }
            }
        }

        public void Cancel()
        {
            _awaitableError = new TaskCanceledException("The request was aborted");

            Complete();
        }

        public IReadableChannel GetAwaiter()
        {
            return this;
        }

        public void OnCompleted(Action continuation)
        {
            var awaitableState = Interlocked.CompareExchange(
                ref _awaitableState,
                continuation,
                _awaitableIsNotCompleted);

            if (ReferenceEquals(awaitableState, _awaitableIsNotCompleted))
            {
                return;
            }
            else if (ReferenceEquals(awaitableState, _awaitableIsCompleted))
            {
                // Dispatch here to avoid stack diving
                _threadPool.Run(continuation);
            }
            else
            {
                _awaitableError = new InvalidOperationException("Concurrent reads are not supported.");

                Interlocked.Exchange(
                    ref _awaitableState,
                    _awaitableIsCompleted);

                _manualResetEvent.Set();

                _threadPool.Run(continuation);
                _threadPool.Run(awaitableState);
            }
        }

        public void UnsafeOnCompleted(Action continuation)
        {
            OnCompleted(continuation);
        }

        public void GetResult()
        {
            if (!IsCompleted)
            {
                _manualResetEvent.Wait();
            }
            var error = _awaitableError;
            if (error != null)
            {
                if (error is TaskCanceledException || error is InvalidOperationException)
                {
                    throw error;
                }
                throw new IOException(error.Message, error);
            }
        }

        public void Dispose()
        {
            Debug.Assert(_completedWriting, "Not completed writing");
            Debug.Assert(_completedReading, "Not completed reading");

            lock (_sync)
            {
                // Return all blocks
                var block = _head;
                while (block != null)
                {
                    var returnBlock = block;
                    block = block.Next;

                    returnBlock.Pool.Return(returnBlock);
                }

                _head = null;
                _tail = null;
            }
        }

        private class LimitState
        {
            public int Length { get; set; }

            public TaskCompletionSource<object> Tcs { get; set; } = new TaskCompletionSource<object>();
        }
    }
}