﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNet.Server.Kestrel;
using Microsoft.AspNet.Server.Kestrel.Filter;
using Microsoft.AspNet.Server.Kestrel.Http;
using Microsoft.Dnx.Runtime;
using Microsoft.Dnx.Runtime.Infrastructure;
using Xunit;

namespace Microsoft.AspNet.Server.KestrelTests
{
    /// <summary>
    /// Summary description for EngineTests
    /// </summary>
    public class EngineTests
    {
        public static TheoryData<ServiceContext> ConnectionFilterData
        {
            get
            {
                return new TheoryData<ServiceContext>
                {
                    {
                        new TestServiceContext()
                    },
                    {
                        new TestServiceContext
                        {
                            ConnectionFilter = new NoOpConnectionFilter()
                        }
                    }
                };
            }
        }

        private async Task App(Frame frame)
        {
            frame.ResponseHeaders.Clear();
            while (true)
            {
                var buffer = new byte[8192];
                var count = await frame.RequestBody.ReadAsync(buffer, 0, buffer.Length);
                if (count == 0)
                {
                    break;
                }
                await frame.ResponseBody.WriteAsync(buffer, 0, count);
            }
        }

        ILibraryManager LibraryManager
        {
            get
            {
                try
                {
                    var locator = CallContextServiceLocator.Locator;
                    if (locator == null)
                    {
                        return null;
                    }
                    var services = locator.ServiceProvider;
                    if (services == null)
                    {
                        return null;
                    }
                    return (ILibraryManager)services.GetService(typeof(ILibraryManager));
                }
                catch (NullReferenceException)
                { return null; }
            }
        }

        private async Task AppChunked(Frame frame)
        {
            var data = new MemoryStream();
            await frame.RequestBody.CopyToAsync(data);
            var bytes = data.ToArray();

            frame.ResponseHeaders.Clear();
            frame.ResponseHeaders["Content-Length"] = new[] { bytes.Length.ToString() };
            await frame.ResponseBody.WriteAsync(bytes, 0, bytes.Length);
        }

        private Task EmptyApp(Frame frame)
        {
            frame.ResponseHeaders.Clear();
            return Task.FromResult<object>(null);
        }

        [Theory]
        [MemberData(nameof(ConnectionFilterData))]
        public void EngineCanStartAndStop(ServiceContext testContext)
        {
            var engine = new KestrelEngine(LibraryManager, testContext);
            engine.Start(1);
            engine.Dispose();
        }

        [Theory]
        [MemberData(nameof(ConnectionFilterData))]
        public void ListenerCanCreateAndDispose(ServiceContext testContext)
        {
            var engine = new KestrelEngine(LibraryManager, testContext);
            engine.Start(1);
            var address = ServerAddress.FromUrl("http://localhost:54321/");
            var started = engine.CreateServer(address, App);
            started.Dispose();
            engine.Dispose();
        }

        [Theory]
        [MemberData(nameof(ConnectionFilterData))]
        public void ConnectionCanReadAndWrite(ServiceContext testContext)
        {
            var engine = new KestrelEngine(LibraryManager, testContext);
            engine.Start(1);
            var address = ServerAddress.FromUrl("http://localhost:54321/");
            var started = engine.CreateServer(address, App);

            Console.WriteLine("Started");
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.Connect(new IPEndPoint(IPAddress.Loopback, 54321));
            socket.Send(Encoding.ASCII.GetBytes("POST / HTTP/1.0\r\n\r\nHello World"));
            socket.Shutdown(SocketShutdown.Send);
            var buffer = new byte[8192];
            for (;;)
            {
                var length = socket.Receive(buffer);
                if (length == 0) { break; }
                var text = Encoding.ASCII.GetString(buffer, 0, length);
            }
            started.Dispose();
            engine.Dispose();
        }


        [Theory]
        [MemberData(nameof(ConnectionFilterData))]
        public async Task Http10(ServiceContext testContext)
        {
            using (var server = new TestServer(App, testContext))
            {
                using (var connection = new TestConnection())
                {
                    await connection.SendEnd(
                        "POST / HTTP/1.0",
                        "",
                        "Hello World");
                    await connection.ReceiveEnd(
                        "HTTP/1.0 200 OK",
                        "",
                        "Hello World");
                }
            }
        }


