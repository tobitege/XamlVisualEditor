using System;
using System.IO;
using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace XamlVisualEditor.Terminal;

public sealed class WindowsPtyProvider : IPtyProvider
{
    internal static int StartExternalProcess(TerminalSessionOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Command)) throw new ArgumentException("A shell executable is required.", nameof(options));
        WindowsNative.STARTUPINFOEX startup = new();
        startup.StartupInfo.cb = Marshal.SizeOf<WindowsNative.STARTUPINFO>();
        startup.StartupInfo.dwFlags = 1; // STARTF_USESHOWWINDOW
        startup.StartupInfo.wShowWindow = 1; // SW_SHOWNORMAL
        IntPtr environment = WindowsNative.CreateEnvironmentBlock(options.Environment);
        try
        {
            WindowsNative.PROCESS_INFORMATION process = WindowsNative.CreateProcess(
                BuildCommandLine(options.Command, options.Arguments), options.WorkingDirectory, environment, ref startup, newConsole: true);
            using SafeFileHandle processHandle = new(process.hProcess, ownsHandle: true);
            using SafeFileHandle threadHandle = new(process.hThread, ownsHandle: true);
            return process.dwProcessId;
        }
        finally { WindowsNative.DestroyEnvironmentBlock(environment); }
    }

    public IPtyProcess StartProcess(TerminalSessionOptions options)
    {
        string command = options.Command ?? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        string commandLine = BuildCommandLine(command, options.Arguments);

        WindowsNative.CreatePipe(out SafeFileHandle inputRead, out SafeFileHandle inputWrite);
        WindowsNative.CreatePipe(out SafeFileHandle outputRead, out SafeFileHandle outputWrite);

        IntPtr ptyHandle = WindowsNative.CreatePseudoConsole(
            (short)options.Columns,
            (short)options.Rows,
            inputRead,
            outputWrite);

        WindowsNative.STARTUPINFOEX startup = default;
        IntPtr envBlock = IntPtr.Zero;
        try
        {
            startup = WindowsNative.CreateStartupInfo(ptyHandle);
            envBlock = WindowsNative.CreateEnvironmentBlock(options.Environment);
            WindowsNative.PROCESS_INFORMATION process = WindowsNative.CreateProcess(commandLine, options.WorkingDirectory, envBlock, ref startup);
            SafeWaitHandle processHandle = new(process.hProcess, ownsHandle: true);
            using SafeFileHandle threadHandle = new(process.hThread, ownsHandle: true);

            inputRead.Dispose();
            outputWrite.Dispose();

            return new WindowsPtyProcess(
                process.dwProcessId,
                processHandle,
                ptyHandle,
                inputWrite,
                outputRead,
                startup);
        }
        catch
        {
            inputRead.Dispose();
            inputWrite.Dispose();
            outputRead.Dispose();
            outputWrite.Dispose();
            WindowsNative.ClosePseudoConsoleHandle(ptyHandle);
            WindowsNative.DisposeStartupInfo(ref startup);
            throw;
        }
        finally { WindowsNative.DestroyEnvironmentBlock(envBlock); }
    }

    private static string BuildCommandLine(string command, IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return QuoteCommand(command);
        }

        List<string> parts = new() { QuoteCommand(command) };
        foreach (string arg in args)
        {
            parts.Add(QuoteArgument(arg));
        }

        return string.Join(' ', parts);
    }

    private static string QuoteCommand(string command)
    {
        return QuoteArgument(command);
    }

    private static string QuoteArgument(string arg)
    {
        if (string.IsNullOrEmpty(arg))
        {
            return "\"\"";
        }

        bool needsQuotes = arg.IndexOfAny(new[] { ' ', '\t', '"' }) >= 0;
        if (!needsQuotes)
        {
            return arg;
        }

        string escaped = arg.Replace("\"", "\\\"");
        return "\"" + escaped + "\"";
    }
}

internal sealed class WindowsPtyProcess : IPtyProcess
{
    private readonly SafeFileHandle _inputWrite;
    private readonly SafeFileHandle _outputRead;
    private readonly FileStream _inputStream;
    private readonly FileStream _outputStream;
    private readonly IntPtr _pseudoConsole;
    private readonly ManualResetEvent _processExited;
    private readonly RegisteredWaitHandle _exitWait;
    private WindowsNative.STARTUPINFOEX _startupInfo;
    private int _pseudoConsoleClosed;
    private int _disposed;

    public Stream Input => _inputStream;
    public Stream Output => _outputStream;
    public int Pid { get; }

    internal WindowsPtyProcess(
        int pid,
        SafeWaitHandle processHandle,
        IntPtr pseudoConsole,
        SafeFileHandle inputWrite,
        SafeFileHandle outputRead,
        WindowsNative.STARTUPINFOEX startupInfo)
    {
        Pid = pid;
        _pseudoConsole = pseudoConsole;
        _inputWrite = inputWrite;
        _outputRead = outputRead;
        _inputStream = new FileStream(_inputWrite, FileAccess.Write, 4096, isAsync: false);
        _outputStream = new FileStream(_outputRead, FileAccess.Read, 4096, isAsync: false);
        _startupInfo = startupInfo;
        _processExited = new ManualResetEvent(false) { SafeWaitHandle = processHandle };
        // ConPTY keeps the output pipe open after the process ended; only ClosePseudoConsole lets the reader see EOF.
        _exitWait = ThreadPool.RegisterWaitForSingleObject(_processExited, OnProcessExited, null, Timeout.Infinite, executeOnlyOnce: true);
    }

