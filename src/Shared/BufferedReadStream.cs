// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;

#if NET451_OR_GREATER || NETCOREAPP
using System.Threading.Tasks;
#endif

#nullable disable

namespace Microsoft.Build.BackEnd
{
    internal class BufferedReadStream : Stream
    {
        private const int BUFFER_SIZE = 1024;
        private NamedPipeServerStream _innerStream;
        private byte[] _buffer;

        // The number of bytes in the buffer that have been read from the underlying stream but not read by consumers of this stream
        private int _currentlyBufferedByteCount;
        private int _currentIndexInBuffer;

        public BufferedReadStream(NamedPipeServerStream innerStream)
        {
            _innerStream = innerStream;
            _buffer = new byte[BUFFER_SIZE];

            _currentlyBufferedByteCount = 0;
        }

        public override bool CanRead { get { return _innerStream.CanRead; } }

        public override bool CanSeek { get { return false; } }

        public override bool CanWrite { get { return _innerStream.CanWrite; } }

        public override long Length { get { return _innerStream.Length; } }

        public override long Position
        {
            get { throw new NotSupportedException(); }
            set { throw new NotSupportedException(); }
        }

        public override void Flush()
        {
            _innerStream.Flush();
        }

        public override int ReadByte()
        {
            if (_currentlyBufferedByteCount > 0)
            {
                int ret = _buffer[_currentIndexInBuffer];
                _currentIndexInBuffer++;
                _currentlyBufferedByteCount--;
                return ret;
            }
            else
            {
                // Let the base class handle it, which will end up calling the Read() method
                return base.ReadByte();
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count > BUFFER_SIZE)
            {
                // Trying to read more data than the buffer can hold
                int alreadyCopied = 0;
                if (_currentlyBufferedByteCount > 0)
                {
                    Array.Copy(_buffer, _currentIndexInBuffer, buffer, offset, _currentlyBufferedByteCount);
                    alreadyCopied = _currentlyBufferedByteCount;
                    _currentIndexInBuffer = 0;
                    _currentlyBufferedByteCount = 0;
                }
                int innerReadCount = _innerStream.Read(buffer, offset + alreadyCopied, count - alreadyCopied);
                return innerReadCount + alreadyCopied;
            }
            else if (count <= _currentlyBufferedByteCount)
            {
                // Enough data buffered to satisfy read request
                Array.Copy(_buffer, _currentIndexInBuffer, buffer, offset, count);
                _currentIndexInBuffer += count;
                _currentlyBufferedByteCount -= count;
                return count;
            }
            else
            {
                // Need to read more data
                int alreadyCopied = 0;
                if (_currentlyBufferedByteCount > 0)
                {
                    Array.Copy(_buffer, _currentIndexInBuffer, buffer, offset, _currentlyBufferedByteCount);
                    alreadyCopied = _currentlyBufferedByteCount;
                    _currentIndexInBuffer = 0;
                    _currentlyBufferedByteCount = 0;
                }

                int innerReadCount = _innerStream.Read(_buffer, 0, BUFFER_SIZE);
                _currentIndexInBuffer = 0;
                _currentlyBufferedByteCount = innerReadCount;

                int remainingCopyCount;

                if (alreadyCopied + innerReadCount >= count)
                {
                    remainingCopyCount = count - alreadyCopied;
                }
                else
                {
                    remainingCopyCount = innerReadCount;
                }

                Array.Copy(_buffer, 0, buffer, offset + alreadyCopied, remainingCopyCount);
                _currentIndexInBuffer += remainingCopyCount;
                _currentlyBufferedByteCount -= remainingCopyCount;

                return alreadyCopied + remainingCopyCount;
            }
        }

#if NET451_OR_GREATER || NETCOREAPP
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (count > BUFFER_SIZE)
            {
                // Trying to read more data than the buffer can hold
                int alreadyCopied = 0;
                if (_currentlyBufferedByteCount > 0)
                {
                    Array.Copy(_buffer, _currentIndexInBuffer, buffer, offset, _currentlyBufferedByteCount);
                    alreadyCopied = _currentlyBufferedByteCount;
                    _currentIndexInBuffer = 0;
                    _currentlyBufferedByteCount = 0;
                }
#pragma warning disable CA1835 // Prefer the 'Memory'-based overloads for 'ReadAsync' and 'WriteAsync'
                int innerReadCount = await _innerStream.ReadAsync(buffer, offset + alreadyCopied, count - alreadyCopied, cancellationToken);
#pragma warning restore CA1835 // Prefer the 'Memory'-based overloads for 'ReadAsync' and 'WriteAsync'
                return innerReadCount + alreadyCopied;
            }
            else if (count <= _currentlyBufferedByteCount)
            {
                // Enough data buffered to satisfy read request
                Array.Copy(_buffer, _currentIndexInBuffer, buffer, offset, count);
                _currentIndexInBuffer += count;
                _currentlyBufferedByteCount -= count;
                return count;
            }
            else
            {
                // Need to read more data
                int alreadyCopied = 0;
                if (_currentlyBufferedByteCount > 0)
                {
                    Array.Copy(_buffer, _currentIndexInBuffer, buffer, offset, _currentlyBufferedByteCount);
                    alreadyCopied = _currentlyBufferedByteCount;
                    _currentIndexInBuffer = 0;
                    _currentlyBufferedByteCount = 0;
                }

#pragma warning disable CA1835 // Prefer the 'Memory'-based overloads for 'ReadAsync' and 'WriteAsync'
                int innerReadCount = await _innerStream.ReadAsync(_buffer, 0, BUFFER_SIZE, cancellationToken);
#pragma warning restore CA1835 // Prefer the 'Memory'-based overloads for 'ReadAsync' and 'WriteAsync'
                _currentIndexInBuffer = 0;
                _currentlyBufferedByteCount = innerReadCount;

                int remainingCopyCount;

                if (alreadyCopied + innerReadCount >= count)
                {
                    remainingCopyCount = count - alreadyCopied;
                }
                else
                {
                    remainingCopyCount = innerReadCount;
                }

                Array.Copy(_buffer, 0, buffer, offset + alreadyCopied, remainingCopyCount);
                _currentIndexInBuffer += remainingCopyCount;
                _currentlyBufferedByteCount -= remainingCopyCount;

                return alreadyCopied + remainingCopyCount;
            }
        }
#endif

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _innerStream.Write(buffer, offset, count);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _innerStream.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
