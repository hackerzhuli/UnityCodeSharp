using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Mono.Debugging.Soft;
using MonoClient = Mono.Debugging.Client;
using DebugProtocol = Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;

namespace MonoDebugger;

/// <summary>
///     Debug session that handles VS Code debug protocol communication and Unity debugging.
/// </summary>
public class DebugSession : DebugAdapterBase, IDisposable
{
    private readonly Dictionary<int, MonoClient.StackFrame> _frameHandles = new();
    private readonly Dictionary<int, MonoClient.SourceLocation> _gotoHandles = new();
    private readonly SoftDebuggerSession _session = new();
    private readonly Dictionary<int, Func<MonoClient.ObjectValue[]>> _variableHandles = new();
    private readonly SymbolServer _symbolServer = new();
    private Launch _launch = null!;
    private int _nextHandle = 1000;

    /// <summary>
    ///     Initializes a new instance of the DebugSession class.
    /// </summary>
    /// <param name="input">Input stream for protocol communication</param>
    /// <param name="output">Output stream for protocol communication</param>
    public DebugSession(Stream input, Stream output)
    {
        Debug.Log("[DebugSession] Initializing new debug session");
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
        Debug.Log("[DebugSession] Debug session initialized successfully");
    }

    public DebugOptions? Options { get; set; }
    
    public SymbolServer SymbolServer => _symbolServer;

