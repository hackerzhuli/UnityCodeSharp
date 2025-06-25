using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Mono.Debugging.Soft;
using MonoClient = Mono.Debugging.Client;
using DebugProtocol = Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;

namespace MonoDebugger;

/// <summary>
///     Debug session that handles VS Code debug protocol communication and Unity debugging.
/// </summary>
public class DebugSession : DebugAdapterBase
{
    private readonly Dictionary<int, MonoClient.StackFrame> _frameHandles = new();
    private readonly Dictionary<int, MonoClient.SourceLocation> _gotoHandles = new();
    private readonly SoftDebuggerSession _session = new();
    private readonly Dictionary<int, Func<MonoClient.ObjectValue[]>> _variableHandles = new();
    private Launch _launch = null!;
    private int _nextHandle = 1000;

    /// <summary>
    ///     Initializes a new instance of the DebugSession class.
    /// </summary>
    /// <param name="input">Input stream for protocol communication</param>
    /// <param name="output">Output stream for protocol communication</param>
    /// <param name="options"></param>
    public DebugSession(Stream input, Stream output, DebugOptions options)
    {
        Options = options;
        InitializeProtocolClient(input, output);

        _session.LogWriter = OnSessionLog;
        _session.DebugWriter = OnDebugLog;
        _session.OutputWriter = OnLog;
        _session.ExceptionHandler = OnExceptionHandled;

        _session.TargetStopped += TargetStopped;
        _session.TargetHitBreakpoint += TargetHitBreakpoint;
        _session.TargetExceptionThrown += TargetExceptionThrown;
        _session.TargetUnhandledException += TargetExceptionThrown;
        _session.TargetReady += TargetReady;
        _session.TargetExited += TargetExited;
        _session.TargetThreadStarted += TargetThreadStarted;
        _session.TargetThreadStopped += TargetThreadStopped;
        _session.AssemblyLoaded += AssemblyLoaded;

        _session.Breakpoints.BreakpointStatusChanged += BreakpointStatusChanged;
    }

    public DebugOptions Options { get; }

    private int CreateHandle<T>(Dictionary<int, T> dictionary, T value)
    {
        var handle = _nextHandle++;
        dictionary[handle] = value;
        return handle;
    }

    private T? GetHandle<T>(Dictionary<int, T> dictionary, int handle, T? defaultValue = default) where T : class
    {
        return dictionary.TryGetValue(handle, out var value) ? value : defaultValue;
    }

    private bool TryGetHandle<T>(Dictionary<int, T> dictionary, int handle, out T? value)
    {
        return dictionary.TryGetValue(handle, out value);
    }

    /// <summary>
    ///     Starts the debug session and begins protocol communication.
    /// </summary>
    public void Start()
    {
        Protocol.LogMessage += LogMessage;
        Protocol.DispatcherError += LogError;
        Protocol.Run();
    }

    /// <summary>
    ///     Handles output data received from the debugged process.
    /// </summary>
    /// <param name="stdout">Standard output message</param>
    public void OnOutputDataReceived(string stdout)
    {
        SendMessageEvent(DebugProtocol.OutputEvent.CategoryValue.Stdout, stdout);
    }

    /// <summary>
    ///     Handles error data received from the debugged process.
    /// </summary>
    /// <param name="stderr">Standard error message</param>
    public void OnErrorDataReceived(string stderr)
    {
        SendMessageEvent(DebugProtocol.OutputEvent.CategoryValue.Stderr, stderr);
    }

    /// <summary>
    ///     Handles debug data received from the debugged process.
    /// </summary>
    /// <param name="debug">Debug message</param>
    public void OnDebugDataReceived(string debug)
    {
        SendMessageEvent(DebugProtocol.OutputEvent.CategoryValue.Console, debug);
    }

    /// <summary>
    ///     Handles important data received from the debugged process.
    /// </summary>
    /// <param name="message">Important message</param>
    public void OnImportantDataReceived(string message)
    {
        SendMessageEvent(DebugProtocol.OutputEvent.CategoryValue.Important, message);
    }