    public void Resize(int columns, int rows, int pixelWidth = 0, int pixelHeight = 0)
    {
        WindowsNative.ResizePseudoConsole(_pseudoConsole, (short)columns, (short)rows);
    }

    private void OnProcessExited(object? state, bool timedOut)
    {
        ClosePseudoConsoleOnce();
    }

    private void ClosePseudoConsoleOnce()
    {
        if (Interlocked.Exchange(ref _pseudoConsoleClosed, 1) != 0) return;
        WindowsNative.ClosePseudoConsoleHandle(_pseudoConsole);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _exitWait.Unregister(null);
        // The reader stays active until ConPTY has written its last output.
        ClosePseudoConsoleOnce();
        _inputStream.Dispose();
        _outputStream.Dispose();
        _inputWrite.Dispose();
        _outputRead.Dispose();
        _processExited.Dispose();
        WindowsNative.DisposeStartupInfo(ref _startupInfo);
    }
}

internal static class WindowsNative
{
    private const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
    private const int STARTF_USESTDHANDLES = 0x00000100;
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_NEW_CONSOLE = 0x00000010;

    [StructLayout(LayoutKind.Sequential)]
    public struct COORD
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(COORD size, SafeFileHandle hInput, SafeFileHandle hOutput, uint dwFlags,
        out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, int nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr prevValue, IntPtr returnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFOEX lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    public static IntPtr CreatePseudoConsole(short cols, short rows, SafeFileHandle input, SafeFileHandle output)
    {
        COORD size = new() { X = cols, Y = rows };
        int result = CreatePseudoConsole(size, input, output, 0, out IntPtr handle);
        if (result != 0)
        {
            throw new InvalidOperationException("CreatePseudoConsole failed.");
        }
        return handle;
    }

    public static void ResizePseudoConsole(IntPtr handle, short cols, short rows)
    {
        COORD size = new() { X = cols, Y = rows };
        ResizePseudoConsole(handle, size);
    }

    public static void ClosePseudoConsoleHandle(IntPtr handle)
    {
        ClosePseudoConsole(handle);
    }

    public static void CreatePipe(out SafeFileHandle read, out SafeFileHandle write)
    {
        if (!CreatePipe(out read, out write, IntPtr.Zero, 0))
        {
            throw new InvalidOperationException("CreatePipe failed.");
        }
    }

    public static STARTUPINFOEX CreateStartupInfo(IntPtr pseudoConsole)
    {
        STARTUPINFOEX info = new();
        info.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
        // ConPTY gets its own standard handles instead of the redirected editor input and output.
        info.StartupInfo.dwFlags = STARTF_USESTDHANDLES;

        IntPtr size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        info.lpAttributeList = Marshal.AllocHGlobal(size);
        if (!InitializeProcThreadAttributeList(info.lpAttributeList, 1, 0, ref size))
        {
            int error = Marshal.GetLastWin32Error();
            Marshal.FreeHGlobal(info.lpAttributeList);
            throw new Win32Exception(error, "InitializeProcThreadAttributeList failed.");
        }

        if (!UpdateProcThreadAttribute(info.lpAttributeList, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
            pseudoConsole, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
        {
            int error = Marshal.GetLastWin32Error();
            DisposeStartupInfo(ref info);
            throw new Win32Exception(error, "UpdateProcThreadAttribute failed.");
        }

        return info;
    }

    public static PROCESS_INFORMATION CreateProcess(string commandLine, string? workingDirectory, IntPtr envBlock,
        ref STARTUPINFOEX startup, bool newConsole = false)
    {
        if (!CreateProcessW(
            null,
            commandLine,
            IntPtr.Zero,
            IntPtr.Zero,
            false,
            (newConsole ? CREATE_NEW_CONSOLE : EXTENDED_STARTUPINFO_PRESENT) | CREATE_UNICODE_ENVIRONMENT,
            envBlock,
            workingDirectory,
            ref startup,
            out PROCESS_INFORMATION process))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return process;
    }

    public static IntPtr CreateEnvironmentBlock(IReadOnlyDictionary<string, string> overrides)
    {
        Dictionary<string, string> merged = new(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            string? key = entry.Key as string;
            string? value = entry.Value as string;
            if (key is not null && value is not null)
            {
                merged[key] = value;
            }
        }

        foreach (KeyValuePair<string, string> kvp in overrides)
        {
            merged[kvp.Key] = kvp.Value;
        }

        if (!merged.ContainsKey("TERM"))
        {
            merged["TERM"] = "xterm-256color";
        }

        if (!merged.ContainsKey("COLORTERM"))
        {
            merged["COLORTERM"] = "truecolor";
        }

        string block = string.Join("\0", merged.Select(kvp => kvp.Key + "=" + kvp.Value)) + "\0\0";
        return Marshal.StringToHGlobalUni(block);
    }

    public static void DestroyEnvironmentBlock(IntPtr block)
    {
        if (block != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(block);
        }
    }

    public static void DisposeStartupInfo(ref STARTUPINFOEX startup)
    {
        if (startup.lpAttributeList != IntPtr.Zero)
        {
            DeleteProcThreadAttributeList(startup.lpAttributeList);
            Marshal.FreeHGlobal(startup.lpAttributeList);
            startup.lpAttributeList = IntPtr.Zero;
        }
    }
}
