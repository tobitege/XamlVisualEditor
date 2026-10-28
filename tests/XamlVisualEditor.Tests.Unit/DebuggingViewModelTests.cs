using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ReactiveUI;
using Xunit;
using XamlVisualEditor.Extensions.Debugging;
using XamlVisualEditor.Shell.ViewModels;

namespace XamlVisualEditor.Tests.Unit;

public sealed class DebuggingViewModelTests
{
    [Fact]
    public void Breakpoints_Toggle_Adds_And_Removes()
    {
        BreakpointsViewModel vm = new();
        vm.ToggleBreakpoint("/tmp/test.cs", 12, 1);

        Assert.Single(vm.Items);
        Assert.Equal(12, vm.Items[0].Line);

        vm.ToggleBreakpoint("/tmp/test.cs", 12, 1);
        Assert.Empty(vm.Items);
    }

    [Fact]
    public async Task Debugger_Refreshes_CallStack_And_Locals()
    {
        FakeDebugSession session = new();
        FakeDebuggerService service = new(session);
        DebuggerServiceRegistry registry = new();
        registry.Register(new DebuggerServiceRegistration("fake", "Fake Debugger", service), makeDefault: true);
        DebuggerViewModel vm = new(registry);

        await vm.StartAsync(new DebugLaunchOptions
        {
            AdapterPath = "fake",
            ProgramPath = "/tmp/app.dll"
        });

        await vm.RefreshAsync();

        Assert.Single(vm.CallStack.Frames);
        Assert.Equal("Main", vm.CallStack.Frames[0].Name);
        Assert.True(vm.Locals.Items.Count > 0);
    }

    [Fact]
    public async Task Debugger_Clears_Removed_Breakpoints()
    {
        TrackingDebugSession session = new();
        FakeDebuggerService service = new(session);
        DebuggerServiceRegistry registry = new();
        registry.Register(new DebuggerServiceRegistration("fake", "Fake Debugger", service), makeDefault: true);
        DebuggerViewModel vm = new(registry);

        await vm.StartAsync(new DebugLaunchOptions
        {
            AdapterPath = "fake",
            ProgramPath = "/tmp/app.dll"
        });

        vm.Breakpoints.ToggleBreakpoint("/tmp/test.cs", 12, 1);
        await session.WaitForBreakpointsAsync(expectedCount: 1);

        vm.Breakpoints.ToggleBreakpoint("/tmp/test.cs", 12, 1);
        await session.WaitForBreakpointsAsync(expectedCount: 0);
    }

    [Fact]
    public async Task Debugger_Applies_A_Breakpoint_The_Adapter_Verifies_Later()
    {
        FakeDebugSession session = new() { VerifiesBreakpoints = false };
        FakeDebuggerService service = new(session);
        DebuggerServiceRegistry registry = new();
        registry.Register(new DebuggerServiceRegistration("fake", "Fake Debugger", service), makeDefault: true);
        DebuggerViewModel vm = new(registry);
        await vm.StartAsync(new DebugLaunchOptions
        {
            AdapterPath = "fake",
            ProgramPath = "/tmp/app.dll"
        });

        vm.Breakpoints.ToggleBreakpoint("/tmp/test.cs", 12, 1);
        BreakpointEntryViewModel entry = Assert.Single(vm.Breakpoints.Items);
        await WaitUntilAsync(() => entry.Message == FakeDebugSession.PendingMessage);
        Assert.False(entry.IsVerified);

        session.Raise(new DebugBreakpointEvent(
            new BreakpointInfo { IsVerified = true, Line = 12, Id = FakeDebugSession.BreakpointId },
            BreakpointChangeType.Changed));

        await WaitUntilAsync(() => entry.IsVerified);
        Assert.Null(entry.Message);
    }

    [Fact]
    public void Breakpoints_Take_The_Answer_In_Request_Order_Also_For_A_Moved_Line()
    {
        BreakpointsViewModel vm = new();
        vm.ToggleBreakpoint("/tmp/test.cs", 4);
        vm.ToggleBreakpoint("/tmp/test.cs", 9);
        IReadOnlyList<BreakpointEntryViewModel> sent = vm.GetEnabledEntriesByFile()["/tmp/test.cs"];

        // The adapter moved the first breakpoint from a line without code to the next statement.
        vm.ApplyResults("/tmp/test.cs", sent, new[]
        {
            new BreakpointInfo { IsVerified = true, Line = 5, Id = 1 },
            new BreakpointInfo { IsVerified = false, Line = 9, Id = 2, Message = "pending" }
        });

        Assert.True(vm.Items[0].IsVerified);
        Assert.False(vm.Items[1].IsVerified);
        vm.ApplyChange(new BreakpointInfo { IsVerified = true, Line = 9, Id = 2 });
        Assert.True(vm.Items[1].IsVerified);
    }

