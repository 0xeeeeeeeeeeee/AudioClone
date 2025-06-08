using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AudioClone.CoreCapture
{
    public class MyAudioStream : Stream
    {
        private readonly Queue<byte> mBuffer = new();
        private bool mFlushed;
        private long mMaxBufferLength = 200 * MB;
        private bool mBlockLastRead;

        public const long KB = 1024;
        public const long MB = KB * 1024;

        public long MaxBufferLength
        {
            get => mMaxBufferLength;
            set
            {
                if (value < 1) throw new ArgumentOutOfRangeException(nameof(value), "MaxBufferLength must be positive");
                mMaxBufferLength = value;
            }
        }

        public bool BlockLastReadBuffer
        {
            get => mBlockLastRead;
            set
            {
                lock (mBuffer)
                {
                    mBlockLastRead = value;
                    if (!value)
                        Monitor.PulseAll(mBuffer);
                }
            }
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length
        {
            get { lock (mBuffer) { return mBuffer.Count; } }
        }
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
            lock (mBuffer)
            {
                mFlushed = true;
                Monitor.PulseAll(mBuffer);
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset + count > buffer.Length)
                throw new ArgumentOutOfRangeException();

            if (count == 0) return;

            lock (mBuffer)
            {
                while (mBuffer.Count >= mMaxBufferLength)
                    Monitor.Wait(mBuffer);

                mFlushed = false;

                for (int i = 0; i < count; i++)
                    mBuffer.Enqueue(buffer[offset + i]);

                Monitor.PulseAll(mBuffer);
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset != 0) throw new NotSupportedException("Only offset = 0 is supported");
            if (offset < 0 || count < 0 || offset + count > buffer.Length)
                throw new ArgumentOutOfRangeException();

            if (count == 0) return 0;

            int read = 0;
            lock (mBuffer)
            {
                while (!ReadAvailable(count))
                    Monitor.Wait(mBuffer);

                while (read < count && mBuffer.Count > 0)
                    buffer[read++] = mBuffer.Dequeue();

                Monitor.PulseAll(mBuffer);
            }

            return read;
        }

        private bool ReadAvailable(int count)
            => (mBuffer.Count >= count || mFlushed)
               && (!mBlockLastRead || mBuffer.Count > count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = Read(buffer.Length <= 1024
                ? ArrayPool<byte>.Shared.Rent(buffer.Length)  
                : ArrayPool<byte>.Shared.Rent(buffer.Length), 0, buffer.Length);

            if (read > 0)
                buffer.Slice(0, read).CopyTo(buffer);

            return new ValueTask<int>(read);
        }

        

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