        [Theory]
        [MemberData(nameof(ConnectionFilterData))]
        public async Task Http11(ServiceContext testContext)
        {
            using (var server = new TestServer(AppChunked, testContext))
            {
                using (var connection = new TestConnection())
                {
                    await connection.SendEnd(
                        "GET / HTTP/1.1",
                        "",
                        "GET / HTTP/1.1",
                        "Connection: close",
                        "",
                        "Goodbye");
                    await connection.ReceiveEnd(
                        "HTTP/1.1 200 OK",
                        "Content-Length: 0",
                        "",
                        "HTTP/1.1 200 OK",
                        "Content-Length: 7",
                        "Connection: close",
                        "",
                        "Goodbye");
                }
            }
        }

        [Theory]
        [MemberData(nameof(ConnectionFilterData))]
        public async Task Http10ContentLength(ServiceContext testContext)
        {
            using (var server = new TestServer(App, testContext))
            {
                using (var connection = new TestConnection())
                {
                    await connection.Send(
                        "POST / HTTP/1.0",
                        "Content-Length: 11",
                        "",
                        "Hello World");
                    await connection.ReceiveEnd(
                        "HTTP/1.0 200 OK",
                        "",
                        "Hello World");
                }
            }
        }

        [Theory]
        [MemberData(nameof(ConnectionFilterData))]
        public async Task Http10TransferEncoding(ServiceContext testContext)
        {
            using (var server = new TestServer(App, testContext))
            {
                using (var connection = new TestConnection())
                {
                    await connection.Send(
                        "POST / HTTP/1.0",
                        "Transfer-Encoding: chunked",
                        "",
                        "5", "Hello", "6", " World", "0\r\n");
                    await connection.ReceiveEnd(
                        "HTTP/1.0 200 OK",
                        "",
                        "Hello World");
                }
            }
        }

        [Theory]
        [MemberData(nameof(ConnectionFilterData))]
        public async Task Http10KeepAlive(ServiceContext testContext)
        {
            using (var server = new TestServer(AppChunked, testContext))
            {
                using (var connection = new TestConnection())
                {
                    await connection.SendEnd(
                        "GET / HTTP/1.0",
                        "Connection: keep-alive",
                        "",
                        "POST / HTTP/1.0",
                        "",
                        "Goodbye");
                    await connection.Receive(
                        "HTTP/1.0 200 OK",
                        "Content-Length: 0",
                        "Connection: keep-alive",
                        "\r\n");
                    await connection.ReceiveEnd(
                        "HTTP/1.0 200 OK",
                        "Content-Length: 7",
                        "",
                        "Goodbye");
                }
            }
        }

        [Theory]
        [MemberData(nameof(ConnectionFilterData))]
        public async Task Http10KeepAliveNotUsedIfResponseContentLengthNotSet(ServiceContext testContext)
        {
            using (var server = new TestServer(App, testContext))
            {
                using (var connection = new TestConnection())
                {
                    await connection.SendEnd(
                        "GET / HTTP/1.0",
                        "Connection: keep-alive",
                        "",
                        "POST / HTTP/1.0",
                        "Connection: keep-alive",
                        "Content-Length: 7",
                        "",
                        "Goodbye");
                    await connection.Receive(
                        "HTTP/1.0 200 OK",
                        "Content-Length: 0",
                        "Connection: keep-alive",
                        "\r\n");
                    await connection.ReceiveEnd(
                        "HTTP/1.0 200 OK",
                        "",
                        "Goodbye");
                }
            }
        }

        [Theory]
        [MemberData(nameof(ConnectionFilterData))]
        public async Task Http10KeepAliveContentLength(ServiceContext testContext)
        {
            using (var server = new TestServer(AppChunked))
            {
                using (var connection = new TestConnection())
                {
                    await connection.SendEnd(
                        "POST / HTTP/1.0",
                        "Connection: keep-alive",
                        "Content-Length: 11",
                        "",
                        "Hello WorldPOST / HTTP/1.0",
                        "",
                        "Goodbye");
                    await connection.Receive(
                        "HTTP/1.0 200 OK",
                        "Content-Length: 11",
                        "Connection: keep-alive",
                        "",
                        "Hello World");
                    await connection.ReceiveEnd(
                        "HTTP/1.0 200 OK",
                        "Content-Length: 7",
                        "",
                        "Goodbye");
                }
            }
        }

