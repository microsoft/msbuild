// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Build.UnitTests;
using Microsoft.Build.Utilities;
using Shouldly;
using Xunit;
using Task = System.Threading.Tasks.Task;

#nullable disable

namespace Microsoft.Build.Tasks.UnitTests
{
    public class DownloadFile_Tests
    {
        private readonly MockEngine _mockEngine = new MockEngine();

        [Fact]
        public void CanBeCanceled()
        {
            using (TestEnvironment testEnvironment = TestEnvironment.Create())
            {
                TransientTestFolder folder = testEnvironment.CreateFolder(createFolder: true);

                DownloadFile downloadFile = new DownloadFile
                {
                    BuildEngine = _mockEngine,
                    DestinationFolder = new TaskItem(folder.Path),
                    HttpMessageHandler = new MockHttpMessageHandler((message, token) => new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StreamContent(new FakeStream()),
                        RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://largedownload/foo.txt")
                    }),
                    SourceUrl = "http://largedownload/foo.txt"
                };

                Task<bool> task = Task.Run(() => downloadFile.Execute());

                downloadFile.Cancel();

                task.Wait(TimeSpan.FromSeconds(1500)).ShouldBeTrue();

                task.Result.ShouldBeFalse();
            }
        }

        [Fact]
        public void CanDownloadToFolder()
        {
            using (TestEnvironment testEnvironment = TestEnvironment.Create())
            {
                TransientTestFolder folder = testEnvironment.CreateFolder(createFolder: false);

                DownloadFile downloadFile = new DownloadFile
                {
                    BuildEngine = _mockEngine,
                    DestinationFolder = new TaskItem(folder.Path),
                    HttpMessageHandler = new MockHttpMessageHandler((message, token) => new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("Success!"),
                        RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://success/foo.txt")
                    }),
                    SourceUrl = "http://success/foo.txt"
                };

                downloadFile.Execute().ShouldBeTrue();

                FileInfo file = new FileInfo(Path.Combine(folder.Path, "foo.txt"));

                file.Exists.ShouldBeTrue(file.FullName);

                File.ReadAllText(file.FullName).ShouldBe("Success!");

                downloadFile.DownloadedFile.ItemSpec.ShouldBe(file.FullName);
            }
        }

        [Fact]
        public void CanGetFileNameFromResponseHeader()
        {
            const string filename = "C6DDD10A99E149F78FA11F133127BF38.txt";

            using HttpResponseMessage response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("Success!")
                {
                    Headers =
                    {
                        ContentDisposition = new ContentDispositionHeaderValue(DispositionTypeNames.Attachment)
                        {
                            FileName = filename
                        }
                    }
                },
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://success/foo.txt")
            };

            using (TestEnvironment testEnvironment = TestEnvironment.Create())
            {
                TransientTestFolder folder = testEnvironment.CreateFolder(createFolder: false);

                DownloadFile downloadFile = new DownloadFile
                {
                    BuildEngine = _mockEngine,
                    DestinationFolder = new TaskItem(folder.Path),
                    DestinationFileName = new TaskItem(filename),
                    HttpMessageHandler = new MockHttpMessageHandler((message, token) => response),
                    SourceUrl = "http://success/foo.txt"
                };

                downloadFile.Execute().ShouldBeTrue();

                FileInfo file = new FileInfo(Path.Combine(folder.Path, filename));

                file.Exists.ShouldBeTrue(file.FullName);

                File.ReadAllText(file.FullName).ShouldBe("Success!");

                downloadFile.DownloadedFile.ItemSpec.ShouldBe(file.FullName);
            }
        }

        [Fact]
        public void CanSpecifyFileName()
        {
            const string filename = "4FD96E4A322842ACB70C40FC16E69A55.txt";

            using (TestEnvironment testEnvironment = TestEnvironment.Create())
            {
                TransientTestFolder folder = testEnvironment.CreateFolder(createFolder: false);

                DownloadFile downloadFile = new DownloadFile
                {
                    BuildEngine = _mockEngine,
                    DestinationFolder = new TaskItem(folder.Path),
                    DestinationFileName = new TaskItem(filename),
                    HttpMessageHandler = new MockHttpMessageHandler((message, token) => new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("Success!"),
                        RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://success/foo.txt")
                    }),
                    SourceUrl = "http://success/foo.txt"
                };

                downloadFile.Execute().ShouldBeTrue();

                FileInfo file = new FileInfo(Path.Combine(folder.Path, filename));

                file.Exists.ShouldBeTrue(file.FullName);

                File.ReadAllText(file.FullName).ShouldBe("Success!");

                downloadFile.DownloadedFile.ItemSpec.ShouldBe(file.FullName);
            }
        }

        [Fact]
        public void InvalidUrlLogsError()
        {
            DownloadFile downloadFile = new DownloadFile()
            {
                BuildEngine = _mockEngine,
                SourceUrl = "&&&&&"
            };

            downloadFile.Execute().ShouldBeFalse(_mockEngine.Log);

            _mockEngine.Log.ShouldContain("MSB3921");
        }

        [Fact]
        public void NotFoundLogsError()
        {
            DownloadFile downloadFile = new DownloadFile()
            {
                BuildEngine = _mockEngine,
                HttpMessageHandler = new MockHttpMessageHandler((message, token) => new HttpResponseMessage(HttpStatusCode.NotFound)),
                SourceUrl = "http://notfound/foo.txt"
            };

            downloadFile.Execute().ShouldBeFalse(_mockEngine.Log);

            _mockEngine.Log.ShouldContain("Response status code does not indicate success: 404 (Not Found).");
        }

        [Fact]
        public void RetryOnDownloadError()
        {
            const string content = "Foo";

            bool hasThrown = false;

            using (TestEnvironment testEnvironment = TestEnvironment.Create())
            {
                TransientTestFolder folder = testEnvironment.CreateFolder(createFolder: false);

                DownloadFile downloadFile = new DownloadFile()
                {
                    BuildEngine = _mockEngine,
                    DestinationFolder = new TaskItem(folder.Path),
                    HttpMessageHandler = new MockHttpMessageHandler((message, token) => new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new MockHttpContent(content.Length, stream =>
                        {
                            if (!hasThrown)
                            {
                                hasThrown = true;
                                throw new WebException("Error", WebExceptionStatus.ReceiveFailure);
                            }

                            return new MemoryStream(Encoding.Unicode.GetBytes(content)).CopyToAsync(stream);
                        }),
                        RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://success/foo.txt")
                    }),
                    Retries = 1,
                    RetryDelayMilliseconds = 100,
                    SourceUrl = "http://success/foo.txt"
                };

                downloadFile.Execute().ShouldBeTrue(_mockEngine.Log);

                _mockEngine.Log.ShouldContain("MSB3924", customMessage: _mockEngine.Log);
            }
        }

        [Fact]
        public void RetryOnResponseError()
        {
            DownloadFile downloadFile = new DownloadFile()
            {
                BuildEngine = _mockEngine,
                HttpMessageHandler = new MockHttpMessageHandler((message, token) => new HttpResponseMessage(HttpStatusCode.RequestTimeout)),
                Retries = 1,
                RetryDelayMilliseconds = 100,
                SourceUrl = "http://notfound/foo.txt"
            };

            downloadFile.Execute().ShouldBeFalse(_mockEngine.Log);

            _mockEngine.Log.ShouldContain("MSB3924", customMessage: _mockEngine.Log);
        }

        [Fact]
        public void AbortOnTimeout()
        {
            using CancellationTokenSource timeout = new CancellationTokenSource();
            timeout.Cancel();
            DownloadFile downloadFile = new DownloadFile()
            {
                BuildEngine = _mockEngine,
                HttpMessageHandler = new MockHttpMessageHandler((message, token) =>
                {
                    // Http timeouts manifest as "OperationCanceledExceptions" from the handler, simulate that
                    throw new OperationCanceledException(timeout.Token);
                }),
                Retries = 1,
                RetryDelayMilliseconds = 100,
                SourceUrl = "http://notfound/foo.txt"
            };

            downloadFile.Execute().ShouldBeFalse(_mockEngine.Log);

            _mockEngine.Log.ShouldContain("MSB3923", customMessage: _mockEngine.Log);
        }

        [Fact]
        public async Task NoRunawayLoop()
        {
            DownloadFile downloadFile = null;
            bool failed = false;
            downloadFile = new DownloadFile()
            {
                BuildEngine = _mockEngine,
                HttpMessageHandler = new MockHttpMessageHandler((message, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    downloadFile.Cancel();
                    if (!failed)
                    {
                        failed = true;
                        return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                    }

                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("Success!"),
                        RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://success/foo.txt")
                    };
                }),
                Retries = 2,
                RetryDelayMilliseconds = 100,
                SourceUrl = "http://notfound/foo.txt"
            };

            var runaway = Task.Run(() => downloadFile.Execute());
            await Task.Delay(TimeSpan.FromSeconds(1));
            runaway.IsCompleted.ShouldBeTrue("Task did not cancel");

            var result = await runaway;
            result.ShouldBeFalse(_mockEngine.Log);
        }

        [Fact]
        public void SkipUnchangedFiles()
        {
            using (TestEnvironment testEnvironment = TestEnvironment.Create())
            {
                TransientTestFolder folder = testEnvironment.CreateFolder(createFolder: true);

                DownloadFile downloadFile = new DownloadFile
                {
                    BuildEngine = _mockEngine,
                    DestinationFolder = new TaskItem(folder.Path),
                    HttpMessageHandler = new MockHttpMessageHandler((message, token) => new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("C197675A3CC64CAA80680128CF4578C9")
                        {
                            Headers =
                            {
                                LastModified = DateTimeOffset.UtcNow.AddDays(-1)
                            }
                        },
                        RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://success/foo.txt")
                    }),
                    SkipUnchangedFiles = true,
                    SourceUrl = "http://success/foo.txt"
                };

                testEnvironment.CreateFile(folder, "foo.txt", "C197675A3CC64CAA80680128CF4578C9");

                downloadFile.Execute().ShouldBeTrue();

                _mockEngine.Log.ShouldContain("Did not download file from \"http://success/foo.txt\"", customMessage: _mockEngine.Log);
            }
        }

        [Fact]
        public void UnknownFileNameLogsError()
        {
            DownloadFile downloadFile = new DownloadFile()
            {
                BuildEngine = _mockEngine,
                HttpMessageHandler = new MockHttpMessageHandler((message, token) => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("Success!"),
                    RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://unknown/")
                }),
                SourceUrl = "http://unknown/"
            };

            downloadFile.Execute().ShouldBeFalse(_mockEngine.Log);

            _mockEngine.Log.ShouldContain("MSB3922", customMessage: _mockEngine.Log);
        }

        private sealed class MockHttpContent : HttpContent
        {
            private readonly Func<Stream, Task> _func;
            private readonly int _length;

            public MockHttpContent(int length, Func<Stream, Task> func)
            {
                _length = length;
                _func = func;
            }

            protected override Task SerializeToStreamAsync(Stream stream, TransportContext context)
            {
                return _func(stream);
            }

            protected override bool TryComputeLength(out long length)
            {
                length = _length;

                return true;
            }
        }

        private sealed class MockHttpMessageHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _func;

            public MockHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> func)
            {
                _func = func;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return Task.FromResult(_func(request, cancellationToken));
            }
        }
    }

    // Fake stream that simulates providing a single character A~Z per a couple of milliseconds without high memory cost.
    public class FakeStream : Stream
    {
        private readonly int delayMilliseconds;

        public FakeStream(int delayInMilliseconds = 20)
        {
            delayMilliseconds = delayInMilliseconds;
            Position = 0;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => long.MaxValue;
        public override long Position { get; set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            buffer[Position % count] = (byte)('A' + Position % 26);
            Position++;
            Task.Delay(delayMilliseconds).Wait();
            return 1;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotImplementedException();

        public override void SetLength(long value) => throw new NotImplementedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotImplementedException();

        public override void Flush() => throw new NotImplementedException();
    }
}
