﻿using System.Diagnostics.CodeAnalysis;

namespace Devlooped.Net;

/// <summary>
/// Factory class for a <see cref="Channel{T}"/> over a <see cref="WebSocket"/> that 
/// exposes the underlying binary messages as <see cref="ReadOnlyMemory{T}"/> of <see cref="byte"/>.
/// </summary>
/// <remarks>
/// Due to the nature of the underlying <see cref="WebSocket"/>, no concurrent writing 
/// or reading is allowed, so the returned channel is effectively thread-safe by design. 
/// </remarks>
public static class WebSocketChannel
{
    /// <summary>
    /// Creates a channel over the given <paramref name="webSocket"/> for reading/writing 
    /// purposes.
    /// </summary>
    /// <param name="webSocket">The <see cref="WebSocket"/> to create the channel over.</param>
    /// <returns>A channel to read/write the given <paramref name="webSocket"/>.</returns>
    public static Channel<ReadOnlyMemory<byte>> Create(WebSocket webSocket)
        => new DefaultWebSocketChannel(webSocket);

    class DefaultWebSocketChannel : Channel<ReadOnlyMemory<byte>>
    {
        static readonly Exception defaultDoneWritting = new Exception(nameof(defaultDoneWritting));
        static readonly Exception socketClosed = new WebSocketException(WebSocketError.ConnectionClosedPrematurely, "WebSocket was closed by the remote party.");

        static readonly TimeSpan closeTimeout = TimeSpan.FromMilliseconds(250);
        readonly TaskCompletionSource<bool> completion = new();
        readonly object syncObj = new();
        Exception? done;

        WebSocket webSocket;

        public DefaultWebSocketChannel(WebSocket webSocket)
        {
            this.webSocket = webSocket;
            Reader = new WebSocketChannelReader(this);
            Writer = new WebSocketChannelWriter(this);
        }

        void Complete()
        {
            if (done is OperationCanceledException oce)
            {
                completion.TrySetCanceled(oce.CancellationToken);
            }
            else if (done != null && done != defaultDoneWritting)
            {
                completion.TrySetException(done);
                if (webSocket.State == WebSocketState.Open)
                {
                    var closed = Close(done.Message);
                    while (!closed.IsCompleted)
                        ;
                }
            }
            else
            {
                completion.TrySetResult(true);
                if (webSocket.State == WebSocketState.Open)
                {
                    var closed = Close();
                    while (!closed.IsCompleted)
                        ;
                }
            }
        }

        async ValueTask Close(string? description = default)
        {
            var closeTask = webSocket is ClientWebSocket ?
                // Disconnect from client vs server is different.
                webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, description, default) :
                webSocket.CloseOutputAsync(description != null ? WebSocketCloseStatus.InternalServerError : WebSocketCloseStatus.NormalClosure, description, default);

            // Don't wait indefinitely for the close to be acknowledged
            await Task.WhenAny(closeTask, Task.Delay(closeTimeout));
        }

        class WebSocketChannelReader : ChannelReader<ReadOnlyMemory<byte>>
        {
            static readonly TimeSpan tryReadTimeout = TimeSpan.FromMilliseconds(250);
            readonly DefaultWebSocketChannel channel;
            readonly SemaphoreSlim semaphore = new SemaphoreSlim(1, 1);

            public WebSocketChannelReader(DefaultWebSocketChannel channel) => this.channel = channel;

            public override Task Completion => channel.completion.Task;

            public override ValueTask<ReadOnlyMemory<byte>> ReadAsync(CancellationToken cancellationToken = default)
            {
                if (cancellationToken.IsCancellationRequested)
                    return new ValueTask<ReadOnlyMemory<byte>>(Task.FromCanceled<ReadOnlyMemory<byte>>(cancellationToken));

                var error = channel.done;
                if (error != null && error != defaultDoneWritting)
                    return new ValueTask<ReadOnlyMemory<byte>>(Task.FromException<ReadOnlyMemory<byte>>(error));

                if (channel.webSocket.State != WebSocketState.Open)
                    return new ValueTask<ReadOnlyMemory<byte>>(Task.FromException<ReadOnlyMemory<byte>>(socketClosed));

                return ReadCoreAsync(cancellationToken);
            }

            public override bool TryRead([MaybeNullWhen(false)] out ReadOnlyMemory<byte> item)
            {
                item = null;
                if (channel.done != null)
                    return false;

                if (channel.webSocket.State != WebSocketState.Open)
                    return false;

                var cts = new CancellationTokenSource(tryReadTimeout);
                var result = ReadCoreAsync(cts.Token);
                while (!result.IsCompleted)
                    ;

                if (result.IsCompletedSuccessfully)
                    item = result.Result;

                return result.IsCompletedSuccessfully && channel.webSocket.State == WebSocketState.Open;
            }

