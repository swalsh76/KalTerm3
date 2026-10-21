using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using KalTerm.Client;
using KalTerm.Contracts;
using KalTerm.Terminal.Markets;
using KalTerm.Terminal.Services;
using KalTerm.Terminal.Views;

namespace KalTerm.Terminal.Tests;

public sealed class WatchlistServiceTests : IDisposable
{
    private readonly FakeApi _api = new();
    private readonly WatchlistService _service = new();
    private int _changes;

    public WatchlistServiceTests()
    {
        _api.Lists["macro"] = ["KX-A", "KX-B"];
        _api.Lists["sports"] = ["KX-B"];
        _service.Changed += () => _changes++;
        _service.Attach(_api);
    }

    public void Dispose() => _api.Dispose();

    [Theory]
    [InlineData("favorites", null)]
    [InlineData("a.b_c-d9", null)]
    [InlineData("0lists", null)]
    [InlineData("", "Type a name.")]
    [InlineData(null, "Type a name.")]
    [InlineData("Favorites", "Lower-case letters, digits, dots, dashes and underscores only, starting with a letter or digit.")]
    [InlineData("-lists", "Lower-case letters, digits, dots, dashes and underscores only, starting with a letter or digit.")]
    [InlineData("two words", "Lower-case letters, digits, dots, dashes and underscores only, starting with a letter or digit.")]
    public void NamesAreCheckedBeforeTheServerIsAsked(string? name, string? problem) => Assert.Equal(problem, WatchlistService.NameProblem(name));

    [Fact]
    public void ANameOfSixtyFiveCharactersIsTooLongAndSixtyFourIsNot()
    {
        Assert.Equal("At most 64 characters.", WatchlistService.NameProblem(new string('a', 65)));
        Assert.Null(WatchlistService.NameProblem(new string('a', 64)));
    }

    [Fact]
    public async Task LoadingReadsEveryListAndItsMarkets()
    {
        Assert.False(_service.Loaded);

        Assert.Null(await _service.LoadAsync());

        Assert.True(_service.Loaded);
        Assert.Equal([new WatchlistInfo("macro", 2), new WatchlistInfo("sports", 1)], _service.Lists);
        Assert.Equal(["macro", "sports"], _service.ListsOf("KX-B"));
        Assert.Equal(["macro"], _service.ListsOf("KX-A"));
        Assert.Empty(_service.ListsOf("KX-Z"));
        Assert.True(_service.IsMember("macro", "KX-A"));
        Assert.False(_service.IsMember("sports", "KX-A"));
        Assert.False(_service.IsMember("nope", "KX-A"));
        Assert.Equal(1, _changes);
        Assert.Equal(["list", "get macro", "get sports"], _api.ListCalls);
    }

    [Fact]
    public async Task LoadingWithoutAServerDoesNothingAndAFailureIsAnswerInWords()
    {
        var detached = new WatchlistService();
        Assert.Null(await detached.LoadAsync());
        Assert.False(detached.Loaded);

        _api.ListReadFails = new ApiUnavailableException("no route");
        Assert.Equal("Could not read your watchlists: no route", await _service.LoadAsync());
        Assert.False(_service.Loaded);

        _api.ListReadFails = Samples.Unauthorized();
        Assert.Equal("The server did not accept the token.", await _service.LoadAsync());
    }

    [Fact]
    public async Task ChangesNeedAServer()
    {
        var detached = new WatchlistService();

        Assert.Equal("Not connected.", await detached.CreateAsync("favorites"));
        Assert.Equal("Not connected.", await detached.DeleteAsync("macro"));
        Assert.Equal("Not connected.", await detached.SetMemberAsync("macro", "KX-A", member: true));
    }

    [Fact]
    public async Task AListIsMadeWhenTheServerAcceptsItAndNotBefore()
    {
        _ = await _service.LoadAsync();
        _changes = 0;
        _api.ListFails = new ApiException(HttpStatusCode.ServiceUnavailable, "unavailable");

        Assert.StartsWith("Could not make the watchlist:", await _service.CreateAsync("favorites"), StringComparison.Ordinal);
        Assert.DoesNotContain(_service.Lists, l => l.Name == "favorites");
        Assert.Equal(0, _changes);

        Assert.Null(await _service.CreateAsync("favorites"));
        Assert.Contains(new WatchlistInfo("favorites", 0), _service.Lists);
        Assert.Equal(1, _changes);
    }