    [Fact]
    public void Breakpoints_Keep_A_Verification_That_Arrives_Before_The_Answer()
    {
        BreakpointsViewModel vm = new();
        vm.ToggleBreakpoint("/tmp/test.cs", 9);
        IReadOnlyList<BreakpointEntryViewModel> sent = vm.GetEnabledEntriesByFile()["/tmp/test.cs"];

        vm.ApplyChange(new BreakpointInfo { IsVerified = true, Line = 9, Id = 3 });
        vm.ApplyResults("/tmp/test.cs", sent, new[] { new BreakpointInfo { IsVerified = false, Line = 9, Id = 3, Message = "pending" } });

        Assert.True(vm.Items[0].IsVerified);
        Assert.Null(vm.Items[0].Message);
    }

    [Fact]
    public void Breakpoints_Forget_The_Ids_Of_An_Ended_Session()
    {
        BreakpointsViewModel vm = new();
        vm.ToggleBreakpoint("/tmp/test.cs", 9);
        vm.ApplyResults("/tmp/test.cs", vm.GetEnabledEntriesByFile()["/tmp/test.cs"],
            new[] { new BreakpointInfo { IsVerified = true, Line = 9, Id = 1 } });

        vm.ResetAdapterState();
        // A new adapter process numbers its breakpoints from 1 again; this one belongs to another file.
        vm.ApplyChange(new BreakpointInfo { IsVerified = true, Line = 2, Id = 1 });

        Assert.False(vm.Items[0].IsVerified);
    }

    [Fact]
    public async Task Debugger_Launches_With_The_Enabled_Breakpoints()
    {
        FakeDebugSession session = new();
        FakeDebuggerService service = new(session);
        DebuggerServiceRegistry registry = new();
        registry.Register(new DebuggerServiceRegistration("fake", "Fake Debugger", service), makeDefault: true);
        DebuggerViewModel vm = new(registry);
        vm.Breakpoints.ToggleBreakpoint("/tmp/test.cs", 7);

        await vm.StartAsync(new DebugLaunchOptions
        {
            AdapterPath = "fake",
            ProgramPath = "/tmp/app.dll"
        });

        SourceBreakpoint breakpoint = Assert.Single(service.LaunchOptions!.Breakpoints!["/tmp/test.cs"]);
        Assert.Equal(7, breakpoint.Line);
    }

    [Fact]
    public async Task Debugger_Forgets_The_Breakpoint_Ids_When_The_Session_Ends()
    {
        FakeDebugSession session = new();
        FakeDebuggerService service = new(session);
        DebuggerServiceRegistry registry = new();
        registry.Register(new DebuggerServiceRegistration("fake", "Fake Debugger", service), makeDefault: true);
        DebuggerViewModel vm = new(registry);
        await vm.StartAsync(new DebugLaunchOptions
        {
            AdapterPath = "fake",
            ProgramPath = "/tmp/app.dll"
        });
        vm.Breakpoints.ToggleBreakpoint("/tmp/test.cs", 12, 1);
        BreakpointEntryViewModel entry = Assert.Single(vm.Breakpoints.Items);
        await WaitUntilAsync(() => entry.IsVerified);

        await vm.StopAsync();

        await WaitUntilAsync(() => !entry.IsVerified);
        vm.Breakpoints.ApplyChange(new BreakpointInfo { IsVerified = true, Line = 12, Id = FakeDebugSession.BreakpointId });
        Assert.False(entry.IsVerified);
    }

    [Fact]
    public async Task Debugger_Resets_A_File_Whose_Last_Breakpoint_Is_Disabled()
    {
        FakeDebugSession session = new();
        FakeDebuggerService service = new(session);
        DebuggerServiceRegistry registry = new();
        registry.Register(new DebuggerServiceRegistration("fake", "Fake Debugger", service), makeDefault: true);
        DebuggerViewModel vm = new(registry);
        await vm.StartAsync(new DebugLaunchOptions
        {
            AdapterPath = "fake",
            ProgramPath = "/tmp/app.dll"
        });
        vm.Breakpoints.ToggleBreakpoint("/tmp/test.cs", 12, 1);
        BreakpointEntryViewModel entry = Assert.Single(vm.Breakpoints.Items);
        await WaitUntilAsync(() => entry.IsVerified);

        entry.IsEnabled = false;

        await WaitUntilAsync(() => !entry.IsVerified);
        vm.Breakpoints.ApplyChange(new BreakpointInfo { IsVerified = true, Line = 12, Id = FakeDebugSession.BreakpointId });
        Assert.False(entry.IsVerified);
    }

