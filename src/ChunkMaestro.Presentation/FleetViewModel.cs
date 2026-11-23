using ChunkMaestro.Application;
using ChunkMaestro.Domain;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace ChunkMaestro.Presentation;

public sealed class FleetViewModel(Func<bool, Task<IFleetInventory>> open, string? storagePath = null) : INotifyPropertyChanged, IAsyncDisposable
{
    private IFleetInventory? inventory;
    private bool busy;
    private bool closing;
    private bool writeNeedsReload;
    private Task? disposal;
    private TaskCompletionSource? idle;
    private bool confirmed;
    private string status = "Storage closed. Confirm local storage to begin.";
    private string error = "";
    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<Fleet> Fleets
    {
        get;
    } = [];
    public ObservableCollection<Host> Hosts
    {
        get;
    } = [];
    public ObservableCollection<string> Rows
    {
        get;
    } = [];
    public string StoragePath
    {
        get;
    } = storagePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChunkMaestro", "Fleet");
    public bool LocalStorageConfirmed
    {
        get => confirmed; set
        {
            confirmed = value;
            Changed();
            Changed(nameof(CanOpen));
        }

    }

    public bool CanOpen => confirmed && inventory is null && !busy && !closing;
    public bool CanRegister => inventory is not null && !busy && !closing && !HasReadFailure && !writeNeedsReload;
    public bool CanReload => inventory is not null && !busy && !closing;
    public bool CanOperate
    {
        get;
    }

    public bool IsEmpty => Rows.Count == 0;
    public bool HasReadFailure
    {
        get; private set;
    }

    public string Status
    {
        get => status; private set
        {
            status = value;
            Changed();
        }

    }

    public string Error
    {
        get => error; private set
        {
            error = value;
            Changed();
        }

    }

    public string FleetName
    {
        get; set;
    } = "";
    public string HostName
    {
        get; set;
    } = "";
    public string HostAddress
    {
        get; set;
    } = "";
    public string InstanceName
    {
        get; set;
    } = "";
    public string ServiceName
    {
        get; set;
    } = "minecraft.service";
    public string JavaPort
    {
        get; set;
    } = "25565";
    public string BedrockPort
    {
        get; set;
    } = "";
    public Fleet? SelectedFleet
    {
        get; set;
    }

    public Host? SelectedHost
    {
        get; set;
    }


