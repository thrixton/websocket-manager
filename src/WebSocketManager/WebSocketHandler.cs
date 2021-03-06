﻿using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using WebSocketManager.Common;
using System.IO;
using System.Collections.Generic;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace WebSocketManager
{
    public abstract class WebSocketHandler : IDisposable
    {

        /*
         * KeepAlive artifacts
        */
        Timer pingTimer;
        private ILogger<WebSocketHandler> logger;
        ConcurrentDictionary<string, DateTime> socketPongMap = new ConcurrentDictionary<string, DateTime>(2, 1);
        ConcurrentDictionary<string, DateTime> socketPingMap = new ConcurrentDictionary<string, DateTime>(2, 1);

        private async void OnPingTimer(object state)
        {
            if (SendPingMessages)
            {
                TimeSpan timeoutPeriod = TimeSpan.FromSeconds(WebSocket.DefaultKeepAliveInterval.TotalSeconds * 3);

                foreach (var item in socketPongMap)
                {
                    if (item.Value < DateTime.Now.Subtract(timeoutPeriod))
                    {
                        var socket = WebSocketConnectionManager.GetSocketById(item.Key);
                        if (socket.State == WebSocketState.Open)
                        {
                            logger.LogInformation("Closing socket due to ping no ping response. LastPongResponseTime: {LastPongResponseTime}", item.Value);
                            await CloseSocketAsync(socket, WebSocketCloseStatus.Empty, null, CancellationToken.None);
                        }
                    }
                    else
                    {

                        if(socketPingMap[item.Key] > socketPongMap[item.Key])
                        {
                            logger.LogDebug("Sending ping without receiving corresponding pong");
                        }
                        await SendMessageAsync(item.Key, new Message() { Data = "ping", MessageType = MessageType.Text, Brief = "ping" });
                        socketPingMap[item.Key] = DateTime.Now;
                        logger.LogDebug("Sending websocket ping");
                    }
                }
            }
        }

        private async Task CloseSocketAsync(WebSocket socket, WebSocketCloseStatus status, string message, CancellationToken token)
        {

            logger.LogDebug("Closing websocket");
            try
            {
                if (status == WebSocketCloseStatus.Empty)
                {
                    message = null;
                }
                await socket.CloseAsync(status, message, token);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error closing socket");
            }
            finally
            {
                await OnDisconnected(socket);
            }
        }

        /// <summary>
        /// If true, will send custom "ping" messages which must be answered with a ping message
        /// Uses WebSocket.DefaultKeepAliveInterval as ping period
        /// Sockets which have not responded to 3 pings will be disconnected
        /// </summary>
        public bool SendPingMessages { get; set; }

        protected WebSocketConnectionManager WebSocketConnectionManager { get; set; }
        private JsonSerializerSettings _jsonSerializerSettings = new JsonSerializerSettings()
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver()
        };

        public WebSocketHandler(WebSocketConnectionManager webSocketConnectionManager, ILogger<WebSocketHandler> logger)
        {
            WebSocketConnectionManager = webSocketConnectionManager;
            pingTimer = new Timer(OnPingTimer, null, WebSocket.DefaultKeepAliveInterval, WebSocket.DefaultKeepAliveInterval);
            this.logger = logger;
        }

        public virtual async Task OnConnected(WebSocket socket)
        {
            logger.LogDebug("Start method. Method: {Method}", nameof(OnConnected));
            WebSocketConnectionManager.AddSocket(socket);

            string id = WebSocketConnectionManager.GetId(socket);

            await SendMessageAsync(socket, new Message()
            {
                MessageType = MessageType.Text,
                Brief = "connect",
                Data = id,
            });

            socketPongMap.GetOrAdd(id, DateTime.Now);
            socketPingMap.GetOrAdd(id, DateTime.Now);
            logger.LogDebug("Finish method. Method: {Method}", nameof(OnConnected));

        }

        public virtual async Task OnDisconnected(WebSocket socket)
        {
            string id = WebSocketConnectionManager.GetId(socket);
            DateTime temp;
            socketPongMap.TryRemove(id, out temp);
            socketPingMap.TryRemove(id, out temp);
            await WebSocketConnectionManager.RemoveSocket(WebSocketConnectionManager.GetId(socket));
        }


        ConcurrentQueue<Tuple<WebSocket, WebSocketMessageType, byte[]>> sendQueue = new ConcurrentQueue<Tuple<WebSocket, WebSocketMessageType, byte[]>>();

        public async Task SendMessageAsync(WebSocket socket, WebSocketMessageType messageType, byte[] messageData)
        {

            if (socket.State != WebSocketState.Open)
                return;

            sendQueue.Enqueue(new Tuple<WebSocket, WebSocketMessageType, byte[]>(socket, messageType, messageData));
            await Task.Run((Action)SendmessagesInQueue);
        }

        protected void SendmessagesInQueue()
        {
            while (!sendQueue.IsEmpty)
            {
                Tuple<WebSocket, WebSocketMessageType, byte[]> item;

                if (sendQueue.TryDequeue(out item))
                {
                    try
                    {
                        item.Item1.SendAsync(buffer: new ArraySegment<byte>(array: item.Item3,
                                          offset: 0,
                                          count: item.Item3.Length),
                                          messageType: item.Item2,
                                          endOfMessage: true,
                                          cancellationToken: CancellationToken.None).Wait();

                    }
                    catch (Exception x)
                    {
                        logger.LogError(x, "Error sending message via socket. Message dropped");
                    }
                }
            }
        }

        public async Task SendMessageAsync(WebSocket socket, Message message)
        {
            var serializedMessage = JsonConvert.SerializeObject(message, _jsonSerializerSettings);
            var encodedMessage = Encoding.UTF8.GetBytes(serializedMessage);

            await SendMessageAsync(socket, WebSocketMessageType.Text, encodedMessage);
        }

        public async Task SendMessageAsync(string socketId, Message message)
        {
            await SendMessageAsync(WebSocketConnectionManager.GetSocketById(socketId), message);
        }

        public async Task SendMessageToAllAsync(Message message)
        {
            foreach (var pair in WebSocketConnectionManager.GetAll())
            {
                if (pair.Value.State == WebSocketState.Open)
                    await SendMessageAsync(pair.Value, message);
            }
        }

        public async Task InvokeClientMethodAsync(string socketId, string methodName, object[] arguments)
        {
            var message = new Message()
            {
                MessageType = MessageType.ClientMethodInvocation,
                Data = JsonConvert.SerializeObject(new InvocationDescriptor()
                {
                    MethodName = methodName,
                    Arguments = arguments
                }, _jsonSerializerSettings)
            };

            await SendMessageAsync(socketId, message);
        }

        public async Task InvokeClientMethodToAllAsync(string methodName, params object[] arguments)
        {
            foreach (var pair in WebSocketConnectionManager.GetAll())
            {
                if (pair.Value.State == WebSocketState.Open)
                    await InvokeClientMethodAsync(pair.Key, methodName, arguments);
            }
        }

        public async Task SendMessageToGroupAsync(string groupID, Message message)
        {
            var sockets = WebSocketConnectionManager.GetAllFromGroup(groupID);
            if (sockets != null)
            {
                foreach (var socket in sockets)
                {
                    await SendMessageAsync(socket, message);
                }
            }
        }

        public async Task SendMessageToGroupAsync(string groupID, Message message, string except)
        {
            var sockets = WebSocketConnectionManager.GetAllFromGroup(groupID);
            if (sockets != null)
            {
                foreach (var id in sockets)
                {
                    if (id != except)
                        await SendMessageAsync(id, message);
                }
            }
        }

        public async Task InvokeClientMethodToGroupAsync(string groupID, string methodName, params object[] arguments)
        {
            var sockets = WebSocketConnectionManager.GetAllFromGroup(groupID);
            if (sockets != null)
            {
                foreach (var id in sockets)
                {
                    await InvokeClientMethodAsync(id, methodName, arguments);
                }
            }
        }

        public async Task InvokeClientMethodToGroupAsync(string groupID, string methodName, string except, params object[] arguments)
        {
            var sockets = WebSocketConnectionManager.GetAllFromGroup(groupID);
            if (sockets != null)
            {
                foreach (var id in sockets)
                {
                    if (id != except)
                        await InvokeClientMethodAsync(id, methodName, arguments);
                }
            }
        }

        public async Task ReceiveAsync(WebSocket socket, WebSocketReceiveResult result, string message)
        {
            var messageObject = JsonConvert.DeserializeObject<Message>(message);

            switch (messageObject.MessageType)
            {
                case MessageType.ClientMethodInvocation:

                    var textMessage = JsonConvert.DeserializeObject<Message>(message);


                    var invocationDescriptor = JsonConvert.DeserializeObject<InvocationDescriptor>(textMessage.Data);
                    var method = this.GetType().GetMethod(invocationDescriptor.MethodName);

                    if (method == null)
                    {
                        await SendMessageAsync(socket, new Message()
                        {
                            MessageType = MessageType.Text,
                            Data = $"Cannot find method {invocationDescriptor.MethodName}"
                        });
                        return;
                    }

                    try
                    {
                        method.Invoke(this, invocationDescriptor.Arguments);
                    }
                    catch (TargetParameterCountException)
                    {
                        await SendMessageAsync(socket, new Message()
                        {
                            MessageType = MessageType.Text,
                            Data = $"The {invocationDescriptor.MethodName} method does not take {invocationDescriptor.Arguments.Length} parameters!"
                        });
                    }

                    catch (ArgumentException)
                    {
                        await SendMessageAsync(socket, new Message()
                        {
                            MessageType = MessageType.Text,
                            Data = $"The {invocationDescriptor.MethodName} method takes different arguments!"
                        });
                    }
                    break;


                case MessageType.Text:
                    logger.LogDebug("Text message received. Brief: {Brief}, Data: {Data}", messageObject.Brief, messageObject.Data);
                    switch (messageObject.Brief)
                    {
                        case MessageBriefConstants.Ping:
                            //server shouldn't send pong
                            break;
                        case MessageBriefConstants.Pong:
                            this.OnPong(socket);
                            break;
                        case MessageBriefConstants.Disconnect:
                            await CloseSocketAsync(socket, WebSocketCloseStatus.NormalClosure, MessageBriefConstants.Disconnect, CancellationToken.None);
                            break;
                    }
                    break;

                default:
                    this.OnMessage(messageObject);
                    break;
            }
        }

        private void OnMessage(Message messageObject)
        {
            throw new NotImplementedException();
        }

        private void OnPong(WebSocket socket)
        {
            logger.LogDebug("Pong message received");
            string id = WebSocketConnectionManager.GetId(socket);
            socketPongMap[id] = DateTime.Now;
            var elapsed = socketPongMap[id].Subtract(socketPingMap[id]);
            logger.LogDebug("PingPong latency. LatencyMs:{LatencyMs}", elapsed.TotalMilliseconds);
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
            pingTimer.Dispose();
        }
    }
}