    [Fact]
    public async Task ABadOrRepeatedNameNeverReachesTheServer()
    {
        _ = await _service.LoadAsync();
        _api.ListCalls.Clear();

        Assert.Equal("Type a name.", await _service.CreateAsync(""));
        Assert.Equal("You already have a watchlist called macro.", await _service.CreateAsync("macro"));

        Assert.Empty(_api.ListCalls);
    }

    [Fact]
    public async Task TheServersLimitsAndRefusalsAreAnsweredInPlainWords()
    {
        _ = await _service.LoadAsync();

        _api.ListFails = new ApiException(HttpStatusCode.Conflict, "limit_reached");
        Assert.Equal("You already have 20 watchlists. Delete one first.", await _service.CreateAsync("favorites"));

        _api.ListFails = new ApiException(HttpStatusCode.Conflict, "already_exists");
        Assert.Equal("You already have a watchlist called favorites.", await _service.CreateAsync("favorites"));

        _api.ListFails = new ApiException(HttpStatusCode.Conflict, "limit_reached");
        Assert.Equal("The watchlist macro is full (500 markets).", await _service.SetMemberAsync("macro", "KX-Z", member: true));

        _api.ListFails = new ApiException(HttpStatusCode.NotFound, "not_found");
        Assert.StartsWith("That watchlist no longer exists.", await _service.SetMemberAsync("macro", "KX-Z", member: true), StringComparison.Ordinal);

        _api.ListFails = new ApiException(HttpStatusCode.NotFound, "market_not_found");
        Assert.Equal("The server does not know that market.", await _service.SetMemberAsync("macro", "KX-Z", member: true));

        _api.ListFails = new ApiException(HttpStatusCode.Conflict, "limit_reached");
        Assert.StartsWith("Could not delete the watchlist:", await _service.DeleteAsync("macro"), StringComparison.Ordinal);

        _api.ListFails = Samples.Unauthorized();
        Assert.Equal("The server did not accept the token.", await _service.DeleteAsync("macro"));

        Assert.Equal(["macro", "sports"], _service.Lists.Select(l => l.Name));
    }

    [Fact]
    public async Task MarketsGoInAndOutOfAListAndTheMemoryFollowsTheServer()
    {
        _ = await _service.LoadAsync();
        _changes = 0;

        Assert.Null(await _service.SetMemberAsync("sports", "KX-A", member: true));
        Assert.Equal(["macro", "sports"], _service.ListsOf("KX-A"));
        Assert.Null(await _service.SetMemberAsync("macro", "KX-A", member: false));
        Assert.Equal(["sports"], _service.ListsOf("KX-A"));
        Assert.Null(await _service.DeleteAsync("macro"));
        Assert.Equal(["sports"], _service.Lists.Select(l => l.Name));

        Assert.Equal(3, _changes);
        Assert.Equal(["KX-B", "KX-A"], _api.Lists["sports"]);
    }

    [Fact]
    public async Task DetachingForgetsTheListsAndSaysSo()
    {
        _ = await _service.LoadAsync();
        _changes = 0;

        _service.Attach(null);

        Assert.Empty(_service.Lists);
        Assert.False(_service.Loaded);
        Assert.Equal(1, _changes);
        _service.Attach(null);   // nothing left to forget: silent
        Assert.Equal(1, _changes);
    }
}

public sealed class PromptViewModelTests
{
    [Fact]
    public void OkIsPossibleOnlyWhenTheTextWillDoAndTheProblemShowsOnlyOnceSomethingIsTyped()
    {
        var closed = 0;
        var prompt = new PromptViewModel("Title", "Message", WatchlistService.NameProblem);
        prompt.Closed += () => closed++;
        Assert.Equal(("Title", "Message"), (prompt.Title, prompt.Message));
        Assert.False(prompt.HasProblem);
        Assert.False(prompt.OkCommand.CanExecute(null));

        prompt.Text = "Bad Name";
        Assert.True(prompt.HasProblem);
        Assert.False(prompt.OkCommand.CanExecute(null));

        prompt.Text = "good-name";
        Assert.False(prompt.HasProblem);
        Assert.True(prompt.OkCommand.CanExecute(null));
        prompt.OkCommand.Execute(null);

        Assert.Equal(("good-name", 1), (prompt.Result, closed));
    }