    /// <summary>
    ///     Starts the debug session and begins protocol communication.
    /// </summary>
    public void Start()
    {
        Debug.Log("[DebugSession] Starting debug session protocol communication");
        Protocol.LogMessage += LogMessage;
        Protocol.DispatcherError += LogError;
        Debug.Log("[DebugSession] Protocol event handlers attached, starting protocol run");
        Protocol.Run();
        Debug.Log("[DebugSession] Protocol.Run() completed");
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
    private void OnErrorDataReceived(string stderr)
    {
        SendMessageEvent(DebugProtocol.OutputEvent.CategoryValue.Stderr, stderr);
    }

    /// <summary>
    ///     Handles debug data received from the debugged process.
    /// </summary>
    /// <param name="debug">Debug message</param>
    private void OnDebugDataReceived(string debug)
    {
        SendMessageEvent(DebugProtocol.OutputEvent.CategoryValue.Console, debug);
    }
    
    /// <summary>
    ///     Handles unhandled exceptions in the debug session.
    /// </summary>
    /// <param name="ex">The unhandled exception</param>
    private void OnUnhandledException(Exception ex)
    {
        Debug.LogError($"[DebugSession] Unhandled exception occurred: {ex.Message}", ex);
        Debug.Log($"[DebugSession] Session state - IsRunning: {_session.IsRunning}, HasExited: {_session.HasExited}");
        Debug.Log("[DebugSession] Disposing launch due to unhandled exception");
        _launch?.Dispose();
        Debug.Log("[DebugSession] Launch disposed after unhandled exception");
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
        // There are too many messages, don't log
        // Debug.Log(args.Message);
    }

    private void LogError(object? sender, DispatcherErrorEventArgs args)
    {
        Debug.LogError($"[DebugSession] Protocol dispatcher error occurred: {args.Exception.Message}", args.Exception);
        Debug.Log($"[DebugSession] Dispatcher error sender: {sender?.GetType().Name ?? "null"}");
        Debug.Log($"[DebugSession] Session state before handling dispatcher error - IsRunning: {_session.IsRunning}, HasExited: {_session.HasExited}");
        Debug.LogError($"[Fatal] {args.Exception.Message}", args.Exception);
        Debug.Log("[DebugSession] Calling OnUnhandledException due to dispatcher error");
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
        try
        {
            Debug.Log("[DebugSession] HandleAttachRequest started");
            Debug.Log($"[DebugSession] Configuration properties count: {arguments.ConfigurationProperties?.Count ?? 0}");
            
            var configuration = new LaunchConfig(arguments.ConfigurationProperties);
            Debug.Log("[DebugSession] LaunchConfig created successfully");
            
            _symbolServer.SetEventLogger(OnDebugDataReceived);
            Debug.Log("[DebugSession] Symbol server event logger set");
            
            _launch = configuration.GetLaunchAgent();
            Debug.Log($"[DebugSession] Launch agent created: {_launch?.GetType().Name ?? "null"}");
            
            _launch.Init(this);
            Debug.Log("[DebugSession] Launch agent initialized");
            
            Debug.Log($"[DebugSession] Session state before connect - IsRunning: {_session.IsRunning}, HasExited: {_session.HasExited}");
            _launch.Connect(_session);
            Debug.Log($"[DebugSession] Session state after connect - IsRunning: {_session.IsRunning}, HasExited: {_session.HasExited}");
            
            Debug.Log("[DebugSession] HandleAttachRequest completed successfully");
            return new DebugProtocol.AttachResponse();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[DebugSession] HandleAttachRequest failed: {ex.Message}", ex);
            throw CreateException(ex);
        }
    }

    private Exception CreateException(Exception ex)
    {
        if (ex is ProtocolException)
            return ex;
        MonoClient.DebuggerLoggingService.CustomLogger?.LogError($"[Handled] {ex.Message}", ex);
        return ServerExtensions.GetProtocolException(ex.Message);
    }

    protected override DebugProtocol.DisconnectResponse HandleDisconnectRequest(
        DebugProtocol.DisconnectArguments arguments)
    {
        Debug.Log("[DebugSession] HandleDisconnectRequest started");
        Debug.Log($"[DebugSession] Session state before disconnect - IsRunning: {_session.IsRunning}, HasExited: {_session.HasExited}");
        
        Debug.Log("[DebugSession] Detaching session");
        _session.Detach();
        Debug.Log("[DebugSession] Session detached");
        
        Debug.Log("[DebugSession] Disposing session");
        _session.Dispose();
        Debug.Log("[DebugSession] Session disposed");
        
        Debug.Log("[DebugSession] Disposing launch");
        _launch?.Dispose();
        Debug.Log("[DebugSession] Launch disposed");
        
        Debug.Log("[DebugSession] HandleDisconnectRequest completed");
        return new DebugProtocol.DisconnectResponse();
    }

    protected override DebugProtocol.ContinueResponse HandleContinueRequest(DebugProtocol.ContinueArguments arguments)
    {
        try
        {
            Debug.Log($"[DebugSession] HandleContinueRequest - Session state: IsRunning={_session.IsRunning}, HasExited={_session.HasExited}");
            if (_session is { IsRunning: false, HasExited: false })
            {
                Debug.Log("[DebugSession] Calling session.Continue()");
                _session.Continue();
                Debug.Log($"[DebugSession] Session.Continue() completed - New state: IsRunning={_session.IsRunning}, HasExited={_session.HasExited}");
            }
            else
            {
                Debug.LogWarning($"[DebugSession] Continue request ignored - Session state: IsRunning={_session.IsRunning}, HasExited={_session.HasExited}");
            }
            return new DebugProtocol.ContinueResponse();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[DebugSession] HandleContinueRequest failed: {ex.Message}", ex);
            throw CreateException(ex);
        }
    }

    protected override DebugProtocol.NextResponse HandleNextRequest(DebugProtocol.NextArguments arguments)
    {
        try
        {
            Debug.Log($"[DebugSession] HandleNextRequest - Session state: IsRunning={_session.IsRunning}, HasExited={_session.HasExited}");
            if (!_session.IsRunning && !_session.HasExited)
            {
                Debug.Log("[DebugSession] Calling session.NextLine()");
                _session.NextLine();
                Debug.Log($"[DebugSession] Session.NextLine() completed - New state: IsRunning={_session.IsRunning}, HasExited={_session.HasExited}");
            }
            else
            {
                Debug.LogWarning($"[DebugSession] NextLine request ignored - Session state: IsRunning={_session.IsRunning}, HasExited={_session.HasExited}");
            }
            return new DebugProtocol.NextResponse();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[DebugSession] HandleNextRequest failed: {ex.Message}", ex);
            throw CreateException(ex);
        }
    }

    protected override DebugProtocol.StepInResponse HandleStepInRequest(DebugProtocol.StepInArguments arguments)
    {
        try
        {
            Debug.Log($"[DebugSession] HandleStepInRequest - Session state: IsRunning={_session.IsRunning}, HasExited={_session.HasExited}");
            if (!_session.IsRunning && !_session.HasExited)
            {
                Debug.Log("[DebugSession] Calling session.StepLine()");
                _session.StepLine();
                Debug.Log($"[DebugSession] Session.StepLine() completed - New state: IsRunning={_session.IsRunning}, HasExited={_session.HasExited}");
            }
            else
            {
                Debug.LogWarning($"[DebugSession] StepLine request ignored - Session state: IsRunning={_session.IsRunning}, HasExited={_session.HasExited}");
            }
            return new DebugProtocol.StepInResponse();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[DebugSession] HandleStepInRequest failed: {ex.Message}", ex);
            throw CreateException(ex);
        }
    }

    protected override DebugProtocol.StepOutResponse HandleStepOutRequest(DebugProtocol.StepOutArguments arguments)
    {
        try
        {
            Debug.Log($"[DebugSession] HandleStepOutRequest - Session state: IsRunning={_session.IsRunning}, HasExited={_session.HasExited}");
            if (!_session.IsRunning && !_session.HasExited)
            {
                Debug.Log("[DebugSession] Calling session.Finish()");
                _session.Finish();
                Debug.Log($"[DebugSession] Session.Finish() completed - New state: IsRunning={_session.IsRunning}, HasExited={_session.HasExited}");
            }
            else
            {
                Debug.LogWarning($"[DebugSession] Finish request ignored - Session state: IsRunning={_session.IsRunning}, HasExited={_session.HasExited}");
            }
            return new DebugProtocol.StepOutResponse();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[DebugSession] HandleStepOutRequest failed: {ex.Message}", ex);
            throw CreateException(ex);
        }
    }

    protected override DebugProtocol.PauseResponse HandlePauseRequest(DebugProtocol.PauseArguments arguments)
    {
        try
        {
            Debug.Log($"[DebugSession] HandlePauseRequest - Session state: IsRunning={_session.IsRunning}, HasExited={_session.HasExited}");
            if (_session.IsRunning)
            {
                Debug.Log("[DebugSession] Calling session.Stop()");
                _session.Stop();
                Debug.Log($"[DebugSession] Session.Stop() completed - New state: IsRunning={_session.IsRunning}, HasExited={_session.HasExited}");
            }
            else
            {
                Debug.LogWarning($"[DebugSession] Stop request ignored - Session not running: IsRunning={_session.IsRunning}, HasExited={_session.HasExited}");
            }
            return new DebugProtocol.PauseResponse();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[DebugSession] HandlePauseRequest failed: {ex.Message}", ex);
            throw CreateException(ex);
        }
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
                foreach (var condition in option.Condition[1..].Split(',', StringSplitOptions.RemoveEmptyEntries))
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
        try
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

                if (frame.Language == "Transition" && (Options?.SkipNativeTransitions ?? true))
                    continue;

                DebugProtocol.Source? source = null;
                var handle = _nextHandle++;
                _frameHandles[handle] = frame;
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
                        AlternateSourceReference = handle,
                        VsSourceLinkInfo = frame.SourceLocation.SourceLink.ToSourceLinkInfo(),
                        Name = string.IsNullOrEmpty(frame.SourceLocation.FileName)
                            ? Path.GetFileName(frame.FullModuleName)
                            : Path.GetFileName(frame.SourceLocation.FileName)
                    };

                stackFrames.Add(new DebugProtocol.StackFrame
                {
                    Id = handle,
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
        }
        catch (Exception ex)
        {
            throw CreateException(ex);
        }
    }

    protected override DebugProtocol.ScopesResponse HandleScopesRequest(DebugProtocol.ScopesArguments arguments)
    {
        try
        {
            var frameId = arguments.FrameId;
            var frame = _frameHandles.GetValueOrDefault(frameId);
            var scopes = new List<DebugProtocol.Scope>();

            if (frame == null)
                throw new ProtocolException("frame not found");

            Func<MonoClient.ObjectValue[]> value = frame.GetAllLocals;
            var handle = _nextHandle++;
            _variableHandles[handle] = value;
            scopes.Add(new DebugProtocol.Scope
            {
                Name = "Locals",
                PresentationHint = DebugProtocol.Scope.PresentationHintValue.Locals,
                VariablesReference = handle
            });
            
            return new DebugProtocol.ScopesResponse(scopes);
        }
        catch (Exception ex)
        {
            throw CreateException(ex);
        }
    }

    protected override DebugProtocol.VariablesResponse HandleVariablesRequest(
        DebugProtocol.VariablesArguments arguments)
    {
        try
        {
            var reference = arguments.VariablesReference;
            var variables = new List<DebugProtocol.Variable>();

            if (_variableHandles.TryGetValue(reference, out var getChildrenDelegate))
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
        }
        catch (Exception ex)
        {
            throw CreateException(ex);
        }
    }

    protected override DebugProtocol.ThreadsResponse HandleThreadsRequest(DebugProtocol.ThreadsArguments arguments)
    {
        try
        {
            var threads = new List<DebugProtocol.Thread>();
            var process = _session.GetProcesses().FirstOrDefault();
            if (process == null)
                return new DebugProtocol.ThreadsResponse();

            // order them according to id, this will make main thread appear first
            foreach (var thread in process.GetThreads().OrderBy(t => t.Id))
            {
                var tid = (int)thread.Id;
                var threadName = thread.Name.ToThreadName(tid);
                // Burst threads are editor related and won't run user code, as far as I know
                if(threadName.StartsWith("Burst-")){
                    continue;
                }
                threads.Add(new DebugProtocol.Thread(tid, threadName));
            }

            return new DebugProtocol.ThreadsResponse(threads);
        }
        catch (Exception ex)
        {
            throw CreateException(ex);
        }
    }

    protected override DebugProtocol.EvaluateResponse HandleEvaluateRequest(DebugProtocol.EvaluateArguments arguments)
    {
        try
        {
            var expression = arguments.TrimExpression();
            var frame = _frameHandles.GetValueOrDefault(arguments.FrameId ?? 0);
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
            {
                Func<MonoClient.ObjectValue[]> value1 = value.GetAllChildren;
                var handle1 = _nextHandle++;
                _variableHandles[handle1] = value1;
                handle = handle1;
            }

            return new DebugProtocol.EvaluateResponse(value.ToDisplayValue(), handle);
        }
        catch (Exception ex)
        {
            throw CreateException(ex);
        }
    }

    protected override DebugProtocol.SourceResponse HandleSourceRequest(DebugProtocol.SourceArguments arguments)
    {
        try
        {
            var sourceLinkUri = arguments.Source.VsSourceLinkInfo?.Url;
            if (!string.IsNullOrEmpty(sourceLinkUri) && _session.Options.AutomaticSourceLinkDownload ==
                MonoClient.AutomaticSourceDownload.Always)
            {
                var content = _symbolServer.DownloadSourceFile(sourceLinkUri);
                if (!string.IsNullOrEmpty(content))
                    return new DebugProtocol.SourceResponse(content);
            }

            var frame = _frameHandles.GetValueOrDefault(arguments.Source.AlternateSourceReference ?? -1);
            if (frame == null)
                throw ServerExtensions.GetProtocolException("No source available");

            return new DebugProtocol.SourceResponse(frame.GetAssemblyCode());
        }
        catch (Exception ex)
        {
            throw CreateException(ex);
        }
    }

    protected override DebugProtocol.ExceptionInfoResponse HandleExceptionInfoRequest(
        DebugProtocol.ExceptionInfoArguments arguments)
    {
        try
        {
            var ex = _session.FindException(arguments.ThreadId);
            if (ex == null)
                throw new ProtocolException("No exception available");

            return ex.ToExceptionInfoResponse();
        }
        catch (Exception ex)
        {
            throw CreateException(ex);
        }
    }

    protected override DebugProtocol.CompletionsResponse HandleCompletionsRequest(
        DebugProtocol.CompletionsArguments arguments)
    {
        try
        {
            var frame = _frameHandles.GetValueOrDefault(arguments.FrameId ?? 0);
            if (frame == null)
                throw new ProtocolException("no active stackframe");

            string? resolvedText = null;
            if (_session.Options.EvaluationOptions.UseExternalTypeResolver)
            {
                var lastTriggerIndex = arguments.Text.LastIndexOf('.');
                if (lastTriggerIndex > 0)
                {
                    resolvedText = frame.ResolveExpression(arguments.Text[..lastTriggerIndex]);
                    resolvedText += arguments.Text[lastTriggerIndex..];
                }
            }

            var completionData = frame.GetExpressionCompletionData(resolvedText ?? arguments.Text);
            if (completionData?.Items == null)
                return new DebugProtocol.CompletionsResponse();

            return new DebugProtocol.CompletionsResponse(
                completionData.Items.Select(x => x.ToCompletionItem()).ToList());
        }
        catch (Exception ex)
        {
            throw CreateException(ex);
        }
    }

    protected override DebugProtocol.SetVariableResponse HandleSetVariableRequest(
        DebugProtocol.SetVariableArguments arguments)
    {
        var variablesDelegate = _variableHandles.GetValueOrDefault(arguments.VariablesReference);
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
        var value = new MonoClient.SourceLocation(string.Empty, arguments.Source.Path, arguments.Line, arguments.Column ?? 1, 0,
            0);
        var handle = _nextHandle++;
        _gotoHandles[handle] = value;
        return arguments.ToJumpToCursorTarget(handle);
    }

    protected override DebugProtocol.GotoResponse HandleGotoRequest(DebugProtocol.GotoArguments arguments)
    {
        try
        {
            var target = _gotoHandles.GetValueOrDefault(arguments.TargetId);
            if (target == null)
                throw new ProtocolException("GotoTarget not found");

            _session.SetNextStatement(target.FileName, target.Line, target.Column);
            return new DebugProtocol.GotoResponse();
        }
        catch (Exception ex)
        {
            throw CreateException(ex);
        }
    }

    private void TargetStopped(object? sender, MonoClient.TargetEventArgs e)
    {
        Debug.Log($"[DebugSession] TargetStopped event fired - ThreadId: {e.Thread.Id}, ThreadName: {e.Thread.Name}");
        Debug.Log($"[DebugSession] Session state in TargetStopped - IsRunning: {_session.IsRunning}, HasExited: {_session.HasExited}");
        
        Debug.Log("[DebugSession] Resetting handles due to target stopped");
        ResetHandles();
        
        Debug.Log("[DebugSession] Sending StoppedEvent with Pause reason");
        Protocol.SendEvent(new DebugProtocol.StoppedEvent(DebugProtocol.StoppedEvent.ReasonValue.Pause)
        {
            ThreadId = (int)e.Thread.Id,
            AllThreadsStopped = true
        });
        Debug.Log("[DebugSession] TargetStopped event handling completed");
    }

    private void TargetHitBreakpoint(object? sender, MonoClient.TargetEventArgs e)
    {
        Debug.Log($"[DebugSession] TargetHitBreakpoint event fired - ThreadId: {e.Thread.Id}, ThreadName: {e.Thread.Name}");
        Debug.Log($"[DebugSession] Session state in TargetHitBreakpoint - IsRunning: {_session.IsRunning}, HasExited: {_session.HasExited}");
        
        try
        {
            var backtrace = e.Thread.Backtrace;
            if (backtrace != null && backtrace.FrameCount > 0)
            {
                var frame = backtrace.GetFrame(0);
                Debug.Log($"[DebugSession] Breakpoint hit at: {frame?.SourceLocation?.FileName}:{frame?.SourceLocation?.Line}");
                Debug.Log($"[DebugSession] Method: {frame?.FullTypeName}");
            }
            else
            {
                Debug.LogWarning("[DebugSession] No backtrace available for breakpoint hit");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[DebugSession] Error getting breakpoint location info: {ex.Message}", ex);
        }
        
        Debug.Log("[DebugSession] Resetting handles due to breakpoint hit");
        ResetHandles();
        
        Debug.Log("[DebugSession] Sending StoppedEvent with Breakpoint reason");
        try
        {
            Protocol.SendEvent(new DebugProtocol.StoppedEvent(DebugProtocol.StoppedEvent.ReasonValue.Breakpoint)
            {
                ThreadId = (int)e.Thread.Id,
                AllThreadsStopped = true
            });
            Debug.Log("[DebugSession] StoppedEvent sent successfully");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[DebugSession] Error sending StoppedEvent: {ex.Message}", ex);
            throw;
        }
        
        Debug.Log("[DebugSession] TargetHitBreakpoint event handling completed");
    }

    private void TargetExceptionThrown(object? sender, MonoClient.TargetEventArgs e)
    {
        Debug.Log($"[DebugSession] TargetExceptionThrown event fired - ThreadId: {e.Thread.Id}, ThreadName: {e.Thread.Name}");
        Debug.Log($"[DebugSession] Session state in TargetExceptionThrown - IsRunning: {_session.IsRunning}, HasExited: {_session.HasExited}");
        
        Debug.Log("[DebugSession] Resetting handles due to exception");
        ResetHandles();
        
        try
        {
            var ex = _session.FindException(e.Thread.Id);
            Debug.Log($"[DebugSession] Exception found: Type={ex?.Type ?? "Unknown"}, Message={ex?.Message ?? "No message"}");
            
            Debug.Log("[DebugSession] Sending StoppedEvent with Exception reason");
            Protocol.SendEvent(new DebugProtocol.StoppedEvent(DebugProtocol.StoppedEvent.ReasonValue.Exception)
            {
                Description = "Paused on exception",
                Text = ex?.Type ?? "Exception",
                ThreadId = (int)e.Thread.Id,
                AllThreadsStopped = true
            });
            Debug.Log("[DebugSession] Exception StoppedEvent sent successfully");
        }
        catch (Exception findEx)
        {
            Debug.LogError($"[DebugSession] Error handling exception event: {findEx.Message}", findEx);
            throw;
        }
        
        Debug.Log("[DebugSession] TargetExceptionThrown event handling completed");
    }

    private void TargetReady(object? sender, MonoClient.TargetEventArgs e)
    {
        Protocol.SendEvent(new DebugProtocol.InitializedEvent());
    }

    private void TargetExited(object? sender, MonoClient.TargetEventArgs e)
    {
        Debug.Log($"[DebugSession] TargetExited event fired - ThreadId: {e.Thread?.Id ?? 0}");
        Debug.Log($"[DebugSession] Session state in TargetExited - IsRunning: {_session.IsRunning}, HasExited: {_session.HasExited}");
        
        try
        {
            var processes = _session.GetProcesses();
            Debug.Log($"[DebugSession] Active processes count: {processes?.Length ?? 0}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[DebugSession] Error getting process info in TargetExited: {ex.Message}", ex);
        }
        
        Debug.Log("[DebugSession] Sending TerminatedEvent due to target exit");
        try
        {
            Protocol.SendEvent(new DebugProtocol.TerminatedEvent());
            Debug.Log("[DebugSession] TerminatedEvent sent successfully");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[DebugSession] Error sending TerminatedEvent: {ex.Message}", ex);
        }
        
        Debug.Log("[DebugSession] TargetExited event handling completed");
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
        Debug.LogError($"[DebugSession] OnExceptionHandled called: {ex.Message}", ex);
        Debug.Log($"[DebugSession] Session state in OnExceptionHandled - IsRunning: {_session.IsRunning}, HasExited: {_session.HasExited}");
        MonoClient.DebuggerLoggingService.CustomLogger?.LogError($"[Handled] {ex.Message}", ex);
        Debug.Log("[DebugSession] OnExceptionHandled returning true");
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
        Debug.Log($"[DebugSession] ResetHandles called - Current handles: Frame={_frameHandles.Count}, Goto={_gotoHandles.Count}, Variable={_variableHandles.Count}, NextHandle={_nextHandle}");
        _frameHandles.Clear();
        _gotoHandles.Clear();
        _variableHandles.Clear();
        _nextHandle = 1000;
        Debug.Log("[DebugSession] All handles reset successfully");
    }

    private DebugProtocol.Variable CreateVariable(MonoClient.ObjectValue v)
    {
        var childrenReference = 0;
        if (v.HasChildren && !v.HasNullValue())
        {
            Func<MonoClient.ObjectValue[]> value = v.GetAllChildren;
            var handle = _nextHandle++;
            _variableHandles[handle] = value;
            childrenReference = handle;
        }

        return new DebugProtocol.Variable
        {
            Name = v.Name,
            Type = v.TypeName,
            Value = v.ToDisplayValue(),
            VariablesReference = childrenReference
        };
    }

    /// <summary>
    /// Disposes the resources used by the DebugSession.
    /// </summary>
    public void Dispose()
    {
        Debug.Log("[DebugSession] Dispose called - cleaning up resources");
        Debug.Log($"[DebugSession] Final session state - IsRunning: {_session?.IsRunning ?? false}, HasExited: {_session?.HasExited ?? true}");
        
        try
        {
            _symbolServer?.Dispose();
            Debug.Log("[DebugSession] Symbol server disposed successfully");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[DebugSession] Error disposing symbol server: {ex.Message}", ex);
        }
        
        Debug.Log("[DebugSession] Dispose completed");
    }
}