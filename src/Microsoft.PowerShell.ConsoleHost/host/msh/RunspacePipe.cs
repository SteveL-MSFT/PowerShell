// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Microsoft.PowerShell
{
    /// <summary>
    /// Carries console input and independently framed stdout and stderr over a local pipe.
    /// </summary>
    internal static class RunspacePipe
    {
        private const int BufferSize = 8192;
        private const int ConnectTimeoutMilliseconds = 30000;
        private static readonly Encoding s_encoding = new UTF8Encoding(false);

        private enum PacketKind : byte
        {
            Input,
            EndInput,
            Output,
            Error,
            Exit,
        }

        internal static int RunServer(string name, Func<int> run)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(
                    name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.CurrentUserOnly);
                pipe.WaitForConnection();

                using var reader = new BinaryReader(pipe, s_encoding, leaveOpen: true);
                using var writer = new BinaryWriter(pipe, s_encoding, leaveOpen: true);
                using var input = new StreamReader(new PipeInputStream(reader), s_encoding);
                TextReader originalInput = Console.In;
                TextWriter originalOutput = Console.Out;
                TextWriter originalError = Console.Error;
                int exitCode;

                try
                {
                    using var output = new OutputCapture(pipe, writer, PacketKind.Output, 1);
                    using var error = new OutputCapture(pipe, writer, PacketKind.Error, 2);
                    Console.SetIn(input);
                    Console.SetOut(output.Writer);
                    Console.SetError(error.Writer);
                    exitCode = run();
                }
                finally
                {
                    Console.SetIn(originalInput);
                    Console.SetOut(originalOutput);
                    Console.SetError(originalError);
                }

                WritePacket(writer, PacketKind.Exit, BitConverter.GetBytes(exitCode));
                return exitCode;
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or Win32Exception or ObjectDisposedException)
            {
                Console.Error.WriteLine(e.Message);
                return ConsoleHost.ExitCodeInitFailure;
            }
        }

        internal static int Connect(string name)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(
                    ".", name, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
                pipe.Connect(ConnectTimeoutMilliseconds);

                using var reader = new BinaryReader(pipe, s_encoding, leaveOpen: true);
                using var writer = new BinaryWriter(pipe, s_encoding, leaveOpen: true);
                using Stream output = Console.OpenStandardOutput();
                using Stream error = Console.OpenStandardError();
                Exception inputError = null;
                int finished = 0;

                // Console input cannot be cancelled on every platform. A background thread
                // lets server exit/disconnection end the client even while stdin is open.
                var inputThread = new Thread(() =>
                {
                    try
                    {
                        using Stream input = Console.OpenStandardInput();
                        byte[] buffer = new byte[BufferSize];
                        int count;
                        while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            if (Volatile.Read(ref finished) != 0)
                            {
                                return;
                            }

                            WritePacket(writer, PacketKind.Input, buffer.AsSpan(0, count));
                        }

                        if (Volatile.Read(ref finished) == 0)
                        {
                            WritePacket(writer, PacketKind.EndInput, ReadOnlySpan<byte>.Empty);
                        }
                    }
                    catch (Exception e) when (e is IOException or ObjectDisposedException)
                    {
                        if (Volatile.Read(ref finished) == 0)
                        {
                            Interlocked.CompareExchange(ref inputError, e, null);
                            pipe.Dispose();
                        }
                    }
                })
                {
                    IsBackground = true,
                    Name = "Runspace pipe stdin",
                };
                inputThread.Start();

                try
                {
                    while (true)
                    {
                        PacketKind kind = ReadPacket(reader, out byte[] payload);
                        switch (kind)
                        {
                            case PacketKind.Output:
                                output.Write(payload);
                                output.Flush();
                                break;
                            case PacketKind.Error:
                                error.Write(payload);
                                error.Flush();
                                break;
                            case PacketKind.Exit when payload.Length == sizeof(int):
                                return BitConverter.ToInt32(payload);
                            default:
                                throw new InvalidDataException(ConsoleHostStrings.InvalidRunspacePipePacket);
                        }
                    }
                }
                catch (Exception e) when (e is IOException or InvalidDataException or ObjectDisposedException)
                {
                    Console.Error.WriteLine((inputError ?? e).Message);
                    return ConsoleHost.ExitCodeInitFailure;
                }
                finally
                {
                    Volatile.Write(ref finished, 1);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or TimeoutException)
            {
                Console.Error.WriteLine(e.Message);
                return ConsoleHost.ExitCodeInitFailure;
            }
        }

        private static PacketKind ReadPacket(BinaryReader reader, out byte[] payload)
        {
            PacketKind kind = (PacketKind)reader.ReadByte();
            int length = reader.ReadInt32();
            if (length < 0 || length > BufferSize)
            {
                throw new InvalidDataException(ConsoleHostStrings.InvalidRunspacePipePacket);
            }

            payload = reader.ReadBytes(length);
            if (payload.Length != length)
            {
                throw new EndOfStreamException(ConsoleHostStrings.RunspacePipeDisconnected);
            }

            return kind;
        }

        private static void WritePacket(BinaryWriter writer, PacketKind kind, ReadOnlySpan<byte> payload)
        {
            lock (writer)
            {
                writer.Write((byte)kind);
                writer.Write(payload.Length);
                writer.Write(payload);
                writer.Flush();
            }
        }

        private sealed class PipeInputStream : Stream
        {
            private readonly BinaryReader _reader;
            private byte[] _input = Array.Empty<byte>();
            private int _offset;
            private bool _atEnd;

            internal PipeInputStream(BinaryReader reader)
            {
                _reader = reader;
            }

            public override bool CanRead => true;

            public override bool CanWrite => false;

            public override bool CanSeek => false;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (count == 0)
                {
                    return 0;
                }

                while (!_atEnd && _offset == _input.Length)
                {
                    PacketKind kind = ReadPacket(_reader, out _input);
                    _offset = 0;
                    if (kind == PacketKind.EndInput && _input.Length == 0)
                    {
                        _atEnd = true;
                    }
                    else if (kind != PacketKind.Input)
                    {
                        throw new InvalidDataException(ConsoleHostStrings.InvalidRunspacePipePacket);
                    }
                }

                int available = Math.Min(count, _input.Length - _offset);
                _input.AsSpan(_offset, available).CopyTo(buffer.AsSpan(offset, available));
                _offset += available;
                return available;
            }

            public override void Write(byte[] buffer, int offset, int count)
                => throw new NotSupportedException();

            public override void Flush()
            { }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();
        }

        /// <summary>
        /// Capture OS handles as well as Console writers, including native child process output.
        /// </summary>
        private sealed class OutputCapture : IDisposable
        {
            private readonly AnonymousPipeServerStream _reader;
            private readonly AnonymousPipeClientStream _writer;
            private readonly SafeFileHandle _originalHandle;
            private readonly int _descriptor;
            private readonly Thread _pump;
            private Exception _exception;

            internal OutputCapture(PipeStream connection, BinaryWriter writer, PacketKind kind, int descriptor)
            {
                _descriptor = descriptor;
                _reader = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
                _writer = new AnonymousPipeClientStream(PipeDirection.Out, _reader.ClientSafePipeHandle);
                Writer = new StreamWriter(_writer, s_encoding, leaveOpen: true) { AutoFlush = true };
                try
                {
#if UNIX
                    int duplicate = NativeMethods.Dup(descriptor);
                    if (duplicate < 0)
                    {
                        throw new Win32Exception(Marshal.GetLastPInvokeError());
                    }

                    _originalHandle = new SafeFileHandle((IntPtr)duplicate, ownsHandle: true);
#else
                    _originalHandle = new SafeFileHandle(NativeMethods.GetStdHandle(-10 - descriptor), ownsHandle: false);
#endif
                    SetHandle(_writer.SafePipeHandle.DangerousGetHandle());
                }
                catch (Win32Exception)
                {
                    Writer.Dispose();
                    _writer.Dispose();
                    _reader.Dispose();
                    _originalHandle?.Dispose();
                    throw;
                }

                _pump = new Thread(() =>
                {
                    try
                    {
                        byte[] buffer = new byte[BufferSize];
                        int count;
                        while ((count = _reader.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            WritePacket(writer, kind, buffer.AsSpan(0, count));
                        }
                    }
                    catch (Exception e) when (e is IOException or ObjectDisposedException)
                    {
                        _exception = e;
                        _reader.Dispose();
                        connection.Dispose();
                    }
                })
                {
                    IsBackground = true,
                    Name = $"Runspace pipe {kind}",
                };
                _pump.Start();
            }

            internal StreamWriter Writer { get; }

            public void Dispose()
            {
                try
                {
                    Writer.Dispose();
                }
                finally
                {
                    SetHandle(_originalHandle.DangerousGetHandle());
                    _originalHandle.Dispose();
                    _writer.Dispose();
                    _pump.Join();
                    _reader.Dispose();
                }

                if (_exception != null)
                {
                    throw new IOException(ConsoleHostStrings.RunspacePipeDisconnected, _exception);
                }
            }

            private void SetHandle(IntPtr handle)
            {
#if UNIX
                bool failed = NativeMethods.Dup2(handle.ToInt32(), _descriptor) < 0;
#else
                bool failed = !NativeMethods.SetStdHandle(-10 - _descriptor, handle);
#endif
                if (failed)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }
            }
        }

        private static class NativeMethods
        {
#if UNIX
            [DllImport("libc", EntryPoint = "dup", SetLastError = true)]
            internal static extern int Dup(int descriptor);

            [DllImport("libc", EntryPoint = "dup2", SetLastError = true)]
            internal static extern int Dup2(int descriptor, int target);
#else
            [DllImport("kernel32.dll", SetLastError = true)]
            internal static extern IntPtr GetStdHandle(int handle);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool SetStdHandle(int standardHandle, IntPtr handle);
#endif
        }
    }
}