    /// <summary>
    ///     Handles unhandled exceptions in the debug session.
    /// </summary>
    /// <param name="ex">The unhandled exception</param>
    protected void OnUnhandledException(Exception ex)
    {
        _launch?.Dispose();
    }

    private void SendMessageEvent(DebugProtocol.OutputEvent.CategoryValue category, string message)
    {
        Protocol.SendEvent(new DebugProtocol.OutputEvent(message.Trim() + Environment.NewLine)
        {
            Category = category
        });
    }

    private void LogMessage(object? sender, LogEventArgs args)
    {
        Debug.Log(args.Message);
    }

    private void LogError(object? sender, DispatcherErrorEventArgs args)
    {
        Debug.LogError($"[Fatal] {args.Exception.Message}", args.Exception);
        OnUnhandledException(args.Exception);
    }

    protected override DebugProtocol.InitializeResponse HandleInitializeRequest(
        DebugProtocol.InitializeArguments arguments)
    {
        return new DebugProtocol.InitializeResponse
        {
            // SupportsTerminateRequest = true, // Only for 'launch' requests
            SupportsEvaluateForHovers = true,
            SupportsExceptionInfoRequest = true,
            SupportsConditionalBreakpoints = true,
            SupportsHitConditionalBreakpoints = true,
            SupportsFunctionBreakpoints = true,
            SupportsLogPoints = true,
            SupportsExceptionOptions = true,
            SupportsExceptionFilterOptions = true,
            SupportsCompletionsRequest = true,
            SupportsSetVariable = true,
            SupportsGotoTargetsRequest = true,
            CompletionTriggerCharacters = ["."],
            ExceptionBreakpointFilters = [ExceptionsFilter.AllExceptions]
        };
    }

    protected override DebugProtocol.AttachResponse HandleAttachRequest(DebugProtocol.AttachArguments arguments)
    {
        return ServerExtensions.DoSafe(() =>
        {
            var configuration = new LaunchConfig(arguments.ConfigurationProperties);
            SymbolServerExtensions.SetEventLogger(OnDebugDataReceived);

            _launch = configuration.GetLaunchAgent();
            _launch.Prepare(this);
            _launch.Connect(_session);
            return new DebugProtocol.AttachResponse();
        });
    }

    protected override DebugProtocol.DisconnectResponse HandleDisconnectRequest(
        DebugProtocol.DisconnectArguments arguments)
    {
        _session.Detach();
        _session.Dispose();
        _launch?.Dispose();
        return new DebugProtocol.DisconnectResponse();
    }

    protected override DebugProtocol.ContinueResponse HandleContinueRequest(DebugProtocol.ContinueArguments arguments)
    {
        return ServerExtensions.DoSafe(() =>
        {
            if (!_session.IsRunning && !_session.HasExited)
                _session.Continue();

            return new DebugProtocol.ContinueResponse();
        });
    }

    protected override DebugProtocol.NextResponse HandleNextRequest(DebugProtocol.NextArguments arguments)
    {
        return ServerExtensions.DoSafe(() =>
        {
            if (!_session.IsRunning && !_session.HasExited)
                _session.NextLine();

            return new DebugProtocol.NextResponse();
        });
    }

    protected override DebugProtocol.StepInResponse HandleStepInRequest(DebugProtocol.StepInArguments arguments)
    {
        return ServerExtensions.DoSafe(() =>
        {
            if (!_session.IsRunning && !_session.HasExited)
                _session.StepLine();

            return new DebugProtocol.StepInResponse();
        });
    }

    protected override DebugProtocol.StepOutResponse HandleStepOutRequest(DebugProtocol.StepOutArguments arguments)
    {
        return ServerExtensions.DoSafe(() =>
        {
            if (!_session.IsRunning && !_session.HasExited)
                _session.Finish();

            return new DebugProtocol.StepOutResponse();
        });
    }

    protected override DebugProtocol.PauseResponse HandlePauseRequest(DebugProtocol.PauseArguments arguments)
    {
        return ServerExtensions.DoSafe(() =>
        {
            if (_session.IsRunning)
                _session.Stop();

            return new DebugProtocol.PauseResponse();
        });
    }

