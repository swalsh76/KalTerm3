using KalTerm.Client;
using KalTerm.Terminal.Markets;
using KalTerm.Terminal.Services;

namespace KalTerm.Terminal.Layouts;

/// <summary>What reading one layout gave.</summary>
/// <param name="State">The layout, or <see langword="null"/> when there is none to use.</param>
/// <param name="Missing">There is no layout by that name (a new one is made by the first save).</param>
/// <param name="Problem">A sentence for the person when the layout could not be read or used; <see langword="null"/> otherwise.</param>
internal sealed record LayoutRead(LayoutState? State, bool Missing, string? Problem);

/// <summary>
/// The person's saved layouts on the server. Like <see cref="WatchlistService"/> every method answers a failure as a sentence for the person instead of an exception, and tells apart a layout that is not there (fine: it is made
/// by the first save) from one that is there but cannot be used (a newer terminal wrote it, or it is not a layout) and must not be written over.
/// </summary>
/// <param name="api">The client for the requests.</param>
internal sealed class LayoutService(IKalTermApi api)
{
    /// <summary>The most layouts the server allows one person.</summary>
    public const int MaxLayouts = 20;

    /// <summary>The names of the saved layouts.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The names, in order, or the reason they could not be read.</returns>
    public async Task<(IReadOnlyList<string> Names, string? Problem)> ListAsync(CancellationToken cancellationToken)
    {
        try
        {
            var list = await api.GetLayoutsAsync(cancellationToken).ConfigureAwait(true);
            return ([.. list.Layouts.Select(l => l.Name)], null);
        }
        catch (Exception ex) when (ApiProblem.IsExpected(ex))
        {
            return ([], ApiProblem.Say(ex, "read your layouts"));
        }
    }

    /// <summary>Reads one layout.</summary>
    /// <param name="name">The layout.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>What it gave.</returns>
    public async Task<LayoutRead> ReadAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            var dto = await api.GetLayoutAsync(name, cancellationToken).ConfigureAwait(true);
            if (LayoutState.FromJson(dto.Body) is not { } state)
            {
                return new LayoutRead(null, false, $"The layout {name} could not be read, so the normal layout is used and nothing is saved over it. Use Save as… to keep this one.");
            }

            return state.Version > LayoutState.Current
                ? new LayoutRead(null, false, $"The layout {name} was saved by a newer KalTerm, so the normal layout is used and nothing is saved over it. Use Save as… to keep this one.")
                : new LayoutRead(state, false, null);
        }
        catch (ApiException ex) when (ex.Code == "not_found")
        {
            return new LayoutRead(null, true, null);
        }
        catch (Exception ex) when (ApiProblem.IsExpected(ex))
        {
            return new LayoutRead(null, false, ApiProblem.Say(ex, $"read the layout {name}"));
        }
    }

    /// <summary>Saves a layout: makes it, or replaces the one with that name.</summary>
    /// <param name="name">The layout.</param>
    /// <param name="state">What to save.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Why it failed, or <see langword="null"/>.</returns>
    public async Task<string?> SaveAsync(string name, LayoutState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        try
        {
            _ = await api.PutLayoutAsync(name, state.ToJson(), cancellationToken).ConfigureAwait(true);
            return null;
        }
        catch (ApiException ex) when (ex.Code == "limit_reached")
        {
            return $"You already have {MaxLayouts} layouts. Delete one first.";
        }
        catch (ApiException ex) when (ex.Code == "invalid_parameter")
        {
            return $"The server did not accept the layout {name}.";
        }
        catch (Exception ex) when (ApiProblem.IsExpected(ex))
        {
            return ApiProblem.Say(ex, $"save the layout {name}");
        }
    }

    /// <summary>Deletes a layout.</summary>
    /// <param name="name">The layout.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Why it failed, or <see langword="null"/>.</returns>
    public async Task<string?> DeleteAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            await api.DeleteLayoutAsync(name, cancellationToken).ConfigureAwait(true);
            return null;
        }
        catch (ApiException ex) when (ex.Code == "not_found")
        {
            return null;   // already gone: what was asked for
        }
        catch (Exception ex) when (ApiProblem.IsExpected(ex))
        {
            return ApiProblem.Say(ex, $"delete the layout {name}");
        }
    }
}