    [Fact]
    public async Task Debugger_Takes_The_State_The_Session_Reached_During_The_Launch()
    {
        FakeDebugSession session = new();
        DebuggerServiceRegistry registry = new();
        registry.Register(new DebuggerServiceRegistration("fake", "Fake Debugger", new FakeDebuggerService(session)), makeDefault: true);
        DebuggerViewModel vm = new(registry);

        await vm.StartAsync(new DebugLaunchOptions
        {
            AdapterPath = "fake",
            ProgramPath = "/tmp/app.dll"
        });

        await WaitUntilAsync(() => vm.State == DebugSessionState.Running);
    }

    [Fact]
    public async Task Debugger_Sends_Breakpoints_One_Request_At_A_Time()
    {
        FakeDebugSession session = new();
        DebuggerServiceRegistry registry = new();
        registry.Register(new DebuggerServiceRegistration("fake", "Fake Debugger", new FakeDebuggerService(session)), makeDefault: true);
        DebuggerViewModel vm = new(registry);
        await vm.StartAsync(new DebugLaunchOptions
        {
            AdapterPath = "fake",
            ProgramPath = "/tmp/app.dll"
        });
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Gate = release.Task;

        vm.Breakpoints.ToggleBreakpoint("/tmp/first.cs", 3);
        await WaitUntilAsync(() => session.Requests == 1);
        vm.Breakpoints.ToggleBreakpoint("/tmp/second.cs", 5);
        // Gives an overlapping run the time to reach the adapter while the first request waits.
        await Task.Delay(200);
        release.SetResult();

        await WaitUntilAsync(() => vm.Breakpoints.Items.All(item => item.IsVerified));
        Assert.Equal(1, session.MostRequestsAtOnce);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class FakeDebuggerService : IDebuggerService
    {
        private readonly IDebugSession _session;

        public FakeDebuggerService(IDebugSession session)
        {
            _session = session;
        }

        public DebugLaunchOptions? LaunchOptions { get; private set; }

        public Task<IDebugSession> LaunchAsync(DebugLaunchOptions options, CancellationToken ct = default)
        {
            LaunchOptions = options;
            return Task.FromResult<IDebugSession>(_session);
        }

        public Task<IDebugSession> AttachAsync(DebugAttachOptions options, CancellationToken ct = default)
        {
            return Task.FromResult<IDebugSession>(_session);
        }
    }

    private sealed class FakeDebugSession : IDebugSession
    {
        public const int BreakpointId = 7;
        public const string PendingMessage = "The breakpoint is pending.";

        public DebugSessionState State => DebugSessionState.Running;

        // False answers each breakpoint as pending, as an adapter does before the module with its code loads.
        public bool VerifiesBreakpoints { get; init; } = true;

#pragma warning disable 0067
        public event Action<DebugSessionState>? StateChanged;
#pragma warning restore 0067
        public event Action<DebugEvent>? EventReceived;

        public void Raise(DebugEvent debugEvent) => EventReceived?.Invoke(debugEvent);

        // Holds each breakpoint request until it completes, as an adapter that takes its time.
        public Task? Gate { get; set; }

        public int Requests => _requests;

        public int MostRequestsAtOnce { get; private set; }

        private int _requests;
        private int _requestsInFlight;

        public async Task<IReadOnlyList<BreakpointInfo>> SetBreakpointsAsync(
            string filePath,
            IReadOnlyList<SourceBreakpoint> breakpoints,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref _requests);
            MostRequestsAtOnce = Math.Max(MostRequestsAtOnce, Interlocked.Increment(ref _requestsInFlight));
            try
            {
                if (Gate is not null)
                {
                    await Gate;
                }

                return breakpoints.Select(bp => new BreakpointInfo
                {
                    IsVerified = VerifiesBreakpoints,
                    Line = bp.Line,
                    Id = BreakpointId,
                    Message = VerifiesBreakpoints ? null : PendingMessage
                }).ToList();
            }
            finally
            {
                Interlocked.Decrement(ref _requestsInFlight);
            }
        }

        public Task<IReadOnlyList<ThreadInfo>> GetThreadsAsync(CancellationToken ct = default)
        {
            return Task.FromResult<IReadOnlyList<ThreadInfo>>(new List<ThreadInfo>
            {
                new() { Id = 1, Name = "Main" }
            });
        }

        public Task<StackTraceInfo> GetStackTraceAsync(int threadId, int startFrame, int levels, CancellationToken ct = default)
        {
            StackTraceInfo info = new()
            {
                Frames = new List<StackFrameInfo>
                {
                    new() { Id = 1, Name = "Main", FilePath = "/tmp/test.cs", Line = 10, Column = 1 }
                },
                TotalFrames = 1
            };
            return Task.FromResult(info);
        }

        public Task<IReadOnlyList<ScopeInfo>> GetScopesAsync(int frameId, CancellationToken ct = default)
        {
            return Task.FromResult<IReadOnlyList<ScopeInfo>>(new List<ScopeInfo>
            {
                new() { VariablesReference = 1, Name = "Locals", IsExpensive = false }
            });
        }

        public Task<IReadOnlyList<VariableInfo>> GetVariablesAsync(int variablesReference, CancellationToken ct = default)
        {
            return Task.FromResult<IReadOnlyList<VariableInfo>>(new List<VariableInfo>
            {
                new() { Name = "value", Value = "42", TypeName = "int", VariablesReference = 0 }
            });
        }

        public Task<EvaluateResult> EvaluateAsync(EvaluateRequest request, CancellationToken ct = default)
        {
            return Task.FromResult(new EvaluateResult { Result = "42", TypeName = "int", VariablesReference = 0 });
        }

        public Task ContinueAsync(int? threadId = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task StepInAsync(int? threadId = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task StepOutAsync(int? threadId = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task StepOverAsync(int? threadId = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task PauseAsync(int? threadId = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync(bool terminateDebuggee, CancellationToken ct = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TrackingDebugSession : IDebugSession
    {
        private readonly TaskCompletionSource<int> _firstNonEmpty = new();
        private readonly TaskCompletionSource<int> _firstEmpty = new();

        public DebugSessionState State => DebugSessionState.Running;

#pragma warning disable 0067
        public event Action<DebugSessionState>? StateChanged;
        public event Action<DebugEvent>? EventReceived;
#pragma warning restore 0067

        public Task WaitForBreakpointsAsync(int expectedCount)
        {
            return expectedCount == 0 ? _firstEmpty.Task : _firstNonEmpty.Task;
        }

        public Task<IReadOnlyList<BreakpointInfo>> SetBreakpointsAsync(
            string filePath,
            IReadOnlyList<SourceBreakpoint> breakpoints,
            CancellationToken ct = default)
        {
            if (breakpoints.Count == 0)
            {
                _firstEmpty.TrySetResult(0);
            }
            else
            {
                _firstNonEmpty.TrySetResult(breakpoints.Count);
            }

            return Task.FromResult<IReadOnlyList<BreakpointInfo>>(breakpoints.Select(bp => new BreakpointInfo
            {
                IsVerified = true,
                Line = bp.Line
            }).ToList());
        }

        public Task<IReadOnlyList<ThreadInfo>> GetThreadsAsync(CancellationToken ct = default)
        {
            return Task.FromResult<IReadOnlyList<ThreadInfo>>(new List<ThreadInfo>
            {
                new() { Id = 1, Name = "Main" }
            });
        }

        public Task<StackTraceInfo> GetStackTraceAsync(int threadId, int startFrame, int levels, CancellationToken ct = default)
        {
            StackTraceInfo info = new()
            {
                Frames = new List<StackFrameInfo>
                {
                    new() { Id = 1, Name = "Main", FilePath = "/tmp/test.cs", Line = 10, Column = 1 }
                },
                TotalFrames = 1
            };
            return Task.FromResult(info);
        }

        public Task<IReadOnlyList<ScopeInfo>> GetScopesAsync(int frameId, CancellationToken ct = default)
        {
            return Task.FromResult<IReadOnlyList<ScopeInfo>>(new List<ScopeInfo>
            {
                new() { VariablesReference = 1, Name = "Locals", IsExpensive = false }
            });
        }

        public Task<IReadOnlyList<VariableInfo>> GetVariablesAsync(int variablesReference, CancellationToken ct = default)
        {
            return Task.FromResult<IReadOnlyList<VariableInfo>>(new List<VariableInfo>
            {
                new() { Name = "value", Value = "42", TypeName = "int", VariablesReference = 0 }
            });
        }

        public Task<EvaluateResult> EvaluateAsync(EvaluateRequest request, CancellationToken ct = default)
        {
            return Task.FromResult(new EvaluateResult { Result = "42", TypeName = "int", VariablesReference = 0 });
        }

        public Task ContinueAsync(int? threadId = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task StepInAsync(int? threadId = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task StepOutAsync(int? threadId = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task StepOverAsync(int? threadId = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task PauseAsync(int? threadId = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync(bool terminateDebuggee, CancellationToken ct = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