    protected override DebugProtocol.SetExceptionBreakpointsResponse HandleSetExceptionBreakpointsRequest(
        DebugProtocol.SetExceptionBreakpointsArguments arguments)
    {
        _session.Breakpoints.ClearCatchpoints();
        if (arguments.FilterOptions == null || arguments.FilterOptions.Count == 0)
            return new DebugProtocol.SetExceptionBreakpointsResponse();

        foreach (var option in arguments.FilterOptions)
        {
            if (option.FilterId != ExceptionsFilter.AllExceptions.Filter)
                continue;

            var allExceptionFilter = typeof(Exception).ToString();
            if (string.IsNullOrEmpty(option.Condition))
            {
                _session.Breakpoints.AddCatchpoint(allExceptionFilter);
                continue;
            }

            if (option.Condition.StartsWith('!'))
            {
                var catchpoint = _session.Breakpoints.AddCatchpoint(allExceptionFilter);
                foreach (var condition in option.Condition.Substring(1)
                             .Split(',', StringSplitOptions.RemoveEmptyEntries))
                    catchpoint.AddIgnore(condition.Trim());
            }
            else
            {
                foreach (var condition in option.Condition.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    _session.Breakpoints.AddCatchpoint(condition.Trim());
            }
        }

        return new DebugProtocol.SetExceptionBreakpointsResponse();
    }

    protected override DebugProtocol.SetBreakpointsResponse HandleSetBreakpointsRequest(
        DebugProtocol.SetBreakpointsArguments arguments)
    {
        var breakpoints = new List<DebugProtocol.Breakpoint>();
        var breakpointsInfos = arguments.Breakpoints;
        var sourcePath = arguments.Source.Path;

        if (string.IsNullOrEmpty(sourcePath))
            throw new ProtocolException("No source available for the breakpoint");

        // Remove all file breakpoints
        var fileBreakpoints = _session.Breakpoints.GetBreakpointsAtFile(sourcePath);
        foreach (var fileBreakpoint in fileBreakpoints)
            _session.Breakpoints.Remove(fileBreakpoint);

        // Add all new breakpoints
        foreach (var breakpointInfo in breakpointsInfos)
        {
            var breakpoint = _session.Breakpoints.Add(sourcePath, breakpointInfo.Line, breakpointInfo.Column ?? 1);
            // Conditional breakpoint
            if (!string.IsNullOrEmpty(breakpointInfo.Condition))
                breakpoint.ConditionExpression = breakpointInfo.Condition;
            // Hit count breakpoint
            if (!string.IsNullOrEmpty(breakpointInfo.HitCondition))
            {
                breakpoint.HitCountMode = MonoClient.HitCountMode.EqualTo;
                breakpoint.HitCount = int.TryParse(breakpointInfo.HitCondition, out var hitCount) ? hitCount : 1;
            }

            // Logpoint
            if (!string.IsNullOrEmpty(breakpointInfo.LogMessage))
            {
                breakpoint.HitAction = MonoClient.HitAction.PrintExpression;
                breakpoint.TraceExpression = $"[LogPoint]: {breakpointInfo.LogMessage}";
            }

            breakpoints.Add(breakpoint.ToBreakpoint(_session));
        }

        return new DebugProtocol.SetBreakpointsResponse(breakpoints);
    }

    protected override DebugProtocol.SetFunctionBreakpointsResponse HandleSetFunctionBreakpointsRequest(
        DebugProtocol.SetFunctionBreakpointsArguments arguments)
    {
        // clear existing function breakpoints
        var functionBreakpoints = _session.Breakpoints.OfType<MonoClient.FunctionBreakpoint>();
        foreach (var functionBreakpoint in functionBreakpoints)
            _session.Breakpoints.Remove(functionBreakpoint);

        foreach (var breakpointInfo in arguments.Breakpoints)
        {
            var languageName = "C#";
            var functionName = breakpointInfo.Name;
            var functionParts = breakpointInfo.Name.Split("!");
            if (functionParts.Length == 2)
            {
                languageName = functionParts[0];
                functionName = functionParts[1];
            }

            var functionBreakpoint = new MonoClient.FunctionBreakpoint(functionName, languageName);
            // Conditional breakpoint
            if (!string.IsNullOrEmpty(breakpointInfo.Condition))
                functionBreakpoint.ConditionExpression = breakpointInfo.Condition;
            // Hit count breakpoint
            if (!string.IsNullOrEmpty(breakpointInfo.HitCondition))
            {
                functionBreakpoint.HitCountMode = MonoClient.HitCountMode.EqualTo;
                functionBreakpoint.HitCount =
                    int.TryParse(breakpointInfo.HitCondition, out var hitCount) ? hitCount : 1;
            }

            _session.Breakpoints.Add(functionBreakpoint);
        }

        return new DebugProtocol.SetFunctionBreakpointsResponse();
    }

    protected override DebugProtocol.StackTraceResponse HandleStackTraceRequest(
        DebugProtocol.StackTraceArguments arguments)
    {
        return ServerExtensions.DoSafe(() =>
        {
            // Avoid using 'session.ActiveThread' because its backtrace may be not actual
            var thread = _session.FindThread(arguments.ThreadId);
            var stackFrames = new List<DebugProtocol.StackFrame>();
            var bt = thread?.Backtrace;

            if (bt == null || bt.FrameCount < 0)
                throw new ProtocolException("No stack trace available");

            var totalFrames = bt.FrameCount;
            var startFrame = arguments.StartFrame ?? 0;
            var levels = arguments.Levels ?? totalFrames;
            for (var i = startFrame; i < Math.Min(startFrame + levels, totalFrames); i++)
            {
                var frame = bt.GetFrameSafe(i);
                if (frame == null)
                {
                    stackFrames.Add(new DebugProtocol.StackFrame(0, "<unknown>", 0, 0));
                    continue;
                }

                if (frame.Language == "Transition" && Options.SkipNativeTransitions)
                    continue;

                DebugProtocol.Source? source = null;
                var frameId = CreateHandle(_frameHandles, frame);
                var remappedSourcePath = this.RemapSourceLocation(frame.SourceLocation);
                if (!string.IsNullOrEmpty(remappedSourcePath) && File.Exists(remappedSourcePath))
                    source = new DebugProtocol.Source
                    {
                        Name = Path.GetFileName(remappedSourcePath),
                        Path = Path.GetFullPath(remappedSourcePath)
                    };
                if (source == null)
                    source = new DebugProtocol.Source
                    {
                        Path = frame.SourceLocation.FileName,
                        SourceReference = frame.GetSourceReference(),
                        AlternateSourceReference = frameId,
                        VsSourceLinkInfo = frame.SourceLocation.SourceLink.ToSourceLinkInfo(),
                        Name = string.IsNullOrEmpty(frame.SourceLocation.FileName)
                            ? Path.GetFileName(frame.FullModuleName)
                            : Path.GetFileName(frame.SourceLocation.FileName)
                    };

                stackFrames.Add(new DebugProtocol.StackFrame
                {
                    Id = frameId,
                    Source = source,
                    Name = frame.GetFullStackFrameText(),
                    Line = frame.SourceLocation.Line,
                    Column = frame.SourceLocation.Column,
                    EndLine = frame.SourceLocation.EndLine,
                    EndColumn = frame.SourceLocation.EndColumn,
                    PresentationHint = DebugProtocol.StackFrame.PresentationHintValue.Normal
                    // VSCode does not focus the exceptions in the 'Subtle' presentation hint
                    // PresentationHint = frame.IsExternalCode
                    //     ? StackFrame.PresentationHintValue.Subtle
                    //     : StackFrame.PresentationHintValue.Normal
                });
            }

            return new DebugProtocol.StackTraceResponse(stackFrames);
        });
    }

    protected override DebugProtocol.ScopesResponse HandleScopesRequest(DebugProtocol.ScopesArguments arguments)
    {
        return ServerExtensions.DoSafe(() =>
        {
            var frameId = arguments.FrameId;
            var frame = GetHandle(_frameHandles, frameId);
            var scopes = new List<DebugProtocol.Scope>();

            if (frame == null)
                throw new ProtocolException("frame not found");

            scopes.Add(new DebugProtocol.Scope
            {
                Name = "Locals",
                PresentationHint = DebugProtocol.Scope.PresentationHintValue.Locals,
                VariablesReference = CreateHandle(_variableHandles, frame.GetAllLocals)
            });

            return new DebugProtocol.ScopesResponse(scopes);
        });
    }

    protected override DebugProtocol.VariablesResponse HandleVariablesRequest(
        DebugProtocol.VariablesArguments arguments)
    {
        return ServerExtensions.DoSafe(() =>
        {
            var reference = arguments.VariablesReference;
            var variables = new List<DebugProtocol.Variable>();

            if (TryGetHandle(_variableHandles, reference, out var getChildrenDelegate))
            {
                var children = getChildrenDelegate?.Invoke();
                if (children != null && children.Length > 0)
                    foreach (var v in children)
                    {
                        // Not matter how many variables, callbacks start automatically after 'GetChildren' is called
                        v.WaitHandle.WaitOne(_session.EvaluationOptions.EvaluationTimeout);
                        variables.Add(CreateVariable(v));
                    }
            }

            return new DebugProtocol.VariablesResponse(variables);
        });
    }

    protected override DebugProtocol.ThreadsResponse HandleThreadsRequest(DebugProtocol.ThreadsArguments arguments)
    {
        return ServerExtensions.DoSafe(() =>
        {
            var threads = new List<DebugProtocol.Thread>();
            var process = _session.GetProcesses().FirstOrDefault();
            if (process == null)
                return new DebugProtocol.ThreadsResponse();

            foreach (var thread in process.GetThreads())
            {
                var tid = (int)thread.Id;
                threads.Add(new DebugProtocol.Thread(tid, thread.Name.ToThreadName(tid)));
            }

            return new DebugProtocol.ThreadsResponse(threads);
        });
    }

    protected override DebugProtocol.EvaluateResponse HandleEvaluateRequest(DebugProtocol.EvaluateArguments arguments)
    {
        return ServerExtensions.DoSafe(() =>
        {
            var expression = arguments.TrimExpression();
            var frame = GetHandle(_frameHandles, arguments.FrameId ?? 0);
            if (frame == null)
                throw new ProtocolException("no active stackframe");

            var options = _session.Options.EvaluationOptions.Clone();
            if (arguments.Context == DebugProtocol.EvaluateArguments.ContextValue.Hover)
                options.UseExternalTypeResolver = false;

            var value = frame.GetExpressionValue(expression, options);
            value.WaitHandle.WaitOne(options.EvaluationTimeout);

            if (value.IsEvaluating)
                throw new ProtocolException("evaluation timeout expected");
            if (value.Flags.HasFlag(MonoClient.ObjectValueFlags.Error) ||
                value.Flags.HasFlag(MonoClient.ObjectValueFlags.NotSupported))
                throw new ProtocolException(value.DisplayValue);
            if (value.Flags.HasFlag(MonoClient.ObjectValueFlags.Unknown))
                throw new ProtocolException("invalid expression");
            if (value.Flags.HasFlag(MonoClient.ObjectValueFlags.Object) &&
                value.Flags.HasFlag(MonoClient.ObjectValueFlags.Namespace))
                throw new ProtocolException("not available");

            var handle = 0;
            if (value.HasChildren)
                handle = CreateHandle(_variableHandles, value.GetAllChildren);

            return new DebugProtocol.EvaluateResponse(value.ToDisplayValue(), handle);
        });
    }

    protected override DebugProtocol.SourceResponse HandleSourceRequest(DebugProtocol.SourceArguments arguments)
    {
        return ServerExtensions.DoSafe(() =>
        {
            var sourceLinkUri = arguments.Source.VsSourceLinkInfo?.Url;
            if (!string.IsNullOrEmpty(sourceLinkUri) && _session.Options.AutomaticSourceLinkDownload ==
                MonoClient.AutomaticSourceDownload.Always)
            {
                var content = SymbolServerExtensions.DownloadSourceFile(sourceLinkUri);
                if (!string.IsNullOrEmpty(content))
                    return new DebugProtocol.SourceResponse(content);
            }

            var frame = GetHandle(_frameHandles, arguments.Source.AlternateSourceReference ?? -1);
            if (frame == null)
                throw ServerExtensions.GetProtocolException("No source available");

            return new DebugProtocol.SourceResponse(frame.GetAssemblyCode());
        });
    }

    protected override DebugProtocol.ExceptionInfoResponse HandleExceptionInfoRequest(
        DebugProtocol.ExceptionInfoArguments arguments)
    {
        return ServerExtensions.DoSafe(() =>
        {
            var ex = _session.FindException(arguments.ThreadId);
            if (ex == null)
                throw new ProtocolException("No exception available");

            return ex.ToExceptionInfoResponse();
        });
    }

    protected override DebugProtocol.CompletionsResponse HandleCompletionsRequest(
        DebugProtocol.CompletionsArguments arguments)
    {
        return ServerExtensions.DoSafe(() =>
        {
            var frame = GetHandle(_frameHandles, arguments.FrameId ?? 0);
            if (frame == null)
                throw new ProtocolException("no active stackframe");

            string? resolvedText = null;
            if (_session.Options.EvaluationOptions.UseExternalTypeResolver)
            {
                var lastTriggerIndex = arguments.Text.LastIndexOf('.');
                if (lastTriggerIndex > 0)
                {
                    resolvedText = frame.ResolveExpression(arguments.Text.Substring(0, lastTriggerIndex));
                    resolvedText += arguments.Text.Substring(lastTriggerIndex);
                }
            }

            var completionData = frame.GetExpressionCompletionData(resolvedText ?? arguments.Text);
            if (completionData == null || completionData.Items == null)
                return new DebugProtocol.CompletionsResponse();

            return new DebugProtocol.CompletionsResponse(
                completionData.Items.Select(x => x.ToCompletionItem()).ToList());
        });
    }

    protected override DebugProtocol.SetVariableResponse HandleSetVariableRequest(
        DebugProtocol.SetVariableArguments arguments)
    {
        var variablesDelegate = GetHandle(_variableHandles, arguments.VariablesReference);
        if (variablesDelegate == null)
            throw new ProtocolException("VariablesReference not found");

        var variables = variablesDelegate.Invoke();
        var variable = variables.FirstOrDefault(v => v.Name == arguments.Name);
        if (variable == null)
            throw new ProtocolException("variable not found");
        // No way to use ExternalTypeResolver for setting variables. Use hardcoded value
        variable.SetValue(variable.ResolveValue(arguments.Value), _session.Options.EvaluationOptions);
        variable.Refresh();
        return CreateVariable(variable).ToSetVariableResponse();
    }

    protected override DebugProtocol.GotoTargetsResponse HandleGotoTargetsRequest(
        DebugProtocol.GotoTargetsArguments arguments)
    {
        var targetId = CreateHandle(_gotoHandles,
            new MonoClient.SourceLocation(string.Empty, arguments.Source.Path, arguments.Line, arguments.Column ?? 1, 0,
                0));
        return arguments.ToJumpToCursorTarget(targetId);
    }

    protected override DebugProtocol.GotoResponse HandleGotoRequest(DebugProtocol.GotoArguments arguments)
    {
        return ServerExtensions.DoSafe(() =>
        {
            var target = GetHandle(_gotoHandles, arguments.TargetId);
            if (target == null)
                throw new ProtocolException("GotoTarget not found");

            _session.SetNextStatement(target.FileName, target.Line, target.Column);
            return new DebugProtocol.GotoResponse();
        });
    }


    private void TargetStopped(object? sender, MonoClient.TargetEventArgs e)
    {
        ResetHandles();
        Protocol.SendEvent(new DebugProtocol.StoppedEvent(DebugProtocol.StoppedEvent.ReasonValue.Pause)
        {
            ThreadId = (int)e.Thread.Id,
            AllThreadsStopped = true
        });
    }

    private void TargetHitBreakpoint(object? sender, MonoClient.TargetEventArgs e)
    {
        ResetHandles();
        Protocol.SendEvent(new DebugProtocol.StoppedEvent(DebugProtocol.StoppedEvent.ReasonValue.Breakpoint)
        {
            ThreadId = (int)e.Thread.Id,
            AllThreadsStopped = true
        });
    }

    private void TargetExceptionThrown(object? sender, MonoClient.TargetEventArgs e)
    {
        ResetHandles();
        var ex = _session.FindException(e.Thread.Id);
        Protocol.SendEvent(new DebugProtocol.StoppedEvent(DebugProtocol.StoppedEvent.ReasonValue.Exception)
        {
            Description = "Paused on exception",
            Text = ex?.Type ?? "Exception",
            ThreadId = (int)e.Thread.Id,
            AllThreadsStopped = true
        });
    }

    private void TargetReady(object? sender, MonoClient.TargetEventArgs e)
    {
        Protocol.SendEvent(new DebugProtocol.InitializedEvent());
    }

    private void TargetExited(object? sender, MonoClient.TargetEventArgs e)
    {
        Protocol.SendEvent(new DebugProtocol.TerminatedEvent());
    }

    private void TargetThreadStarted(object? sender, MonoClient.TargetEventArgs e)
    {
        var tid = (int)e.Thread.Id;
        Protocol.SendEvent(new DebugProtocol.ThreadEvent(DebugProtocol.ThreadEvent.ReasonValue.Started, tid));
    }

    private void TargetThreadStopped(object? sender, MonoClient.TargetEventArgs e)
    {
        var tid = (int)e.Thread.Id;
        Protocol.SendEvent(new DebugProtocol.ThreadEvent(DebugProtocol.ThreadEvent.ReasonValue.Exited, tid));
    }

    private void AssemblyLoaded(object? sender, MonoClient.AssemblyEventArgs e)
    {
        Protocol.SendEvent(new DebugProtocol.ModuleEvent(DebugProtocol.ModuleEvent.ReasonValue.New,
            e.Assembly.ToModule()));
    }

    private void BreakpointStatusChanged(object? sender, MonoClient.BreakpointEventArgs e)
    {
        Protocol.SendEvent(new DebugProtocol.BreakpointEvent(DebugProtocol.BreakpointEvent.ReasonValue.Changed,
            e.Breakpoint.ToBreakpoint(_session)));
    }

    private bool OnExceptionHandled(Exception ex)
    {
        MonoClient.DebuggerLoggingService.CustomLogger?.LogError($"[Handled] {ex.Message}", ex);
        return true;
    }

    private void OnSessionLog(bool isError, string message)
    {
        if (isError) MonoClient.DebuggerLoggingService.CustomLogger?.LogError($"[Error] {message.Trim()}", null);
        else MonoClient.DebuggerLoggingService.CustomLogger?.LogMessage($"[Info] {message.Trim()}");

        OnOutputDataReceived($"[Mono] {message.Trim()}");
    }

    private void OnLog(bool isError, string message)
    {
        if (isError) OnErrorDataReceived(message);
        else OnOutputDataReceived(message);
    }

    private void OnDebugLog(int level, string category, string message)
    {
        OnDebugDataReceived(message);
    }

    private void ResetHandles()
    {
        _frameHandles.Clear();
        _gotoHandles.Clear();
        _variableHandles.Clear();
        _nextHandle = 1000;
    }

    private DebugProtocol.Variable CreateVariable(MonoClient.ObjectValue v)
    {
        var childrenReference = 0;
        if (v.HasChildren && !v.HasNullValue()) childrenReference = CreateHandle(_variableHandles, v.GetAllChildren);
        return new DebugProtocol.Variable
        {
            Name = v.Name,
            Type = v.TypeName,
            Value = v.ToDisplayValue(),
            VariablesReference = childrenReference
        };
    }
}