    [Fact]
    public void CancellingLeavesNoResult()
    {
        var prompt = new PromptViewModel("Title", "Message", _ => null) { Text = "anything" };
        var closed = false;
        prompt.Closed += () => closed = true;

        prompt.CancelCommand.Execute(null);

        Assert.Null(prompt.Result);
        Assert.True(closed);
        Assert.Throws<ArgumentNullException>(() => new PromptViewModel("T", "M", null!));
    }
}

public sealed class WatchlistViewModelTests : IDisposable
{
    private readonly TestServices _t = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly Formatting _format = new(TimeZoneInfo.Utc);
    private readonly MarketsViewModel _model;

    public WatchlistViewModelTests()
    {
        _model = new MarketsViewModel(_format, _t.Time, InlineContext.Instance, _dialogs);
        _t.Api.ScreenerRows.AddRange([Samples.Row("KX-A"), Samples.Row("KX-B")]);
        _t.Api.Lists["macro"] = ["KX-A"];
        _t.Api.Lists["sports"] = ["KX-A", "KX-B"];
    }

    public void Dispose()
    {
        _model.Dispose();
        _t.Dispose();
    }

    private async Task OpenAsync()
    {
        _model.Attach(() => _t.Api);
        await _model.LoadAsync();
    }

    [Fact]
    public async Task TheListsAreReadWithTheFirstPageAndEachRowSaysWhichListsItIsIn()
    {
        await OpenAsync();

        Assert.Equal(["All markets", "macro (1)", "sports (2)"], _model.ListChoices.Select(c => c.Text));
        Assert.Equal(("All markets", null), (_model.ListFilter.Text, _model.ListFilter.Name));
        Assert.Equal(["macro, sports", "sports"], _model.Rows.Select(r => r.Lists));
    }

    [Fact]
    public async Task ChoosingAListAsksForItShowsEveryStatusAndGoingBackRestoresTheStatus()
    {
        await OpenAsync();
        _model.StatusFilter = _model.Statuses.Single(s => s.Value == "closed");
        await Wait.UntilAsync(() => _t.Api.ScreenQueries.Count == 2);

        _model.ListFilter = _model.ListChoices.Single(c => c.Name == "macro");
        await Wait.UntilAsync(() => _t.Api.ScreenQueries.Count == 3);
        Assert.Equal(("macro", null), (_t.Api.ScreenQueries[^1].Watchlist, _t.Api.ScreenQueries[^1].Status));
        Assert.Null(_model.StatusFilter.Value);

        _model.ListFilter = _model.ListChoices[0];
        await Wait.UntilAsync(() => _t.Api.ScreenQueries.Count == 4);
        Assert.Equal((null, "closed"), (_t.Api.ScreenQueries[^1].Watchlist, _t.Api.ScreenQueries[^1].Status));
        Assert.Equal("closed", _model.StatusFilter.Value);
    }

    [Fact]
    public async Task SwitchingFromOneListToAnotherKeepsTheStatusFromBeforeTheFirst()
    {
        await OpenAsync();

        _model.ListFilter = _model.ListChoices.Single(c => c.Name == "macro");
        _model.ListFilter = _model.ListChoices.Single(c => c.Name == "sports");
        _model.ListFilter = _model.ListChoices[0];
        await Wait.UntilAsync(() => _t.Api.ScreenQueries.Count >= 4);

        Assert.Equal("active", _model.StatusFilter.Value);
        _model.ListFilter = null!;   // a box whose items were just replaced may offer nothing: ignored
        Assert.Equal("All markets", _model.ListFilter.Text);
    }

