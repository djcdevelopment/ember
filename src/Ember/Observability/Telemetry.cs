using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Ember.Observability;

/// <summary>Central names and instruments for ember's OpenTelemetry signals.</summary>
public static class Telemetry
{
    /// <summary>Activity source / meter name — registered with OpenTelemetry in Program.cs.</summary>
    public const string SourceName = "Ember";

    public static readonly ActivitySource Activity = new(SourceName);

    public static readonly Meter Meter = new(SourceName);

    /// <summary>Counts slash commands handled, tagged by command name.</summary>
    public static readonly Counter<long> CommandsHandled =
        Meter.CreateCounter<long>("ember.commands.handled");
}
