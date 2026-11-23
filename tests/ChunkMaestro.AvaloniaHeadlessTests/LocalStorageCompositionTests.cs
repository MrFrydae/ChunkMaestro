using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ChunkMaestro.Application;
using ChunkMaestro.Desktop;
using ChunkMaestro.Domain;
using ChunkMaestro.Persistence;
using ChunkMaestro.Presentation;
using Microsoft.Data.Sqlite;
using System.ComponentModel;
using System.Reflection;
using Xunit;

namespace ChunkMaestro.AvaloniaHeadlessTests;

// Real local-service composition on supported desktop OSes. Headless interaction is not native acceptance.
public sealed class LocalStorageCompositionTests
{
    [AvaloniaFact]
    public async Task WindowConsentAndRegistrationPersistIdentitiesAndSeparateTransports()
    {
        using SecureStoreFixture fixture = new();
        int opens = 0;
        FleetViewModel model = new(async confirmed =>
        {
            Assert.True(confirmed);
            opens++;
            return await fixture.OpenAsync();
        }, fixture.DirectoryPath);
        FleetInventory original;
        await using (model)
        {
            MainWindow window = new()
            {
                DataContext = model
            };
            window.Show();
            try
            {
                Assert.False(window.FindControl<Button>("OpenStorage")!.IsEnabled);
                Assert.Equal(fixture.DirectoryPath, model.StoragePath);
                Assert.Contains(fixture.DirectoryPath, window.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text));
                await model.OpenAsync();
                Assert.Equal(0, opens);
                Assert.False(Directory.Exists(fixture.DirectoryPath));
                window.FindControl<CheckBox>("StorageConsent")!.IsChecked = true;
                Assert.True(model.LocalStorageConfirmed);
                await ClickAsync(model, window.FindControl<Button>("OpenStorage")!);
                Assert.Equal(1, opens);
                Assert.True(model.IsEmpty);
                Assert.True(window.FindControl<TextBlock>("EmptyState")!.IsVisible);
                window.FindControl<TextBox>("FleetNameInput")!.Text = "Home";
                await ClickAsync(model, RegistrationButton(window, "Register _fleet"));
                window.FindControl<ComboBox>("FleetSelector")!.SelectedItem = model.Fleets.Single();
                window.FindControl<TextBox>("HostNameInput")!.Text = "Local fixture host";
                window.FindControl<TextBox>("HostAddressInput")!.Text = "example.invalid";
                await ClickAsync(model, RegistrationButton(window, "Register _host"));
                window.FindControl<ComboBox>("HostSelector")!.SelectedItem = model.Hosts.Single();
                window.FindControl<TextBox>("InstanceNameInput")!.Text = "Survival";
                window.FindControl<TextBox>("JavaPortInput")!.Text = "25565";
                window.FindControl<TextBox>("BedrockPortInput")!.Text = "25565";
                await ClickAsync(model, RegistrationButton(window, "Register _instance"));
                Assert.Equal("", model.Error);
                Assert.False(window.FindControl<TextBlock>("EmptyState")!.IsVisible);
                Assert.False(window.FindControl<Button>("RemoteOperations")!.IsEnabled);
                Assert.Contains(model.Rows, row => row.Contains("Java TCP", StringComparison.Ordinal));
                Assert.Contains(model.Rows, row => row.Contains("Bedrock UDP", StringComparison.Ordinal));
                Assert.Contains(model.Rows, row => row.Contains("Last contact: unavailable", StringComparison.Ordinal));
                Assert.Contains(model.Rows, row => row.Contains("Health: unknown (not probed)", StringComparison.Ordinal));
                Assert.False(model.CanOperate);
                original = await fixture.Store!.ReadAsync(TestContext.Current.CancellationToken);
                AssertInventory(original);
            }
            finally
            {
                window.Close();
            }
        }
        await using FleetViewModel reopened = fixture.CreateModel();
        await reopened.OpenAsync();
        Assert.Equal("", reopened.Error);
        FleetInventory persisted = await fixture.Store!.ReadAsync(TestContext.Current.CancellationToken);
        AssertInventory(persisted);
        Assert.Equal(original.Fleets, persisted.Fleets);
        Assert.Equal(original.Hosts, persisted.Hosts);
        Instance before = original.Instances.Single();
        Instance after = persisted.Instances.Single();
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(before.HostId, after.HostId);
        Assert.Equal(before.RegisteredAt, after.RegisteredAt);
        Assert.Equal(before.Endpoints, after.Endpoints);
        Assert.False(reopened.CanOperate);
    }

    [Fact]
    public async Task CompetingControllerIsRefusedAndRetryRetainsExistingIdentityWithoutReset()
    {
        using SecureStoreFixture fixture = new();
        FleetViewModel first = fixture.CreateModel();
        await using FleetViewModel second = fixture.CreateModel();
        FleetId identity;
        await using (first)
        {
            await first.OpenAsync();
            first.FleetName = "Preserved fleet";
            await first.RegisterFleetAsync();
            identity = first.Fleets.Single().Id;
            await second.OpenAsync();
            Assert.Contains("Another desktop session", second.Error, StringComparison.Ordinal);
            Assert.DoesNotContain(fixture.DirectoryPath, second.Error, StringComparison.Ordinal);
            Assert.True(second.CanOpen);
            Assert.False(second.CanRegister);
            Assert.False(second.CanOperate);
            Assert.Empty(second.Rows);
            Assert.Equal(identity, (await fixture.Store!.ReadAsync(TestContext.Current.CancellationToken)).Fleets.Single().Id);
        }
        await second.OpenAsync();
        Assert.Equal("", second.Error);
        Assert.Equal(identity, second.Fleets.Single().Id);
        Assert.True(second.CanRegister);
        Assert.False(second.CanOperate);
    }

    [Fact]
    public async Task DuplicateServiceWritePreservesRowsAndDoesNotPartiallyPersistEndpoints()
    {
        using SecureStoreFixture fixture = new();
        await using FleetViewModel model = fixture.CreateModel();
        await RegisterHierarchyAsync(model);
        FleetInventory before = await fixture.Store!.ReadAsync(TestContext.Current.CancellationToken);
        string[] rows = model.Rows.ToArray();
        model.InstanceName = "Rejected duplicate service";
        model.JavaPort = "25566";
        model.BedrockPort = "19133";
        await model.RegisterInstanceAsync();
        Assert.NotEmpty(model.Error);
        Assert.DoesNotContain(fixture.DirectoryPath, model.Error, StringComparison.Ordinal);
        Assert.Equal(rows, model.Rows);
        FleetInventory after = await fixture.Store.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(before.Instances.Single().Id, Assert.Single(after.Instances).Id);
        Assert.Equal(before.Instances.Single().Endpoints, after.Instances.Single().Endpoints);
        await model.ReloadAsync();
        Assert.Equal("", model.Error);
        Assert.Equal(rows, model.Rows);
        Assert.True(model.CanRegister);
        Assert.False(model.CanOperate);
    }

    [AvaloniaFact]
    public async Task RealSqliteReadFailureRetainsDisplayAndBlocksWritesUntilExplicitReload()
    {
        using SecureStoreFixture fixture = new();
        await using FleetViewModel model = fixture.CreateModel();
        await RegisterHierarchyAsync(model);
        string[] rows = model.Rows.ToArray();
        FleetId identity = model.Fleets.Single().Id;
        MainWindow window = new()
        {
            DataContext = model
        };
        window.Show();
        try
        {
            // Commit a reversible schema fault in this disposable store: no injected service exception.
            await fixture.ExecuteAsync("ALTER TABLE Fleets RENAME TO HeldFleets");
            try
            {
                await ClickAsync(model, window.FindControl<Button>("Reload")!);
                Assert.True(model.HasReadFailure);
                Assert.Equal(rows, model.Rows);
                Assert.Equal(identity, model.Fleets.Single().Id);
                Assert.Contains("stale", model.Status, StringComparison.Ordinal);
                Assert.False(model.CanRegister);
                Assert.False(RegistrationButton(window, "Register _fleet").IsEffectivelyEnabled);
                Assert.True(model.CanReload);
                Assert.False(model.CanOperate);
                Assert.False(string.IsNullOrEmpty(window.FindControl<TextBlock>("Error")!.Text));
                Assert.DoesNotContain(fixture.DirectoryPath, model.Error, StringComparison.Ordinal);
                Assert.DoesNotContain("HeldFleets", model.Error, StringComparison.Ordinal);
                model.FleetName = "Must not be queued";
                await model.RegisterFleetAsync();
                Assert.Equal(1L, await fixture.FleetCountAsync());
            }
            finally
            {
                await fixture.ExecuteAsync("ALTER TABLE HeldFleets RENAME TO Fleets");
            }
            await ClickAsync(model, window.FindControl<Button>("Reload")!);
            Assert.False(model.HasReadFailure);
            Assert.Equal("", model.Error);
            Assert.True(model.CanRegister);
            Assert.Equal(rows, model.Rows);
            Assert.Equal(identity, (await fixture.Store!.ReadAsync(TestContext.Current.CancellationToken)).Fleets.Single().Id);
        }
        finally
        {
            window.Close();
        }
    }

    private static async Task RegisterHierarchyAsync(FleetViewModel model)
    {
        await model.OpenAsync();
        model.FleetName = "Home";
        await model.RegisterFleetAsync();
        model.SelectedFleet = model.Fleets.Single();
        model.HostName = "Fixture host";
        model.HostAddress = "example.invalid";
        await model.RegisterHostAsync();
        model.SelectedHost = model.Hosts.Single();
        model.InstanceName = "Survival";
        model.BedrockPort = "25565";
        await model.RegisterInstanceAsync();
        Assert.Equal("", model.Error);
    }

    private static void AssertInventory(FleetInventory snapshot)
    {
        Fleet fleet = Assert.Single(snapshot.Fleets);
        Host host = Assert.Single(snapshot.Hosts);
        Instance instance = Assert.Single(snapshot.Instances);
        Assert.Equal("Home", fleet.Name);
        Assert.Equal(fleet.Id, host.FleetId);
        Assert.Equal(host.Id, instance.HostId);
        Assert.Equal("minecraft.service", instance.ServiceName);
        Assert.Equal(Observation.Unknown, host.Observation);
        Assert.Equal(Observation.Unknown, instance.Observation);
        Assert.Collection(instance.Endpoints,
            java =>
            {
                Assert.Equal(EndpointEdition.Java, java.Edition);
                Assert.Equal(EndpointTransport.Tcp, java.Transport);
                Assert.Equal(25565, java.Port);
                Assert.Equal("example.invalid", java.Address);
                Assert.Equal(Observation.Unknown, java.Health);
            },
            bedrock =>
            {
                Assert.Equal(EndpointEdition.Bedrock, bedrock.Edition);
                Assert.Equal(EndpointTransport.Udp, bedrock.Transport);
                Assert.Equal(25565, bedrock.Port);
                Assert.Equal("example.invalid", bedrock.Address);
                Assert.Equal(Observation.Unknown, bedrock.Health);
            });
    }

    private static Button RegistrationButton(MainWindow window, string content)
    {
        return window.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, content));
    }

    private static async Task ClickAsync(FleetViewModel model, Button button)
    {
        Assert.True(button.IsEffectivelyEnabled);
        TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(FleetViewModel.CanReload) && (model.CanReload || model.CanOpen))
            {
                completed.TrySetResult();
            }
        }
        model.PropertyChanged += Changed;
        try
        {
            // Executes the XAML-wired handler; this does not simulate a native pointer or keyboard.
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        }
        finally
        {
            model.PropertyChanged -= Changed;
        }
    }

    private sealed class SecureStoreFixture : IDisposable
    {
        public string DirectoryPath
        {
            get;
        } = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(),
            "chunkmaestro-composition-" + Guid.NewGuid().ToString("N"));
        public LocalFleetStore? Store
        {
            get; private set;
        }

        public FleetViewModel CreateModel()
        {
            return new FleetViewModel(async confirmed =>
            {
                Assert.True(confirmed);
                return await OpenAsync();
            }, DirectoryPath)
            {
                LocalStorageConfirmed = true
            };
        }

        public async Task<LocalFleetStore> OpenAsync()
        {
            // Only location selection uses the existing internal test seam. Prepare/security, locks,
            // migrations, reads, writes and disposal execute unchanged through public store APIs.
            MethodInfo factory = typeof(StorageLocation).GetMethod("ForTests", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException("Disposable storage location seam unavailable.");
            StorageLocation location = (StorageLocation) factory.Invoke(null, [DirectoryPath])!;
            LocalFleetStore opened = await LocalFleetStore.OpenAsync(location, TestContext.Current.CancellationToken);
            Store = opened;
            return opened;
        }

        public async Task ExecuteAsync(string sql)
        {
            await using SqliteConnection connection = new(ConnectionString());
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        public async Task<long> FleetCountAsync()
        {
            await using SqliteConnection connection = new(ConnectionString());
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM HeldFleets";
            return (long) (await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        }

        private string ConnectionString()
        {
            return new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(DirectoryPath, "fleet.db"),
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false
            }.ToString();
        }

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, true);
            }
        }
    }
}
