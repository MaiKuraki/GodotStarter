#if TOOLS
using System;
using System.Globalization;
using System.Text;
using CycloneGames.Logging.Pipeline;
using Godot;

namespace CycloneGames.Logging.Godot.Editor;

/// <summary>
/// The plugin's single editor-side entry point: a bottom dock that shows the logging stack's live
/// state and runs the invariant harness.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why one object owns both the registration and the panel.</b> The dock scene is instantiated
/// exactly once, when the plugin is enabled, and is the only C# editor object the addon creates.
/// Giving the Project Settings registration its own node would add a second lifetime to reason
/// about for no benefit; giving it to the dock means "plugin enabled" and "editor side is set up"
/// are the same event, which is the property that matters. The coupling is deliberate and stated
/// here rather than discovered later.
/// </para>
/// <para>
/// <b>Why it shows state instead of configuring it.</b> Configuration lives in Project Settings,
/// where it belongs: values are serialized into the project file, they survive an inspector
/// reselect, and an automated export can read them without the editor UI. Putting editable fields
/// in the dock as well would create two sources of truth for the same value, and the second one
/// would silently win whenever the dock happened to be open. The dock's value is the thing
/// Project Settings cannot show: what the running backend is actually doing.
/// </para>
/// <para>
/// <b>Refresh cost.</b> Metrics refresh on a 0.25 s accumulator rather than every frame. The dock
/// builds strings, so at 4 Hz it is invisible; at frame rate it would itself become the allocation
/// source an operator would then try to blame on the logging stack.
/// </para>
/// </remarks>
[Tool]
public partial class LoggingDock : VBoxContainer
{
    private const double RefreshIntervalSeconds = 0.25;

    private Label _stateValue;
    private Label _pipelineDepthValue;
    private Label _handoffDepthValue;
    private Label _peakValue;
    private Label _throughputValue;
    private Label _dropValue;
    private Label _pumpValue;
    private Label _poolValue;
    private RichTextLabel _output;

    private double _accumulator;

    public override void _Ready()
    {
        // Registration is idempotent and never overwrites a value the user already edited, so
        // running it on every plugin enable is safe and is what makes an addon upgrade pick up
        // newly introduced keys without any migration step.
        LoggingProjectSettings.RegisterDefaults();

        BuildUi();
        Refresh();
    }

    public override void _Process(double delta)
    {
        _accumulator += delta;
        if (_accumulator < RefreshIntervalSeconds)
        {
            return;
        }

        _accumulator = 0.0;
        Refresh();
    }

    private void BuildUi()
    {
        AddThemeConstantOverride("separation", 4);

        var title = new Label { Text = "CycloneGames Logging" };
        title.AddThemeFontSizeOverride("font_size", 15);
        AddChild(title);

        var hint = new Label
        {
            Text = "Configure under Project Settings → CycloneGames Logging. "
                + "Add LoggingMonitor to a scene for periodic runtime reports."
        };
        hint.AddThemeColorOverride("font_color", new Color(0.62f, 0.62f, 0.66f));
        AddChild(hint);

        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 16);
        AddChild(grid);

        _stateValue = AddMetric(grid, "Backend");
        _pipelineDepthValue = AddMetric(grid, "Pipeline depth");
        _handoffDepthValue = AddMetric(grid, "Handoff depth");
        _peakValue = AddMetric(grid, "Peak depth");
        _throughputValue = AddMetric(grid, "Throughput");
        _dropValue = AddMetric(grid, "Dropped");
        _pumpValue = AddMetric(grid, "Pump (frames / exhausted / max)");
        _poolValue = AddMetric(grid, "Idle pools");

        var buttons = new HBoxContainer();
        AddChild(buttons);

        AddButton(buttons, "Restart backend", OnRestartPressed);
        AddButton(buttons, "Shutdown", OnShutdownPressed);
        AddButton(buttons, "Trim idle pools", OnTrimPressed);
        AddButton(buttons, "Run self-check", OnSelfCheckPressed);
        AddButton(buttons, "Copy report", OnCopyReportPressed);

