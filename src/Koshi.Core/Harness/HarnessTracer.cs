namespace Koshi.Core.Harness;

using System.Diagnostics;

/// <summary>
/// Abstracts tracing over System.Diagnostics.ActivitySource.
/// OpenTelemetry compatible but keeps boilerplate out of business logic.
/// </summary>
public interface IHarnessTracer
{
    /// <summary>Start a named activity (span). Returns null if no listener is attached.</summary>
    Activity? StartActivity(string name, ActivityKind kind = ActivityKind.Internal);

    /// <summary>Record an event on the current activity.</summary>
    void AddEvent(string name, IDictionary<string, object?>? tags = null);
}

/// <summary>
/// Default tracer using System.Diagnostics.ActivitySource.
/// Wire up an OpenTelemetry exporter (Console, OTLP, etc.) to see traces.
/// </summary>
public sealed class HarnessTracer : IHarnessTracer
{
    private static readonly ActivitySource Source = new("Koshi.Harness", "1.0.0");

    public Activity? StartActivity(string name, ActivityKind kind = ActivityKind.Internal) =>
        Source.StartActivity(name, kind);

    public void AddEvent(string name, IDictionary<string, object?>? tags = null)
    {
        var current = Activity.Current;
        if (current is null) return;

        if (tags is null)
        {
            current.AddEvent(new ActivityEvent(name));
        }
        else
        {
            var activityTags = new ActivityTagsCollection(
                tags.Select(kv => new KeyValuePair<string, object?>(kv.Key, kv.Value)));
            current.AddEvent(new ActivityEvent(name, tags: activityTags));
        }
    }

    /// <summary>The ActivitySource name for configuring OpenTelemetry listeners.</summary>
    public static string SourceName => Source.Name;
}

/// <summary>
/// No-op tracer for testing or when tracing is disabled.
/// </summary>
public sealed class NullTracer : IHarnessTracer
{
    public static readonly NullTracer Instance = new();
    public Activity? StartActivity(string name, ActivityKind kind = ActivityKind.Internal) => null;
    public void AddEvent(string name, IDictionary<string, object?>? tags = null) { }
}