    public Task OpenAsync() => RunAsync(async () =>
    {
        if (!confirmed || inventory is not null)
        {
            return;
        }

        inventory = await open(confirmed);
        if (!closing)
        {
            await ReadAsync();
        }
    });
    public Task ReloadAsync() => RunAsync(ReadAsync);
    public Task RegisterFleetAsync() => RegisterAsync(store => store.RegisterFleetAsync(new FleetId(Guid.NewGuid()), FleetName));
    public Task RegisterHostAsync() => RegisterAsync(store => store.RegisterHostAsync(new HostId(Guid.NewGuid()),
        SelectedFleet?.Id ?? throw Invalid(), HostName, HostAddress));
    public Task RegisterInstanceAsync() => RegisterAsync(store =>
    {
        Host host = SelectedHost ?? throw Invalid();
        List<Endpoint> endpoints = [];
        AddEndpoint(endpoints, EndpointEdition.Java, JavaPort, host.Address);
        AddEndpoint(endpoints, EndpointEdition.Bedrock, BedrockPort, host.Address);
        if (endpoints.Count == 0)
        {
            throw Invalid();
        }

        return store.RegisterInstanceAsync(new InstanceId(Guid.NewGuid()), host.Id, InstanceName, ServiceName, endpoints);
    });
    private Task RegisterAsync(Func<IFleetInventory, Task> write)
    {
        if (!CanRegister)
        {
            return Task.CompletedTask;
        }

        return RunAsync(async () =>
{
    try
    {
        await write(inventory!);
    }
    catch (Exception exception) when (exception is not FleetStorageException storageException || storageException.Failure != StorageFailure.InvalidRegistration)
    {
        writeNeedsReload = true;
        Status = "Cached metadata may be stale. Local registration outcome is unknown; previous display retained. This is not a host contact.";
        Error = "Local registration outcome is unknown. Reload successfully to check saved metadata before another registration. Do not repeat the previous registration without checking. Preserve existing storage for recovery.";
        return;
    }

    if (!closing)
    {
        await ReadAsync();
    }
});
    }
    private static void AddEndpoint(List<Endpoint> endpoints, EndpointEdition edition, string port, string address)
    {
        if (string.IsNullOrWhiteSpace(port))
        {
            return;
        }

        if (!int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number is < 1 or > 65535)
        {
            throw Invalid();
        }

        endpoints.Add(new Endpoint(edition, address, number));
    }
    private async Task ReadAsync()
    {
        if (inventory is null)
        {
            return;
        }

        try
        {
            FleetInventory snapshot = await inventory.ReadAsync();
            if (closing)
            {
                return;
            }
            Fleets.Clear();
            Hosts.Clear();
            Rows.Clear();
            foreach (Fleet fleet in snapshot.Fleets)
            {
                Fleets.Add(fleet);
                Rows.Add($"Fleet · {fleet.Name}");
            }

            foreach (Host host in snapshot.Hosts)
            {
                Hosts.Add(host);
                Rows.Add($"Host · {host.Name} · {host.Address} · Not enrolled / unknown · Last contact: unavailable");
            }
            foreach (Instance instance in snapshot.Instances)
            {
                Rows.Add($"Instance · {instance.Name} · {instance.ServiceName} · Not enrolled / unknown");
                foreach (Endpoint endpoint in instance.Endpoints)
                {
                    Rows.Add($"  {endpoint.Edition} {endpoint.Transport.ToString().ToUpperInvariant()} · {endpoint.Address}:{endpoint.Port} · Health: unknown (not probed)");
                }
            }
            HasReadFailure = false;
            writeNeedsReload = false;
            Status = $"Local cached metadata · Read {snapshot.RetrievedAt:O} · This is not a host contact.";
            Changed(nameof(IsEmpty));
        }
        catch
        {
            HasReadFailure = true;
            Status = "Cached metadata may be stale. Local reload failed; previous display retained. Remote state is unknown.";
            throw;
        }
    }
    private async Task RunAsync(Func<Task> action)
    {
        if (busy || closing)
        {
            return;
        }

        busy = true;
        idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        NotifyGates();
        Error = "";
        try
        {
            await action();
        }

        catch (FleetStorageException exception)
        {
            Error = exception.Failure switch
            {
                StorageFailure.InvalidRegistration => "Check names, selected parents, addresses and ports. The registration may conflict with existing metadata.",
                StorageFailure.ControllerInUse => "Another desktop session holds this store. Close it before retrying.",
                StorageFailure.InvalidLocation => "Storage must be local, nonsynchronized and restricted to your OS user. Check directory permissions and links.",
                _ => "Storage could not be read or initialized. Preserve the database, sidecars and recovery backups; use reviewed recovery with a compatible application."
            };
        }
        catch (Exception)
        {
            Error = "Local action failed. Reload to check saved metadata before trying again. Preserve existing storage for recovery.";
        }

        finally
        {
            busy = false;
            idle.SetResult();
            NotifyGates();
        }

    }
    private static FleetStorageException Invalid() => new(StorageFailure.InvalidRegistration, "Invalid metadata.");
    private void NotifyGates()
    {
        Changed(nameof(CanOpen));
        Changed(nameof(CanRegister));
        Changed(nameof(CanReload));
    }

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    public ValueTask DisposeAsync()
    {
        return new ValueTask(disposal ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        closing = true;
        NotifyGates();
        if (busy && idle is not null)
        {
            await idle.Task;
        }

        IFleetInventory? acquired = inventory;
        inventory = null;
        if (acquired is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync();
        }
    }
}