    [Fact]
    public async Task TheMenuForTheSelectedMarketChecksItsListsAndChoosingALineToggles()
    {
        await OpenAsync();
        Assert.Empty(_model.Menu.Choices);

        _model.Selected = _model.Rows[1];

        Assert.Equal([("macro", false), ("sports", true), ("New watchlist…", false)], _model.Menu.Choices.Select(c => (c.Text, c.IsMember)));
        Assert.Equal([true, true, false], _model.Menu.Choices.Select(c => c.IsCheckable));

        await _model.Menu.Choices[0].Command.RunAsync();   // put KX-B in macro
        Assert.Equal([true, true], _model.Menu.Choices.Take(2).Select(c => c.IsMember));
        Assert.Equal("macro, sports", _model.Rows[1].Lists);
        Assert.Equal("macro (2)", _model.ListChoices[1].Text);

        await _model.Menu.Choices[1].Command.RunAsync();   // take it out of sports
        Assert.Equal([true, false], _model.Menu.Choices.Take(2).Select(c => c.IsMember));
        Assert.Equal("macro", _model.Rows[1].Lists);
        Assert.Equal(["add macro KX-B", "remove sports KX-B"], _t.Api.ListCalls.Skip(3));

        _model.Selected = null;
        Assert.Empty(_model.Menu.Choices);
    }

    [Fact]
    public async Task AFailedToggleIsShownOnThePageAndChangesNothing()
    {
        await OpenAsync();
        _model.Selected = _model.Rows[1];
        _t.Api.ListFails = new ApiException(HttpStatusCode.Conflict, "limit_reached");

        await _model.Menu.Choices[0].Command.RunAsync();

        Assert.Equal("The watchlist macro is full (500 markets).", _model.Error);
        Assert.False(_model.Menu.Choices[0].IsMember);
        Assert.Equal("sports", _model.Rows[1].Lists);
    }

    [Fact]
    public async Task TakingAMarketOutOfTheListOnShowRereadsThePageSoItDisappears()
    {
        await OpenAsync();
        _model.ListFilter = _model.ListChoices.Single(c => c.Name == "sports");
        await Wait.UntilAsync(() => _t.Api.ScreenQueries.Count == 2);
        _model.Selected = _model.Rows[1];
        var asked = _t.Api.ScreenQueries.Count;
        _t.Api.ScreenerAnswer = _ => new ScreenerPage("volume", "desc", null, null, [Samples.Row("KX-A")], null);

        await _model.Menu.Choices[1].Command.RunAsync();   // sports: take KX-B out
        await Wait.UntilAsync(() => _model.Rows.Count == 1);

        Assert.Equal(asked + 1, _t.Api.ScreenQueries.Count);
        Assert.Equal("KX-A", _model.Rows[0].Ticker);
    }

    [Fact]
    public async Task TakingAMarketIntoAnotherListDoesNotRereadThePage()
    {
        await OpenAsync();
        _model.ListFilter = _model.ListChoices.Single(c => c.Name == "sports");
        await Wait.UntilAsync(() => _t.Api.ScreenQueries.Count == 2);
        _model.Selected = _model.Rows[1];

        await _model.Menu.Choices[0].Command.RunAsync();   // macro: put KX-B in
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal(2, _t.Api.ScreenQueries.Count);
    }

    [Fact]
    public async Task DeletingTheListShownAsksFirstThenGoesBackToEveryMarket()
    {
        await OpenAsync();
        _model.ListFilter = _model.ListChoices.Single(c => c.Name == "macro");
        Assert.True(_model.DeleteListCommand.CanExecute(null));
        _dialogs.Confirm = false;

        await _model.DeleteListCommand.RunAsync();
        Assert.Contains("macro", _model.ListChoices.Select(c => c.Name));
        Assert.Contains("Delete the watchlist macro?", _dialogs.Asked.Single(), StringComparison.Ordinal);

        _dialogs.Confirm = true;
        await _model.DeleteListCommand.RunAsync();

        Assert.DoesNotContain("macro", _model.ListChoices.Select(c => c.Name));
        Assert.Null(_model.ListFilter.Name);
        Assert.Equal("active", _model.StatusFilter.Value);
        Assert.False(_model.DeleteListCommand.CanExecute(null));
        Assert.False(_t.Api.Lists.ContainsKey("macro"));
    }

