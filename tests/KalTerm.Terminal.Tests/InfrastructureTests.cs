using System.ComponentModel;
using KalTerm.Client;
using KalTerm.Terminal.Mvvm;
using KalTerm.Terminal.Services;

namespace KalTerm.Terminal.Tests;

public class ObservableObjectTests
{
    private sealed class Sample : ObservableObject
    {
        private int _value;

        public int Value
        {
            get => _value;
            set => Set(ref _value, value);
        }

        public void Announce() => Raise(nameof(Value));
    }

    [Fact]
    public void ASetRaisesOnlyWhenTheValueChanges()
    {
        var sample = new Sample();
        var raised = new List<string?>();
        sample.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        sample.Value = 5;
        sample.Value = 5;
        sample.Announce();

        Assert.Equal(["Value", "Value"], raised);
        Assert.Equal(5, sample.Value);
    }

    [Fact]
    public void ItIsWhatTheViewsListenTo()
    {
        Assert.IsAssignableFrom<INotifyPropertyChanged>(new Sample());
    }
}

public class CommandTests
{
    [Fact]
    public void ARelayCommandRunsAndCanBeSwitchedOff()
    {
        var runs = 0;
        var allowed = true;
        var command = new RelayCommand(() => runs++, () => allowed);
        var changed = 0;
        command.CanExecuteChanged += (_, _) => changed++;

        command.Execute(null);
        allowed = false;
        command.RaiseCanExecuteChanged();

        Assert.Equal(1, runs);
        Assert.False(command.CanExecute(null));
        Assert.Equal(1, changed);
        Assert.True(new RelayCommand(() => { }).CanExecute(null));
    }

    [Fact]
    public async Task AnAsyncCommandCannotRunTwiceAtOnceAndTellsTheViewWhenItChanges()
    {
        var gate = new TaskCompletionSource();
        var runs = 0;
        var command = new AsyncCommand(async () =>
        {
            runs++;
            await gate.Task;
        }, _ => { });
        var changes = 0;
        command.CanExecuteChanged += (_, _) => changes++;

        var first = command.RunAsync();
        Assert.False(command.CanExecute(null));
        await command.RunAsync();   // ignored while running
        gate.SetResult();
        await first;

        Assert.Equal(1, runs);
        Assert.True(command.CanExecute(null));
        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task AFailureGoesToTheHandlerAndACancellationIsNotAFailure()
    {
        var errors = new List<string>();
        var failing = new AsyncCommand(() => throw new InvalidOperationException("boom"), ex => errors.Add(ex.Message));
        var cancelled = new AsyncCommand(() => throw new OperationCanceledException(), ex => errors.Add(ex.Message));

        await failing.RunAsync();
        await cancelled.RunAsync();

        Assert.Equal(["boom"], errors);
        Assert.True(failing.CanExecute(null));
    }

    [Fact]
    public async Task ACommandThatMayNotRunDoesNotRun()
    {
        var runs = 0;
        var command = new AsyncCommand(() =>
        {
            runs++;
            return Task.CompletedTask;
        }, _ => { }, () => false);

        await command.RunAsync();
        command.Execute(null);

        Assert.Equal(0, runs);
    }

    [Fact]
    public async Task TheViewsEntryPointRunsTheCommand()
    {
        var done = new TaskCompletionSource();
        var command = new AsyncCommand(() =>
        {
            done.SetResult();
            return Task.CompletedTask;
        }, _ => { });

        command.Execute(null);

        await done.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }
}

public sealed class TerminalSettingsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "kalterm-settings-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private string File_ => Path.Combine(_folder, "terminal.json");

    [Theory]
    [InlineData("http://server.example:5280")]
    [InlineData("http://192.0.2.10:5280")]
    public void AServerThatWouldGetTheTokenOverPlainHttpIsNotRememberedAsTheOneToUse(string address)
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(File_, $$"""{ "server": "{{address}}" }""");

        Assert.Null(new TerminalSettingsStore(File_).LoadServer());
    }

    [Fact]
    public void ALoopbackHttpServerOrAnHttpsOneIsStillRemembered()
    {
        var store = new TerminalSettingsStore(File_);

        store.SaveServer(new Uri("http://127.0.0.1:5280"));
        Assert.Equal(new Uri("http://127.0.0.1:5280"), store.LoadServer());
        store.SaveServer(new Uri("https://server.example:5280"));
        Assert.Equal(new Uri("https://server.example:5280"), store.LoadServer());
    }

    [Fact]
    public void TheServerUsedLastIsRememberedWithoutAnyPath()
    {
        var store = new TerminalSettingsStore(File_);
        Assert.Null(store.LoadServer());

        store.SaveServer(new Uri("https://kaldb:5280/v1/status?x=1"));

        Assert.Equal(new Uri("https://kaldb:5280"), store.LoadServer());
        Assert.Equal(["terminal.json"], Directory.GetFiles(_folder).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"server\":\"\"}")]
    [InlineData("{\"server\":\"not a uri\"}")]
    [InlineData("{\"server\":\"ftp://kaldb:5280\"}")]
    public void AFileThatCannotBeUsedIsForgottenNotFatal(string content)
    {
        Directory.CreateDirectory(_folder);
        System.IO.File.WriteAllText(File_, content);

        Assert.Null(new TerminalSettingsStore(File_).LoadServer());
    }

    [Fact]
    public void TheDefaultPathIsNextToTheOtherKalTermFiles()
    {
        Assert.Equal(Path.GetDirectoryName(PinStore.DefaultPath()), Path.GetDirectoryName(TerminalSettingsStore.DefaultPath()));
        Assert.EndsWith("terminal.json", TerminalSettingsStore.DefaultPath(), StringComparison.Ordinal);
    }
}

public class RealServicesTests
{
    [Fact]
    public void TheRealServicesUseTheSameStoresAsTheCli()
    {
        var real = ClientServices.Real();

        Assert.NotNull(real.Pins);
        Assert.NotNull(real.Tokens);
        Assert.Equal(TimeProvider.System, real.Time);
        Assert.Equal(TimeZoneInfo.Local, real.SystemZone);
        using var api = (IDisposable)real.CreateApi(new Uri("http://127.0.0.1:5280"));
        using var withToken = (IDisposable)real.CreateApiWithToken(new Uri("http://127.0.0.1:5280"), "kt_x");
    }
}
