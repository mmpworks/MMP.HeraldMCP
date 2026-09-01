// SPDX-License-Identifier: Apache-2.0
// Copyright (c) MMPWorks. See LICENSE for terms.
using System.Text.Json.Nodes;
using HeraldMcp.Core.Budgets;
using HeraldMcp.Core.Paths;
using HeraldMcp.Core.Query;
using HeraldMcp.Core.Sources;

namespace HeraldMcp.Server;

/// <summary>Startup configuration for the server (roots, ceiling, budgets).</summary>
public sealed record HeraldServerOptions
{
    public required IReadOnlyList<string> Roots { get; init; }

    /// <summary>Declared supported corpus ceiling (PRD section 4): 50 GiB.</summary>
    public long CeilingBytes { get; init; } = 50L * 1024 * 1024 * 1024;
    public BudgetLimits Budget { get; init; } = BudgetLimits.Default;
}

/// <summary>
/// Composes the Core pieces behind the tools: one confined resolver, one
/// source registry, one scanner, one context reader. Shared so
/// herald_sources and the query tools see the same registry (ids resolve).
/// </summary>
public sealed class HeraldService
{
    public HeraldService(HeraldServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Roots.Count == 0)
            throw new ArgumentException("At least one log root is required.", nameof(options));
        var resolver = new RootConfinedResolver(options.Roots.ToArray());
        Sources = new SourceRegistry(resolver, options.CeilingBytes);
        Scanner = new LogScanner(Sources, new ResultBudget(options.Budget));
        Context = new ContextReader(Sources);
    }

    public SourceRegistry Sources { get; }
    public LogScanner Scanner { get; }
    public ContextReader Context { get; }

    /// <summary>Reads all matching events for clustering / window-diff.</summary>
    public IReadOnlyList<HeraldEvent> ReadAll(string sourceId, EventFilter filter)
    {
        Sources.List(); // refresh bindings so the id resolves and the ceiling is checked
        return Scanner.ReadEvents(sourceId, filter);
    }

    /// <summary>Shapes a single event to masked JSON (exemplars).</summary>
    public JsonNode ShapeEvent(HeraldEvent e, bool redact) => LogScanner.ShapeEvent(e, redact);
}