        [Theory]
        [MemberData(nameof(ConnectionFilterData))]
        public async Task Http10KeepAliveTransferEncoding(ServiceContext testContext)
        {
            using (var server = new TestServer(AppChunked))
            {
                using (var connection = new TestConnection())
                {
                    await connection.SendEnd(
                        "POST / HTTP/1.0",
                        "Transfer-Encoding: chunked",
                        "Connection: keep-alive",
                        "",
                        "5", "Hello", "6", " World", "0",
                        "POST / HTTP/1.0",
                        "",
                        "Goodbye");
                    await connection.Receive(
                        "HTTP/1.0 200 OK",
                        "Content-Length: 11",
                        "Connection: keep-alive",
                        "",
                        "Hello World");
                    await connection.ReceiveEnd(
                        "HTTP/1.0 200 OK",
                        "Content-Length: 7",
                        "",
                        "Goodbye");
                }
            }
        }

        [Theory]
        [MemberData(nameof(ConnectionFilterData))]
        public async Task Expect100ContinueForBody(ServiceContext testContext)
        {
            using (var server = new TestServer(AppChunked, testContext))
            {
                using (var connection = new TestConnection())
                {
                    await connection.Send(
                        "POST / HTTP/1.1",
                        "Expect: 100-continue",
                        "Content-Length: 11",
                        "Connection: close",
                        "\r\n");
                    await connection.Receive("HTTP/1.1 100 Continue", "\r\n");
                    await connection.SendEnd("Hello World");
                    await connection.Receive(
                        "HTTP/1.1 200 OK",
                        "Content-Length: 11",
                        "Connection: close",
                        "",
                        "Hello World");
                }
            }
        }

        [Theory]
        [MemberData(nameof(ConnectionFilterData))]
        public async Task DisconnectingClient(ServiceContext testContext)
        {
            using (var server = new TestServer(App, testContext))
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.IP);
                socket.Connect(IPAddress.Loopback, 54321);
                await Task.Delay(200);
                socket.Disconnect(false);
                socket.Dispose();

