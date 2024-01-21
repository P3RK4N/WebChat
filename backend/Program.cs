using Microsoft.AspNetCore.WebSockets;
using Microsoft.Extensions.Primitives;
using Newtonsoft.Json;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Xml;

#region Initialization

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services
    .AddRazorPages();

builder.Services.AddWebSockets((options) =>
{

});

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowLocalhost", builder =>
    {
        builder.WithOrigins("http://localhost:443", "http://localhost:80", "http://localhost:8080")
                .AllowAnyHeader()
                .AllowAnyMethod();
    });
});


var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseRouting();

app.MapRazorPages();

app.UseCors("AllowLocalhost");


#endregion

List<int>                       connectedUsers  = new List<int>();
string[]                        messageCache    = { "", "" };
ConnectionType[]                connectionTypes = { 0, 0 };
List<string>[]                  messageBuffer   = new List<string>[2] { new(), new() };
TaskCompletionSource<object>[]  conditionals    = new TaskCompletionSource<object>[2] { new(), new() };

void signalMessage(int sender_id)
{
    lock(conditionals[sender_id - 1])
    {
        conditionals[sender_id - 1].SetResult(null);
        conditionals[sender_id - 1] = new();
    }
}

app.MapGet("/join", async (HttpContext context) => await context.Response.WriteAsync("Home!"));

app.MapPost("/join", async (HttpContext context) =>
{
    var conn_type = Int32.Parse(context.Request.Query["connection_type"].First());
    Console.WriteLine($"Called join! | Connection type: {conn_type}");

    // New user trying to join full room
    if(connectedUsers.Count == 2)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("Room is full!");
        Console.WriteLine($"User could not join due to full room!");
    }

    // New user trying to join non full room
    else
    {
        var id = connectedUsers.Contains(1) ? 2 : 1;
        var other_user = id == 1 ? 2 : 1;
        connectedUsers.Add(id);
        connectionTypes[id - 1] = (ConnectionType)conn_type;
        Console.WriteLine($"Joined user id: {id}");

        using (StringWriter stringWriter = new StringWriter())
        {
            // Create an XmlWriter
            using (XmlWriter xmlWriter = XmlWriter.Create(stringWriter))
            {
                // Start writing the XML document
                xmlWriter.WriteStartDocument();
                
                // Start the root element
                xmlWriter.WriteStartElement("Response");

                // Write the integer element
                xmlWriter.WriteElementString("UserId", $"{id}");

                if(!string.IsNullOrEmpty(messageCache[other_user - 1]))
                {
                    xmlWriter.WriteElementString("InitialMessage", messageCache[other_user - 1]);
                    messageCache[other_user - 1] = string.Empty;
                }

                // End the root element
                xmlWriter.WriteEndElement();

                // End the XML document
                xmlWriter.WriteEndDocument();
            }

            // Get the XML string from the StringWriter
            string xmlResponse = stringWriter.ToString();

            // Print or use the XML response
            context.Response.ContentType = "application/xml";
            await context.Response.WriteAsync(xmlResponse);
        }
    }
});

app.MapPost("/leave", async (HttpContext context) =>
{
    var user_id = Int32.Parse(context.Request.Query["user_id"].FirstOrDefault("-1") ?? "-1");
    var other_user = user_id == 1 ? 2 : 1;

    Console.WriteLine($"Called leave! User id: {user_id}");

    // New user trying to leave room
    if(!connectedUsers.Contains(user_id))
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("Already not in a room!");
    }

    // Old user
    else
    {
        // Remove user, its connection type, its message buffer
        connectedUsers.Remove(user_id);
        connectionTypes[user_id - 1] = 0;

        lock(messageBuffer[other_user - 1])
        {
            messageBuffer[other_user - 1].Clear();
        }
        
        await context.Response.WriteAsync($"User {user_id} Left a room!");
    }
});