    [Fact]
    public async Task DeletingFailsInWordsAndDoingNothingWhenNoListIsChosen()
    {
        await OpenAsync();
        await _model.DeleteListCommand.RunAsync();
        Assert.Empty(_dialogs.Asked);

        _model.ListFilter = _model.ListChoices.Single(c => c.Name == "macro");
        _t.Api.ListFails = new ApiUnavailableException("down");
        await _model.DeleteListCommand.RunAsync();

        Assert.Equal("Could not delete the watchlist: down", _model.Error);
        Assert.Contains("macro", _model.ListChoices.Select(c => c.Name));
    }

    [Fact]
    public async Task ANewListIsMadeFromTheNameTypedAndKeepsTheOrderByName()
    {
        await OpenAsync();
        _dialogs.Name = "favorites";

        await _model.NewListCommand.RunAsync();

        Assert.Equal(["All markets", "favorites (0)", "macro (1)", "sports (2)"], _model.ListChoices.Select(c => c.Text));
        Assert.Equal("New watchlist: A name for the new watchlist.", _dialogs.Asked.Single());
        Assert.Null(_dialogs.NameCheck);
        Assert.Null(_model.ListFilter.Name);   // making a list does not switch to it

        _dialogs.Name = "zeta";
        await _model.NewListCommand.RunAsync();
        Assert.Equal("zeta (0)", _model.ListChoices[^1].Text);
    }

    [Fact]
    public async Task GivingUpOnTheNameMakesNothingAndABadOrRepeatedNameIsSaidInWords()
    {
        await OpenAsync();
        _dialogs.Name = null;
        await _model.NewListCommand.RunAsync();
        Assert.DoesNotContain(_t.Api.ListCalls, c => c.StartsWith("create", StringComparison.Ordinal));

        _dialogs.Name = "macro";
        await _model.NewListCommand.RunAsync();
        Assert.Equal("You already have a watchlist called macro.", _model.Error);

        _dialogs.Name = "Bad";
        await _model.NewListCommand.RunAsync();
        Assert.StartsWith("Lower-case letters", _model.Error, StringComparison.Ordinal);
        Assert.NotNull(_dialogs.NameCheck);
    }

    [Fact]
    public async Task RereadingTheListsPicksUpChangesMadeElsewhere()
    {
        await OpenAsync();
        _t.Api.Lists["fresh"] = ["KX-B"];
        _ = _t.Api.Lists.Remove("macro");

        await _model.RefreshListsCommand.RunAsync();

        Assert.Equal(["All markets", "fresh (1)", "sports (2)"], _model.ListChoices.Select(c => c.Text));
        Assert.Equal(["sports", "fresh, sports"], _model.Rows.Select(r => r.Lists));
    }

    [Fact]
    public async Task RereadingWhileTheListWasDeletedElsewhereGoesBackToEveryMarket()
    {
        await OpenAsync();
        _model.ListFilter = _model.ListChoices.Single(c => c.Name == "macro");
        _ = _t.Api.Lists.Remove("macro");

        await _model.RefreshListsCommand.RunAsync();

        Assert.Null(_model.ListFilter.Name);
    }

    [Fact]
    public async Task AFailureToReadTheListsIsShownAndThePageStillLoads()
    {
        _t.Api.ListReadFails = new ApiUnavailableException("no route");

        await OpenAsync();

        Assert.Equal(2, _model.Rows.Count);
        Assert.Equal("Could not read your watchlists: no route", _model.Error);
        Assert.Equal(["All markets"], _model.ListChoices.Select(c => c.Text));
    }

    [Fact]
    public async Task TheListCommandsNeedAServerAndAWindowToAsk()
    {
        Assert.False(_model.NewListCommand.CanExecute(null));
        Assert.False(_model.RefreshListsCommand.CanExecute(null));

        await OpenAsync();
        Assert.True(_model.NewListCommand.CanExecute(null));
        Assert.True(_model.RefreshListsCommand.CanExecute(null));

        var plain = new MarketsViewModel(_format, _t.Time, InlineContext.Instance);
        plain.Attach(() => _t.Api);
        Assert.False(plain.NewListCommand.CanExecute(null));
        Assert.False(plain.DeleteListCommand.CanExecute(null));
        await plain.NewListCommand.RunAsync();
        await plain.DeleteListCommand.RunAsync();
        plain.Dispose();
    }