                await Task.Delay(200);
                using (var connection = new TestConnection())
                {
                    await connection.SendEnd(
                        "GET / HTTP/1.0",
                        "\r\n");
                    await connection.ReceiveEnd(
                        "HTTP/1.0 200 OK",
                        "\r\n");
                }
            }
        }

        [Theory]
        [MemberData(nameof(ConnectionFilterData))]
        public async Task ZeroContentLengthSetAutomaticallyAfterNoWrites(ServiceContext testContext)
        {
            using (var server = new TestServer(EmptyApp, testContext))
            {
                using (var connection = new TestConnection())
                {
                    await connection.SendEnd(
                        "GET / HTTP/1.1",
                        "",
                        "GET / HTTP/1.0",
                        "Connection: keep-alive",
                        "",
                        "");
                    await connection.ReceiveEnd(
                        "HTTP/1.1 200 OK",
                        "Content-Length: 0",
                        "",
                        "HTTP/1.0 200 OK",
                        "Content-Length: 0",
                        "Connection: keep-alive",
                        "",
                        "");
                }
            }
        }

        [Theory]
        [MemberData(nameof(ConnectionFilterData))]
        public async Task ZeroContentLengthNotSetAutomaticallyForNonKeepAliveRequests(ServiceContext testContext)
        {
            using (var server = new TestServer(EmptyApp, testContext))
            {
                using (var connection = new TestConnection())
                {
                    await connection.SendEnd(
                        "GET / HTTP/1.1",
                        "Connection: close",
                        "",
                        "");
                    await connection.ReceiveEnd(
                        "HTTP/1.1 200 OK",
                        "Connection: close",
                        "",
                        "");
                }

                using (var connection = new TestConnection())
                {
                    await connection.SendEnd(
                        "GET / HTTP/1.0",
                        "",
                        "");
                    await connection.ReceiveEnd(
                        "HTTP/1.0 200 OK",
                        "",
                        "");
                }
            }
        }

        [Theory]
        [MemberData(nameof(ConnectionFilterData))]
        public async Task ZeroContentLengthNotSetAutomaticallyForHeadRequests(ServiceContext testContext)
        {
            using (var server = new TestServer(EmptyApp, testContext))
            {
                using (var connection = new TestConnection())
                {
                    await connection.SendEnd(
                        "HEAD / HTTP/1.1",
                        "",
                        "");
                    await connection.ReceiveEnd(
                        "HTTP/1.1 200 OK",
                        "",
                        "");
                }
            }
        }

        [Theory]
        [MemberData(nameof(ConnectionFilterData))]
        public async Task ZeroContentLengthNotSetAutomaticallyForCertainStatusCodes(ServiceContext testContext)
        {
            using (var server = new TestServer(async frame =>
            {
                frame.ResponseHeaders.Clear();

                using (var reader = new StreamReader(frame.RequestBody, Encoding.ASCII))
                {
                    var statusString = await reader.ReadLineAsync();
                    frame.StatusCode = int.Parse(statusString);
                }
            }, testContext))
            {
                using (var connection = new TestConnection())
                {
                    await connection.SendEnd(
                        "POST / HTTP/1.1",
                        "Content-Length: 3",
                        "",
                        "101POST / HTTP/1.1",
                        "Content-Length: 3",
                        "",
                        "204POST / HTTP/1.1",
                        "Content-Length: 3",
                        "",
                        "205POST / HTTP/1.1",
                        "Content-Length: 3",
                        "",
                        "304POST / HTTP/1.1",
                        "Content-Length: 3",
                        "",
                        "200");
                    await connection.ReceiveEnd(
                        "HTTP/1.1 101 Switching Protocols",
                        "",
                        "HTTP/1.1 204 No Content",
                        "",
                        "HTTP/1.1 205 Reset Content",
                        "",
                        "HTTP/1.1 304 Not Modified",
                        "",
                        "HTTP/1.1 200 OK",
                        "Content-Length: 0",
                        "",
                        "");
                }
            }
        }

        [Theory]
        [MemberData(nameof(ConnectionFilterData))]
        public async Task ThrowingResultsIn500Response(ServiceContext testContext)
        {
            bool onStartingCalled = false;

            using (var server = new TestServer(frame =>
            {
                frame.OnStarting(_ =>
                {
                    onStartingCalled = true;
                    return Task.FromResult<object>(null);
                }, null);

                // Anything added to the ResponseHeaders dictionary is ignored
                frame.ResponseHeaders.Clear();
                frame.ResponseHeaders["Content-Length"] = new[] { "11" };
                throw new Exception();
            }, testContext))
            {
                using (var connection = new TestConnection())
                {
                    await connection.SendEnd(
                        "GET / HTTP/1.1",
                        "",
                        "GET / HTTP/1.1",
                        "Connection: close",
                        "",
                        "");
                    await connection.Receive(
                        "HTTP/1.1 500 Internal Server Error",
                        "");
                    await connection.ReceiveStartsWith("Date:");
                    await connection.Receive(
                        "Content-Length: 0",
                        "Server: Kestrel",
                        "",
                        "HTTP/1.1 500 Internal Server Error",
                        "");
                    await connection.ReceiveStartsWith("Date:");
                    await connection.ReceiveEnd(
                        "Content-Length: 0",
                        "Server: Kestrel",
                        "Connection: close",
                        "",
                        "");

                    Assert.False(onStartingCalled);
                }
            }
        }

        [Theory]
        [MemberData(nameof(ConnectionFilterData))]
        public async Task ThrowingAfterWritingKillsConnection(ServiceContext testContext)
        {
            bool onStartingCalled = false;

            using (var server = new TestServer(async frame =>
            {
                frame.OnStarting(_ =>
                {
                    onStartingCalled = true;
                    return Task.FromResult<object>(null);
                }, null);

                frame.ResponseHeaders.Clear();
                frame.ResponseHeaders["Content-Length"] = new[] { "11" };
                await frame.ResponseBody.WriteAsync(Encoding.ASCII.GetBytes("Hello World"), 0, 11);
                throw new Exception();
            }, testContext))
            {
                using (var connection = new TestConnection())
                {
                    await connection.Send(
                        "GET / HTTP/1.1",
                        "",
                        "");
                    await connection.ReceiveEnd(
                        "HTTP/1.1 200 OK",
                        "Content-Length: 11",
                        "",
                        "Hello World");

                    Assert.True(onStartingCalled);
                }
            }
        }

        [Theory]
        [MemberData(nameof(ConnectionFilterData))]
        public async Task ThrowingAfterPartialWriteKillsConnection(ServiceContext testContext)
        {
            bool onStartingCalled = false;

            using (var server = new TestServer(async frame =>
            {
                frame.OnStarting(_ =>
                {
                    onStartingCalled = true;
                    return Task.FromResult<object>(null);
                }, null);

                frame.ResponseHeaders.Clear();
                frame.ResponseHeaders["Content-Length"] = new[] { "11" };
                await frame.ResponseBody.WriteAsync(Encoding.ASCII.GetBytes("Hello"), 0, 5);
                throw new Exception();
            }, testContext))
            {
                using (var connection = new TestConnection())
                {
                    await connection.Send(
                        "GET / HTTP/1.1",
                        "",
                        "");
                    await connection.ReceiveEnd(
                        "HTTP/1.1 200 OK",
                        "Content-Length: 11",
                        "",
                        "Hello");

                    Assert.True(onStartingCalled);
                }
            }
        }

        [Theory]
        [MemberData(nameof(ConnectionFilterData))]
        public async Task ConnectionClosesWhenFinReceived(ServiceContext testContext)
        {
            using (var server = new TestServer(AppChunked, testContext))
            {
                using (var connection = new TestConnection())
                {
                    await connection.SendEnd(
                        "GET / HTTP/1.1",
                        "",
                        "Post / HTTP/1.1",
                        "Content-Length: 7",
                        "",
                        "Goodbye");
                    await connection.ReceiveEnd(
                        "HTTP/1.1 200 OK",
                        "Content-Length: 0",
                        "",
                        "HTTP/1.1 200 OK",
                        "Content-Length: 7",
                        "",
                        "Goodbye");
                }
            }
        }

        [Theory]
        [MemberData(nameof(ConnectionFilterData))]
        public async Task ConnectionClosesWhenFinReceivedBeforeRequestCompletes(ServiceContext testContext)
        {
            using (var server = new TestServer(AppChunked, testContext))
            {
                using (var connection = new TestConnection())
                {
                    await connection.SendEnd(
                        "GET /");
                    await connection.ReceiveEnd();
                }

                using (var connection = new TestConnection())
                {
                    await connection.SendEnd(
                        "GET / HTTP/1.1",
                        "",
                        "Post / HTTP/1.1");
                    await connection.ReceiveEnd(
                        "HTTP/1.1 200 OK",
                        "Content-Length: 0",
                        "",
                        "");
                }

                using (var connection = new TestConnection())
                {
                    await connection.SendEnd(
                        "GET / HTTP/1.1",
                        "",
                        "Post / HTTP/1.1",
                        "Content-Length: 7");
                    await connection.ReceiveEnd(
                        "HTTP/1.1 200 OK",
                        "Content-Length: 0",
                        "",
                        "");
                }
            }
        }

        [Theory]
        [MemberData(nameof(ConnectionFilterData))]
        public async Task ThrowingInOnStartingResultsIn500Response(ServiceContext testContext)
        {
            using (var server = new TestServer(frame =>
            {
                frame.OnStarting(_ =>
                {
                    throw new Exception();
                }, null);

                frame.ResponseHeaders.Clear();
                frame.ResponseHeaders["Content-Length"] = new[] { "11" };

                // If we write to the response stream, we will not get a 500.

                return Task.FromResult<object>(null);
            }, testContext))
            {
                using (var connection = new TestConnection())
                {
                    await connection.SendEnd(
                        "GET / HTTP/1.1",
                        "",
                        "GET / HTTP/1.1",
                        "Connection: close",
                        "",
                        "");
                    await connection.Receive(
                        "HTTP/1.1 500 Internal Server Error",
                        "");
                    await connection.ReceiveStartsWith("Date:");
                    await connection.Receive(
                        "Content-Length: 0",
                        "Server: Kestrel",
                        "",
                        "HTTP/1.1 500 Internal Server Error",
                        "");
                    await connection.ReceiveStartsWith("Date:");
                    await connection.ReceiveEnd(
                        "Content-Length: 0",
                        "Server: Kestrel",
                        "Connection: close",
                        "",
                        "");
                }
            }
        }

        [Theory]
        [MemberData(nameof(ConnectionFilterData))]
        public async Task ThrowingInOnStartingResultsInFailedWrites(ServiceContext testContext)
        {
            using (var server = new TestServer(async frame =>
            {
                var onStartingException = new Exception();

                frame.OnStarting(_ =>
                {
                    throw onStartingException;
                }, null);

                frame.ResponseHeaders.Clear();
                frame.ResponseHeaders["Content-Length"] = new[] { "11" };

                var writeException = await Assert.ThrowsAsync<Exception>(async () =>
                    await frame.ResponseBody.WriteAsync(Encoding.ASCII.GetBytes("Hello World"), 0, 11));

                Assert.Same(onStartingException, writeException);

                // The second write should succeed since the OnStarting callback will not be called again
                await frame.ResponseBody.WriteAsync(Encoding.ASCII.GetBytes("Exception!!"), 0, 11);
            }, testContext))
            {
                using (var connection = new TestConnection())
                {
                    await connection.Send(
                        "GET / HTTP/1.1",
                        "",
                        "");
                    await connection.Receive(
                        "HTTP/1.1 200 OK",
                        "Content-Length: 11",
                        "",
                        "Exception!!"); ;
                }
            }
        }
    }
}