app.MapGet("/message", async (HttpContext context) =>
{
    var user_id = Int32.Parse(context.Request.Query["user_id"].FirstOrDefault("-1") ?? "-1");
    
    if(!connectedUsers.Contains(user_id))
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("User is not connected!");
        return;
    }

    Console.WriteLine($"Getting messages! User id: {user_id}");

    var other_user = user_id == 1 ? 2 : 1;

    // If long poll, await
    if(messageBuffer[other_user - 1].Count == 0 && connectionTypes[user_id - 1] == ConnectionType.LongPoll)
    {
        await conditionals[other_user - 1].Task;
    }

    // Flush messages from the buffer as xml response
    var serializer = new System.Xml.Serialization.XmlSerializer(messageBuffer[other_user - 1].GetType());
    using(var stringWriter = new StringWriter())
    {
        lock(messageBuffer[other_user - 1])
        {
            serializer.Serialize(stringWriter, messageBuffer[other_user - 1]);
            messageBuffer[other_user - 1].Clear();
        }

        var xmlString = stringWriter.ToString();
        context.Response.ContentType = "application/xml";
        await context.Response.WriteAsync(xmlString);
    }
});

app.MapPost("/message", async (HttpContext context) =>
{
    var user_id = Int32.Parse(context.Request.Query["user_id"].FirstOrDefault("-1") ?? "-1");
    var other_user = user_id == 1 ? 2 : 1;
    var message = context.Request.Query["msg"].FirstOrDefault("");

    // Invalid message
    if(string.IsNullOrEmpty(message))
    {
        return;
    }

    // There is no other peer connected
    else if(connectionTypes[other_user - 1] == ConnectionType.None)
    {
        Console.WriteLine($"User {user_id}: Caching message: {message}");
        messageCache[user_id - 1] = message;
    }

    // Other user is connected
    else
    {
        Console.WriteLine($"User {user_id}: Sending message: {message}");
        // Append message to buffer
        lock(messageBuffer[user_id - 1])
        {
            messageBuffer[user_id - 1].Add(message);
        }

        // Signal other one and reset it
        signalMessage(user_id);
    }
});

app.UseWebSockets();

app.Map("/ws", async (HttpContext context) =>
{
    /******************
        Validation
     ******************/

    var conn_type = Int32.Parse(context.Request.Query["connection_type"].First());
    Console.WriteLine($"Called join! | Connection type: {conn_type}");

    // New user trying to join full room
    if(connectedUsers.Count == 2)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("Room is full!");
        Console.WriteLine($"User could not join due to full room!");
        return;
    }

    var id = connectedUsers.Contains(1) ? 2 : 1;
    var other_user = id == 1 ? 2 : 1;
    connectedUsers.Add(id);
    connectionTypes[id - 1] = (ConnectionType)conn_type;
    Console.WriteLine($"Joined user id: {id}");

    // Send initial data
    string xmlResponse = null;
    using (StringWriter stringWriter = new StringWriter())
    {
        // Create an XmlWriter
        using (XmlWriter xmlWriter = XmlWriter.Create(stringWriter))
        {
            // Start writing the XML document
            xmlWriter.WriteStartDocument();
                
            // Start the root element
            xmlWriter.WriteStartElement("Response");

            // Write the integer element
            xmlWriter.WriteElementString("UserId", $"{id}");

            if(!string.IsNullOrEmpty(messageCache[other_user - 1]))
            {
                xmlWriter.WriteElementString("InitialMessage", messageCache[other_user - 1]);
                messageCache[other_user - 1] = string.Empty;
            }

            // End the root element
            xmlWriter.WriteEndElement();

            // End the XML document
            xmlWriter.WriteEndDocument();
        }

        // Get the XML string from the StringWriter
        xmlResponse = stringWriter.ToString();
    }

    /******************
      Handling Client
     ******************/

    if (context.WebSockets.IsWebSocketRequest)
    {
        await HandleWebSocket(await context.WebSockets.AcceptWebSocketAsync(), id, xmlResponse);
    }
    else
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
    }

    /******************
          Cleanup
     ******************/
    Console.WriteLine($"Called leave! User id: {id}");
    connectedUsers.Remove(id);
    connectionTypes[id - 1] = 0;
    lock(messageBuffer[other_user - 1])
    {
        messageBuffer[other_user - 1].Clear();
    }
});