    [Fact]
    public async Task DetachingResetsTheListAndTheStatusItHadChanged()
    {
        await OpenAsync();
        _model.ListFilter = _model.ListChoices.Single(c => c.Name == "macro");
        await Wait.UntilAsync(() => _t.Api.ScreenQueries.Count == 2);

        _model.Detach();

        Assert.Null(_model.ListFilter.Name);
        Assert.Equal("active", _model.StatusFilter.Value);
        Assert.Equal(["All markets"], _model.ListChoices.Select(c => c.Text));
        Assert.False(_model.RefreshListsCommand.CanExecute(null));
    }

    [Fact]
    public async Task TheOpenMarketHasItsOwnMenuAndMakingAListFromItPutsTheMarketIn()
    {
        await OpenAsync();
        await _model.OpenAsync(_model.Rows[1]);
        var menu = _model.Detail!.Watchlists!;
        Assert.True(_model.Detail.HasWatchlists);
        Assert.Equal([("macro", false), ("sports", true), ("New watchlist…", false)], menu.Choices.Select(c => (c.Text, c.IsMember)));

        _dialogs.Name = "favorites";
        await menu.NewCommand.RunAsync();

        Assert.Equal(["KX-B"], _t.Api.Lists["favorites"]);
        Assert.Equal("favorites, sports", _model.Rows[1].Lists);
        Assert.Equal("New watchlist: A name for the watchlist that will hold KX-B.", _dialogs.Asked.Single());
        Assert.Contains(menu.Choices, c => c is { Text: "favorites", IsMember: true });
    }

    [Fact]
    public async Task TheDetailMenuReportsAFailedNewListAndGivingUpMakesNothing()
    {
        await OpenAsync();
        await _model.OpenAsync(_model.Rows[0]);
        var menu = _model.Detail!.Watchlists!;

        _dialogs.Name = null;
        await menu.NewCommand.RunAsync();
        Assert.DoesNotContain(_t.Api.ListCalls, c => c.StartsWith("create", StringComparison.Ordinal));

        _dialogs.Name = "macro";
        await menu.NewCommand.RunAsync();
        Assert.Equal("You already have a watchlist called macro.", _model.Error);
    }

    [Fact]
    public async Task ClosingTheDetailLetsGoOfItsMenu()
    {
        await OpenAsync();
        await _model.OpenAsync(_model.Rows[0]);
        var menu = _model.Detail!.Watchlists!;

        _model.Detail.BackCommand.Execute(null);
        _t.Api.Lists["late"] = [];
        await _model.RefreshListsCommand.RunAsync();

        Assert.DoesNotContain(menu.Choices, c => c.Text == "late");
    }

    [Fact]
    public void ADetailOpenedWithoutAMenuHasNoButtonAndAMenuNeedsItsPieces()
    {
        using var feed = new MarketFeed(_t.Api, _t.Time, InlineContext.Instance);
        using var detail = new MarketDetailViewModel("KX-A", _t.Api, feed, _format, _t.Time);

        Assert.False(detail.HasWatchlists);
        Assert.Throws<ArgumentNullException>(() => new WatchlistMenu(null!, null, _ => { }));
        Assert.Throws<ArgumentNullException>(() => new WatchlistMenu(new WatchlistService(), null, null!));
        var menu = new WatchlistMenu(new WatchlistService(), null, _ => { }) { Ticker = "KX-A" };
        Assert.False(menu.NewCommand.CanExecute(null));
        menu.NewCommand.Execute(null);
        menu.Dispose();
    }

    /// <summary>A page made and loaded on the calling (STA) thread, as the views need: their collections may only change on the thread that shows them.</summary>
    private MarketsViewModel Page()
    {
        var model = new MarketsViewModel(_format, _t.Time, InlineContext.Instance, _dialogs);
        model.Attach(() => _t.Api);
        model.LoadAsync().GetAwaiter().GetResult();
        return model;
    }

    private static List<(string Text, bool Checkable, bool Checked)> Lines(ContextMenu menu)
    {
        menu.ApplyTemplate();
        menu.UpdateLayout();
        return [.. menu.Items.Cast<WatchlistChoice>().Select(c => (c.Text, c.IsCheckable, c.IsMember))];
    }

