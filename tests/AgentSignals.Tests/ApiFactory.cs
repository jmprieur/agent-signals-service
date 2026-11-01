using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using AgentSignals.Api.Kusto;
using AgentSignals.Api.Protocol;
using AgentSignals.Api.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AgentSignals.Tests;

internal sealed class FakeKusto : IKustoGateway
{
    public ConcurrentQueue<string> Commands { get; } = new();
    public ConcurrentQueue<(string Query, IReadOnlyDictionary<string, string> Parameters)> Queries { get; } = new();
    public IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows { get; set; } = [];

    public Task ExecuteCommandAsync(string command, CancellationToken cancellationToken)
    {
        Commands.Enqueue(command);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(
        string query, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        Queries.Enqueue((query, parameters));
        return Task.FromResult(Rows);
    }
}

internal sealed class FakeArchive : ISignalArchive
{
    public ConcurrentDictionary<string, string> Blobs { get; } = new();

    public Task<ArchiveResult> StoreAsync(SignalRecord record, CancellationToken cancellationToken) =>
        Task.FromResult(Blobs.TryAdd(record.BlobName, record.ArchivedJson) ? ArchiveResult.Stored : ArchiveResult.AlreadyStored);

    public async IAsyncEnumerable<ArchivedSignal> ReadReceivedSinceAsync(DateTimeOffset since, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield break;
    }
}

/// <summary>TestServer has no remote address; local-development auth only trusts loopback callers.</summary>
internal sealed class LoopbackStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, nextMiddleware) =>
        {
            context.Connection.RemoteIpAddress ??= IPAddress.Loopback;
            return nextMiddleware(context);
        });
        next(app);
    };
}

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    internal FakeKusto Kusto { get; } = new();
    internal FakeArchive Archive { get; } = new();
    public string Environment { get; init; } = "Development";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environment);
        builder.UseSetting("Auth:Mode", "LocalDevelopment");
        builder.UseSetting("ConnectionStrings:signals", "Endpoint=http://127.0.0.1:10000/devstoreaccount1;ContainerName=signals");
        builder.UseSetting("ConnectionStrings:signalsdb", "Data Source=http://127.0.0.1:1;Initial Catalog=signals");
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IKustoGateway>(Kusto);
            services.AddSingleton<ISignalArchive>(Archive);
            services.AddSingleton<IStartupFilter, LoopbackStartupFilter>();
        });
    }
}