async Task HandleWebSocket(WebSocket socket, int user_id, string? initialMessage = null)
{
    // Send initial data
    if(!string.IsNullOrEmpty(initialMessage))
    {
        var arraySegment = new ArraySegment<byte>(Encoding.UTF8.GetBytes(initialMessage));
        await socket.SendAsync(arraySegment, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    var other_user = user_id == 1 ? 2 : 1;
    var socketClose = new TaskCompletionSource<object>();

    // Sender -> waits for other user messages and sends to current user via websocket
    var senderTask = Task.Run(async () =>
    {
        while(socket.State == WebSocketState.Open)
        {
            // Flush messages from the buffer as xml response
            ArraySegment<byte> arraySegment;
            lock(messageBuffer[other_user - 1])
            {
                var serializer = new System.Xml.Serialization.XmlSerializer(messageBuffer[other_user - 1].GetType());
                using(var stringWriter = new StringWriter())
                {
                    serializer.Serialize(stringWriter, messageBuffer[other_user - 1]);
                    messageBuffer[other_user - 1].Clear();

                    var xmlString = stringWriter.ToString();
                    arraySegment = new ArraySegment<byte>(Encoding.UTF8.GetBytes(xmlString));
                }
            }

            // Sends message to current user
            await socket.SendAsync(arraySegment, WebSocketMessageType.Text, true, CancellationToken.None);

            // Awaits for signal (aka task finish) for new messages in buffer
            Console.WriteLine($"User {user_id} awaiting signal! {conditionals[other_user - 1].Task.IsCompleted} {socketClose.Task.IsCompleted}");
            await Task.Delay(100);
            await Task.WhenAny(conditionals[other_user - 1].Task, socketClose.Task);
        }
        Console.WriteLine($"WebSocket client {user_id} leave! - From sender");
    });

    // Receiver -> waits for messages from current user and signals other one
    var receiverTask = Task.Run(async () =>
    {
        while(socket.State == WebSocketState.Open)
        {
            Console.WriteLine($"User {user_id} awaiting message!");

            byte[] buffer = new byte[1024];
            var res = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

            if(res.MessageType == WebSocketMessageType.Close)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                Console.WriteLine($"WebSocket client {user_id} leave! - From receiver 1");
                socketClose.SetResult(null);
                break;
            }

            Console.WriteLine($"Received websocket msg! {Encoding.UTF8.GetString(buffer, 0, res.Count)}");

            var clientMsg = JsonConvert.DeserializeObject<ClientMessage>(Encoding.UTF8.GetString(buffer, 0, res.Count));

            // Fill the message buffer
            if(connectedUsers.Contains(other_user)) 
            {
                Console.WriteLine($"User {user_id}: Sending message: {clientMsg.msg}");

                lock(messageBuffer[user_id - 1])
                {
                    messageBuffer[user_id - 1].Add(clientMsg.msg);
                }

                // Signal other one and reset it
                signalMessage(user_id);
            }
            // Fill the cache
            else
            {
                Console.WriteLine($"User {user_id}: Caching message: {clientMsg.msg}");
                messageCache[user_id - 1] = clientMsg.msg;
            }
        }

        Console.WriteLine($"WebSocket client {user_id} leave! - From receiver 0");
    });
    
    await Task.WhenAll(senderTask, receiverTask);
}

app.Run();

public enum ConnectionType
{
    None        = 0,
    Poll        = 1,
    LongPoll    = 2,
    WebSocket   = 3,
};

public struct ClientMessage
{
    public int user_id;
    public string msg;
}