    [Fact]
    public void TheSharedItemStyleReadsEveryPartOfAChoice()
    {
        var bound = Sta.Run(() =>
        {
            var view = new MarketsView();
            var style = (Style)view.FindResource("WatchlistItem");
            return style.Setters.Cast<Setter>().ToDictionary(x => x.Property.Name, x => ((System.Windows.Data.Binding)x.Value).Path.Path);
        });

        Assert.Equal(new Dictionary<string, string> { ["Header"] = "Text", ["IsCheckable"] = "IsCheckable", ["IsChecked"] = "IsMember", ["Command"] = "Command" }, bound);
        Assert.All(bound.Values, path => Assert.NotNull(typeof(WatchlistChoice).GetProperty(path)));
    }

    [Fact]
    public void TheListBoxTheListsColumnAndTheGridMenuShowOnThePageWithoutBindingErrors()
    {
        var (texts, lines, selected) = Sta.Run(() =>
        {
            using var errors = BindingErrors.Listen();
            using var model = Page();
            var view = new MarketsView { DataContext = model };
            VisualTreeKit.Realize(view);
            var found = VisualTreeKit.Texts(view).ToList();

            var grid = VisualTreeKit.All<DataGrid>(view).Single();
            var row = VisualTreeKit.All<DataGridRow>(view).Last();
            var target = VisualTreeKit.All<TextBlock>(row).First();
            grid.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right) { RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent, Source = target });
            grid.ContextMenu!.DataContext = model;   // a menu takes the data of the control it opens over; here there is no mouse to open it
            var menuLines = Lines(grid.ContextMenu);
            Assert.Empty(errors.Messages);
            return (found, menuLines, model.Selected?.Ticker);
        });

        Assert.Contains("List", texts);
        Assert.Contains("Lists", texts);
        Assert.Contains("macro, sports", texts);
        Assert.Equal("KX-B", selected);
        Assert.Equal([("macro", true, false), ("sports", true, true), ("New watchlist…", false, false)], lines);
    }

    [Fact]
    public void ARightClickAwayFromARowSelectsNothing()
    {
        var selected = Sta.Run(() =>
        {
            using var model = Page();
            var view = new MarketsView { DataContext = model };
            VisualTreeKit.Realize(view);
            var grid = VisualTreeKit.All<DataGrid>(view).Single();
            grid.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right) { RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent, Source = new Run("x") });
            grid.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right) { RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent, Source = view });
            return model.Selected;
        });

        Assert.Null(selected);
    }

    [Fact]
    public void TheDetailShowsAWatchlistsButtonThatOpensItsMenu()
    {
        var (visibility, lines, open) = Sta.Run(() =>
        {
            using var errors = BindingErrors.Listen();
            using var model = Page();
            model.OpenAsync(model.Rows[1]).GetAwaiter().GetResult();
            var view = new MarketDetailView { DataContext = model.Detail };
            VisualTreeKit.Realize(view);
            var watchlists = VisualTreeKit.All<Button>(view).Single(b => (b.Content as string) == "Watchlists ▾");
            watchlists.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var isOpen = watchlists.ContextMenu!.IsOpen;
            var menuLines = Lines(watchlists.ContextMenu);
            watchlists.ContextMenu.IsOpen = false;
            Assert.Empty(errors.Messages);
            return (watchlists.Visibility, menuLines, isOpen);
        });

        Assert.Equal(Visibility.Visible, visibility);
        Assert.Equal([("macro", true, false), ("sports", true, true), ("New watchlist…", false, false)], lines);
        Assert.True(open);
    }

    [Fact]
    public void ADetailWithoutAMenuHidesTheButton()
    {
        var visibility = Sta.Run(() =>
        {
            using var feed = new MarketFeed(_t.Api, _t.Time, InlineContext.Instance);
            using var detail = new MarketDetailViewModel("KX-A", _t.Api, feed, _format, _t.Time);
            var view = new MarketDetailView { DataContext = detail };
            VisualTreeKit.Realize(view);
            return VisualTreeKit.All<Button>(view).Single(b => (b.Content as string) == "Watchlists ▾").Visibility;
        });

        Assert.Equal(Visibility.Collapsed, visibility);
    }
}