        _output = new RichTextLabel
        {
            BbcodeEnabled = true,
            ScrollFollowing = true,
            CustomMinimumSize = new Vector2(0, 160),
            SizeFlagsVertical = SizeFlags.ExpandFill,
            Text = "Run the self-check to verify the stack's invariants, including the "
                + "zero-allocation producer claim."
        };
        AddChild(_output);
    }

    private static Label AddMetric(GridContainer grid, string caption)
    {
        var captionLabel = new Label { Text = caption };
        captionLabel.AddThemeColorOverride("font_color", new Color(0.70f, 0.70f, 0.74f));
        grid.AddChild(captionLabel);

        var valueLabel = new Label { Text = "—" };
        grid.AddChild(valueLabel);
        return valueLabel;
    }

    private static void AddButton(Container parent, string text, Action onPressed)
    {
        var button = new Button { Text = text };
        // A lambda rather than the method group directly: Godot's signal is a generated
        // `PressedEventHandler` delegate, and C# will not implicitly convert a differently-named
        // delegate type to it even when the signatures match.
        button.Pressed += () => onPressed();
        parent.AddChild(button);
    }

    private void Refresh()
    {
        if (!IsInstanceValid(_stateValue))
        {
            return;
        }

        LoggingHandoffStatistics handoff = GodotConsoleLogSink.GetStatistics();
        bool running = LoggingBootstrap.TryGetOwnedPipeline(out LogPipeline pipeline);

        if (!running)
        {
            _stateValue.Text = LoggingProjectSettings.IsRegistered()
                ? "not running (Project Settings registered)"
                : "not running (Project Settings NOT registered)";
            _stateValue.AddThemeColorOverride("font_color", new Color(0.85f, 0.7f, 0.35f));
        }
        else if (pipeline.IsFaulted)
        {
            _stateValue.Text = "FAULTED — restart required";
            _stateValue.AddThemeColorOverride("font_color", new Color(0.9f, 0.4f, 0.4f));
        }
        else
        {
            _stateValue.Text = "running (owned writer installed)";
            _stateValue.AddThemeColorOverride("font_color", new Color(0.55f, 0.8f, 0.55f));
        }

        int pipelineQueued = 0;
        int pipelinePeak = 0;
        long processed = 0L;
        long dropped = 0L;
        long droppedCritical = 0L;
        long sinkFailures = 0L;
        long quarantined = 0L;

        if (running)
        {
            LogPipelineStatistics statistics = pipeline.GetStatistics();
            pipelineQueued = statistics.QueuedCount;
            pipelinePeak = statistics.PeakQueuedCount;
            processed = statistics.ProcessedMessageCount;
            dropped = statistics.DroppedMessageCount;
            droppedCritical = statistics.DroppedCriticalCount;
            sinkFailures = statistics.SinkFailureCount;
            quarantined = statistics.QuarantinedSinkCount;
        }

        CultureInfo invariant = CultureInfo.InvariantCulture;
        _pipelineDepthValue.Text = pipelineQueued + " / " + handoff.QueuedCount;
        _handoffDepthValue.Text = handoff.QueuedCharacters + " chars queued, "
            + handoff.InFlightCount + " in flight";
        _peakValue.Text = pipelinePeak + " pipeline, " + handoff.PeakQueuedCount + " handoff";
        _throughputValue.Text = processed + " processed, " + sinkFailures + " sink failures, "
            + quarantined + " quarantined";
        _dropValue.Text = (dropped + handoff.DroppedMessageCount) + " total ("
            + (droppedCritical + handoff.DroppedCriticalCount) + " critical)";

        double averagePump = handoff.PumpFrameCount > 0
            ? (double)handoff.TotalPumpMicroseconds / handoff.PumpFrameCount
            : 0.0;
        _pumpValue.Text = handoff.PumpFrameCount
            + " / " + handoff.PumpBudgetExhaustedCount
            + " / " + handoff.MaxPumpMicroseconds + " us"
            + " (avg " + averagePump.ToString("F1", invariant) + " us)";

        LogMemoryPoolStatistics pools = LogMemoryPools.GetStatistics();
        _poolValue.Text = pools.RetainedLogEvents + " events, "
            + pools.RetainedStringBuilders + " builders";
    }

    private void OnRestartPressed()
    {
        Append("== Restart backend ==");
        try
        {
            LoggingReinitializationResult result = LoggingBootstrap.Reinitialize();
            Append("shutdown: " + result.Shutdown.Status
                + ", dropped=" + result.Shutdown.DroppedMessageCount);
            Append("initialization: " + result.Initialization.Status
                + ", writerInstalled=" + result.Initialization.WriterInstalled);
        }
        catch (Exception exception) when (!(exception is OutOfMemoryException))
        {
            Append("[color=#e06666]Restart failed: " + exception.Message + "[/color]");
        }

        Refresh();
    }

    private void OnShutdownPressed()
    {
        Append("== Shutdown ==");
        try
        {
            LogPipelineShutdownResult result = LoggingBootstrap.Shutdown(LogFlushMode.Buffered);
            Append("status=" + result.Status
                + " dropped=" + result.DroppedMessageCount
                + " sinksFlushed=" + result.SinksFlushed);
        }
        catch (Exception exception) when (!(exception is OutOfMemoryException))
        {
            Append("[color=#e06666]Shutdown failed: " + exception.Message + "[/color]");
        }

        Refresh();
    }

    private void OnTrimPressed()
    {
        // A bounded trim step rather than a full clear: the pools are what keeps the steady state
        // allocation-free, so releasing them wholesale would trade peak memory for a latency
        // spike on the next burst.
        LogMemoryTrimResult result = LogMemoryPools.TrimStep(64, 32, 4096);
        Append("== Trim idle pools ==");
        Append("released " + result.WorkConsumed + " entries ("
            + result.ReleasedLogEvents + " events, " + result.ReleasedStringBuilders + " builders); "
            + "remaining " + result.RemainingLogEvents + " / " + result.RemainingStringBuilders
            + "; more=" + result.HasMoreIdleEntries);
        Refresh();
    }

    private void OnSelfCheckPressed()
    {
        Append("== Self-check ==");
        try
        {
            // 50k iterations in the editor: enough for a trustworthy allocation measurement,
            // small enough that the editor stays responsive.
            string report = LoggingSelfCheck.RunAndFormat(50_000, LoggingRuntimeHost.ReadHarnessCounters());
            Append(report.Replace("PASS ", "[color=#7ac77a]PASS[/color] ").Replace("FAIL ", "[color=#e06666]FAIL[/color] "));
            Append(LoggingSelfCheck.FormatPoolReport());
        }
        catch (Exception exception) when (!(exception is OutOfMemoryException))
        {
            Append("[color=#e06666]Self-check threw: " + exception.Message + "[/color]");
        }
    }

    private void OnCopyReportPressed()
    {
        var builder = new StringBuilder(1024);
        builder.Append(LoggingDiagnostics.Enabled
            ? LoggingDiagnostics.ToReport(LoggingDiagnostics.Sample())
            : "telemetry disabled (LoggingDiagnostics.Enabled = false)");
        builder.Append('\n');
        builder.Append(LoggingSelfCheck.FormatPoolReport());
        builder.Append('\n');
        builder.Append("self-check: ");
        builder.Append(LoggingSelfCheck.RunAndFormat(20_000, LoggingRuntimeHost.ReadHarnessCounters()));

        DisplayServer.ClipboardSet(builder.ToString());
        Append("Copied report to clipboard (" + builder.Length + " chars).");
    }

    private void Append(string line)
    {
        if (!IsInstanceValid(_output))
        {
            return;
        }

        _output.AppendText(line);
        _output.AppendText("\n");
    }
}
#endif
