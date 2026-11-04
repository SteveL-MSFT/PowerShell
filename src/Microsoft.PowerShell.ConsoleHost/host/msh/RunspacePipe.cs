// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.ComponentModel;
using System.Diagnostics;
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
        private static ServerConnection s_server;

        internal static string PendingCommand => s_server.Command;

        internal static bool ClientDisconnected => s_server.Disconnected;

        internal static void Reconnect(int exitCode)
        {
            s_server.Complete(exitCode);
            s_server.Accept();
        }

        private enum PacketKind : byte
        {
            Input,
            EndInput,
            Output,
            Error,
            Exit,
            Command,
            Ready,
        }

        internal static int RunServer(string name, Func<int> run)
        {
            try
            {
                TextReader originalInput = Console.In;
                TextWriter originalOutput = Console.Out;
                TextWriter originalError = Console.Error;
                using var server = new ServerConnection(name, originalError);
                s_server = server;
                try
                {
                    server.Accept();
                    int exitCode = run();
                    server.Complete(exitCode);
                    return exitCode;
                }
                finally
                {
                    try
                    {
                        server.Dispose();
                    }
                    finally
                    {
                        s_server = null;
                        Console.SetIn(originalInput);
                        Console.SetOut(originalOutput);
                        Console.SetError(originalError);
                    }
                }
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or Win32Exception or ObjectDisposedException)
            {
                Console.Error.WriteLine(e.Message);
                return ConsoleHost.ExitCodeInitFailure;
            }
        }

        internal static int Connect(string name, string command)
        {
            try
            {
                using var pipe = ConnectPipe(name);

                using var reader = new BinaryReader(pipe, s_encoding, leaveOpen: true);
                using var writer = new BinaryWriter(pipe, s_encoding, leaveOpen: true);
                using Stream output = Console.OpenStandardOutput();
                using Stream error = Console.OpenStandardError();
                Exception inputError = null;
                int finished = 0;

                if (command != null)
                {
                    byte[] text = s_encoding.GetBytes(command);
                    for (int offset = 0; offset < text.Length; offset += BufferSize)
                    {
                        WritePacket(writer, PacketKind.Command, text.AsSpan(offset, Math.Min(BufferSize, text.Length - offset)));
                    }

                    if (text.Length == 0)
                    {
                        WritePacket(writer, PacketKind.Command, ReadOnlySpan<byte>.Empty);
                    }

                    WritePacket(writer, PacketKind.EndInput, ReadOnlySpan<byte>.Empty);
                }

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
                if (command == null)
                {
                    inputThread.Start();
                }

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
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or TimeoutException)
            {
                Console.Error.WriteLine(e.Message);
                return ConsoleHost.ExitCodeInitFailure;
            }
        }

        private static NamedPipeClientStream ConnectPipe(string name)
        {
            using var timeout = new CancellationTokenSource(ConnectTimeoutMilliseconds);
            var timer = Stopwatch.StartNew();
            while (true)
            {
                int remaining = ConnectTimeoutMilliseconds - (int)timer.ElapsedMilliseconds;
                if (remaining <= 0)
                {
                    throw new TimeoutException(ConsoleHostStrings.RunspacePipeConnectTimeout);
                }

                var pipe = new NamedPipeClientStream(
                    ".", name, PipeDirection.InOut, PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
                try
                {
                    pipe.Connect(remaining);
                    // On Unix a connection can be queued on the previous client's socket.
                    // Retry only before the server acknowledges readiness, never after sending commands.
                    byte[] ready = new byte[sizeof(int) + sizeof(byte)];
                    pipe.ReadExactlyAsync(ready, timeout.Token).AsTask().GetAwaiter().GetResult();
                    if (ready[0] != (byte)PacketKind.Ready || BitConverter.ToInt32(ready, 1) != 0)
                    {
                        throw new InvalidDataException(ConsoleHostStrings.InvalidRunspacePipePacket);
                    }

                    return pipe;
                }
                catch (IOException)
                {
                    pipe.Dispose();
                    Thread.Sleep(10);
                }
                catch (OperationCanceledException e)
                {
                    pipe.Dispose();
                    throw new TimeoutException(ConsoleHostStrings.RunspacePipeConnectTimeout, e);
                }
                catch (Exception e) when (e is InvalidDataException or UnauthorizedAccessException or TimeoutException)
                {
                    pipe.Dispose();
                    throw;
                }
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
            private readonly ServerConnection _connection;
            private MemoryStream _command;
            private byte[] _input = Array.Empty<byte>();
            private int _offset;
            private bool _atEnd;

            internal PipeInputStream(BinaryReader reader, ServerConnection connection)
            {
                _reader = reader;
                _connection = connection;
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
                    PacketKind kind;
                    try
                    {
                        kind = ReadPacket(_reader, out _input);
                    }
                    catch (Exception e) when (e is IOException or InvalidDataException or ObjectDisposedException)
                    {
                        _connection.Disconnect(e);
                        _input = Array.Empty<byte>();
                        _offset = 0;
                        _atEnd = true;
                        return 0;
                    }

                    _offset = 0;
                    if (kind == PacketKind.EndInput && _input.Length == 0)
                    {
                        if (_command != null)
                        {
                            _connection.Command = s_encoding.GetString(_command.ToArray());
                        }

                        _atEnd = true;
                    }
                    else if (kind == PacketKind.Command)
                    {
                        _command ??= new MemoryStream();
                        _command.Write(_input);
                        _input = Array.Empty<byte>();
                    }
                    else if (kind != PacketKind.Input)
                    {
                        _connection.Disconnect(new InvalidDataException(ConsoleHostStrings.InvalidRunspacePipePacket));
                        _input = Array.Empty<byte>();
                        _atEnd = true;
                        return 0;
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

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _command?.Dispose();
                }

                base.Dispose(disposing);
            }
        }

        private sealed class ServerConnection : IDisposable
        {
            private readonly string _name;
            private readonly TextWriter _diagnostics;
            private NamedPipeServerStream _pipe;
            private BinaryReader _reader;
            private BinaryWriter _writer;
            private StreamReader _input;
            private OutputCapture _output;
            private OutputCapture _error;
            private int _disconnected;
            private Exception _disconnectReason;

            internal ServerConnection(string name, TextWriter diagnostics)
            {
                _name = name;
                _diagnostics = diagnostics;
            }

            internal string Command { get; set; }

            internal bool Disconnected => Volatile.Read(ref _disconnected) != 0;

            internal void Accept()
            {
                Command = null;
                _disconnectReason = null;
                Volatile.Write(ref _disconnected, 0);
                _pipe = new NamedPipeServerStream(
                    _name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.CurrentUserOnly);
                _pipe.WaitForConnection();
                _reader = new BinaryReader(_pipe, s_encoding, leaveOpen: true);
                _writer = new BinaryWriter(_pipe, s_encoding, leaveOpen: true);
                _input = new StreamReader(new PipeInputStream(_reader, this), s_encoding);
                _output = new OutputCapture(this, PacketKind.Output, 1);
                _error = new OutputCapture(this, PacketKind.Error, 2);
                Console.SetIn(_input);
                Console.SetOut(_output.Writer);
                Console.SetError(_error.Writer);
                Send(PacketKind.Ready, ReadOnlySpan<byte>.Empty);
            }

            internal void Send(PacketKind kind, ReadOnlySpan<byte> payload)
            {
                if (Disconnected)
                {
                    return;
                }

                try
                {
                    WritePacket(_writer, kind, payload);
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException)
                {
                    Disconnect(e);
                }
            }

            internal void Disconnect(Exception exception)
            {
                if (Interlocked.Exchange(ref _disconnected, 1) == 0)
                {
                    _disconnectReason = exception;
                    _pipe.Dispose();
                }
            }

            internal void Complete(int exitCode)
            {
                try
                {
                    // Drain both OS streams before telling the client its command is complete.
                    _error?.Dispose();
                    _error = null;
                    _output?.Dispose();
                    _output = null;
                    Send(PacketKind.Exit, BitConverter.GetBytes(exitCode));
                }
                finally
                {
                    Dispose();
                    if (_disconnectReason != null)
                    {
                        _diagnostics.WriteLine(_disconnectReason.Message);
                    }
                }
            }

            public void Dispose()
            {
                _error?.Dispose();
                _error = null;
                _output?.Dispose();
                _output = null;
                _input?.Dispose();
                _input = null;
                _reader?.Dispose();
                _reader = null;
                if (!Disconnected)
                {
                    _writer?.Dispose();
                }

                // A disconnected BinaryWriter cannot be flushed; the pipe owns its handle.
                _writer = null;
                _pipe?.Dispose();
                _pipe = null;
            }
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
            private bool _disposed;

            internal OutputCapture(ServerConnection connection, PacketKind kind, int descriptor)
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
                            connection.Send(kind, buffer.AsSpan(0, count));
                        }
                    }
                    catch (Exception e) when (e is IOException or ObjectDisposedException)
                    {
                        _exception = e;
                        _reader.Dispose();
                        connection.Disconnect(e);
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
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
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
