using BeatRelay.Ranking;

namespace BeatRelay.UI;

public sealed class OverlayUpdateResult
{
    public OverlayUpdateResult(
        OverlayViewModel viewModel,
        int? pageToFetch,
        bool forcePageRefresh = false,
        ProjectionResult? projection = null,
        int? fetchPageSize = null,
        bool mergeFetchedPageAsScan = false)
    {
        ViewModel = viewModel;
        PageToFetch = pageToFetch;
        ForcePageRefresh = forcePageRefresh;
        Projection = projection;
        FetchPageSize = fetchPageSize;
        MergeFetchedPageAsScan = mergeFetchedPageAsScan;
    }

    public OverlayViewModel ViewModel { get; }

    public int? PageToFetch { get; }

    public bool ForcePageRefresh { get; }

    public ProjectionResult? Projection { get; }

    public int? FetchPageSize { get; }

    public bool MergeFetchedPageAsScan { get; }

    public OverlayUpdateResult WithPageFetch(
        int? pageToFetch,
        bool forcePageRefresh = false,
        int? fetchPageSize = null,
        bool mergeFetchedPageAsScan = false)
    {
        return new OverlayUpdateResult(
            ViewModel,
            pageToFetch,
            forcePageRefresh,
            Projection,
            fetchPageSize,
            mergeFetchedPageAsScan);
    }
}
