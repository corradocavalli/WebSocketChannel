![Icon](https://raw.githubusercontent.com/devlooped/WebSocketChannel/main/assets/img/icon.png) WebSocketChannel
============

High-performance [System.Threading.Channels](https://devblogs.microsoft.com/dotnet/an-introduction-to-system-threading-channels/) API adapter for System.Net.WebSockets

[![Version](https://img.shields.io/nuget/v/WebSocketChannel.svg?color=royalblue)](https://www.nuget.org/packages/WebSocketChannel)
[![Downloads](https://img.shields.io/nuget/dt/WebSocketChannel.svg?color=green)](https://www.nuget.org/packages/WebSocketChannel)
[![License](https://img.shields.io/github/license/devlooped/WebSocketChannel.svg?color=blue)](https://github.com/devlooped/WebSocketChannel/blob/main/license.txt)
[![Build](https://github.com/devlooped/WebSocketChannel/workflows/build/badge.svg?branch=main)](https://github.com/devlooped/WebSocketChannel/actions)

# Usage

```csharp
var client = new ClientWebSocket();
await client.ConnectAsync(serverUri, CancellationToken.None);

Channel<ReadOnlyMemory<byte>> channel = client.CreateChannel();

await channel.Writer.WriteAsync(Encoding.UTF8.GetBytes("hello").AsMemory());

// Read single message when it arrives
ReadOnlyMemory<byte> response = await channel.Reader.ReadAsync();

// Read all messages while underlying websocket is open
await foreach (var item in channel.Reader.ReadAllAsync())
{
    Console.WriteLine(Encoding.UTF8.GetString(item.Span));
}

// Completing the writer closes the underlying websocket cleanly
channel.Writer.Complete();

// Can also complete reporting an error for the remote party
channel.Writer.Complete(new InvalidOperationException("Bad format"));
```


The `WebSocketChannel` can also be used on the server. The following example is basically 
taken from the documentation on [WebSockets in ASP.NET Core](https://docs.microsoft.com/en-us/aspnet/core/fundamentals/websockets?view=aspnetcore-5.0#configure-the-middleware) 
and adapted to use a `WebSocketChannel` to echo messages to the client:

```csharp
app.Use(async (context, next) =>
{
    if (context.Request.Path == "/ws")
    {
        if (context.WebSockets.IsWebSocketRequest)
        {
            using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
            var channel = WebSocketChannel.Create(webSocket);
            try
            {
                await foreach (var item in channel.Reader.ReadAllAsync(context.RequestAborted))
                {
                    await channel.Writer.WriteAsync(item, context.RequestAborted);
                }
            }
            catch (OperationCanceledException)
            {
                try
                {
                    await webSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, default);
                }
                catch { } // Best effort to try closing cleanly. Client may be entirely gone.
            }
        }
        else
        {
            context.Response.StatusCode = (int) HttpStatusCode.BadRequest;
        }
    }
    else
    {
        await next();
    }
});
```


# Dogfooding

[![CI Version](https://img.shields.io/endpoint?url=https://shields.kzu.io/vpre/WebSocketChannel/main&label=nuget.ci&color=brightgreen)](https://pkg.kzu.io/index.json)
[![Build](https://github.com/devlooped/WebSocketChannel/workflows/build/badge.svg?branch=main)](https://github.com/devlooped/WebSocketChannel/actions)

We also produce CI packages from branches and pull requests so you can dogfood builds as quickly as they are produced. 

The CI feed is `https://pkg.kzu.io/index.json`. 

The versioning scheme for packages is:

- PR builds: *42.42.42-pr*`[NUMBER]`
- Branch builds: *42.42.42-*`[BRANCH]`.`[COMMITS]`



## Sponsors

[![sponsored](https://raw.githubusercontent.com/devlooped/oss/main/assets/images/sponsors.svg)](https://github.com/sponsors/devlooped) [![clarius](https://raw.githubusercontent.com/clarius/branding/main/logo/byclarius.svg)](https://github.com/clarius)[![clarius](https://raw.githubusercontent.com/clarius/branding/main/logo/logo.svg)](https://github.com/clarius)

*[get mentioned here too](https://github.com/sponsors/devlooped)!*