            public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default)
            {
                if (cancellationToken.IsCancellationRequested)
                    return new ValueTask<bool>(Task.FromCanceled<bool>(cancellationToken));

                var error = channel.done;
                return
                    // We can read as long as the socket is open.
                    error == null ? new ValueTask<bool>(channel.webSocket.State == WebSocketState.Open) :
                    // both the sentinel done as well as the sentinel closed do not cause an exception to be thrown.
                    // they are expected conditions for clean completion.
                    error != defaultDoneWritting && error != socketClosed ? new ValueTask<bool>(Task.FromException<bool>(error)) :
                    default;
            }

            async ValueTask<ReadOnlyMemory<byte>> ReadCoreAsync(CancellationToken cancellation)
            {
                await semaphore.WaitAsync(cancellation);
                try
                {
                    using var owner = MemoryPool<byte>.Shared.Rent(512);
                    var received = await channel.webSocket.ReceiveAsync(owner.Memory, cancellation).ConfigureAwait(false);
                    var count = received.Count;
                    while (!cancellation.IsCancellationRequested && !received.EndOfMessage && received.MessageType != WebSocketMessageType.Close)
                    {
                        if (received.Count == 0)
                            break;

                        received = await channel.webSocket.ReceiveAsync(owner.Memory, cancellation).ConfigureAwait(false);
                        count += received.Count;
                    }

                    cancellation.ThrowIfCancellationRequested();

                    // We didn't get a complete message, we can't flush partial message.
                    if (received.MessageType == WebSocketMessageType.Close)
                    {
                        // Server requested closure.
                        lock (channel.syncObj)
                        {
                            if (channel.done == null)
                            {
                                channel.done = socketClosed;
                                channel.Complete();
                            }
                        }
                        throw socketClosed;
                    }

                    // Only return from the whole buffer, the slice of bytes that we actually received.
                    return owner.Memory.Slice(0, count);
                }
                // Don't re-throw the expected socketClosed exception we throw when Close received.
                catch (Exception ex) when (ex != socketClosed &&
                                           (ex is WebSocketException || ex is InvalidOperationException))
                {
                    // We consider premature closure just as an explicit closure.
                    if (ex is WebSocketException wex && wex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
                        ex = socketClosed;

                    lock (channel.syncObj)
                    {
                        if (channel.done == null)
                            channel.done = ex;

                        channel.Complete();
                    }
                    throw;
                }
                finally
                {
                    semaphore.Release();
                }
            }
        }

        class WebSocketChannelWriter : ChannelWriter<ReadOnlyMemory<byte>>
        {
            readonly DefaultWebSocketChannel channel;
            readonly SemaphoreSlim semaphore = new SemaphoreSlim(1, 1);

            public WebSocketChannelWriter(DefaultWebSocketChannel channel) => this.channel = channel;

            public override bool TryComplete(Exception? error = null)
            {
                lock (channel.syncObj)
                {
                    if (channel.done != null)
                        return false;

                    channel.done = error ?? defaultDoneWritting;
                    channel.Complete();
                }

                return true;
            }

            public override bool TryWrite(ReadOnlyMemory<byte> item)
            {
                if (channel.done != null)
                    return false;

                var result = WriteAsyncCore(item, CancellationToken.None);
                while (!result.IsCompleted)
                    ;

                return result.IsCompletedSuccessfully;
            }

            public override ValueTask WriteAsync(ReadOnlyMemory<byte> item, CancellationToken cancellationToken = default)
            {
                if (cancellationToken.IsCancellationRequested)
                    return new ValueTask(Task.FromCanceled<ReadOnlyMemory<byte>>(cancellationToken));

                var error = channel.done;
                if (error != null && error != defaultDoneWritting)
                    return new ValueTask(Task.FromException(error));

                return WriteAsyncCore(item, cancellationToken);
            }

            public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default)
            {
                var error = channel.done;
                return
                    cancellationToken.IsCancellationRequested ? new ValueTask<bool>(Task.FromCanceled<bool>(cancellationToken)) :
                    // We can read as long as the socket is open.
                    error == null ? new ValueTask<bool>(channel.webSocket.State == WebSocketState.Open) :
                    error != defaultDoneWritting ? new ValueTask<bool>(Task.FromException<bool>(error)) :
                    default;
            }

            async ValueTask WriteAsyncCore(ReadOnlyMemory<byte> item, CancellationToken cancellationToken = default)
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    await channel.webSocket.SendAsync(item, WebSocketMessageType.Binary, true, cancellationToken);
                }
                finally
                {
                    semaphore.Release();
                }
            }
        }
    }
}