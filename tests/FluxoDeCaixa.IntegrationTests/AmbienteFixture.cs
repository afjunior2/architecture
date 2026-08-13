using Dapper;
using FluxoDeCaixa.Consolidado.Api;
using FluxoDeCaixa.Consolidado.Worker;
using FluxoDeCaixa.Lancamentos.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace FluxoDeCaixa.IntegrationTests;

/// <summary>
/// Um Postgres e um RabbitMQ reais, compartilhados pela suíte. Testcontainers em vez de
/// mocks de banco: transação, unique constraint, SKIP LOCKED e jsonb não existem em dublê.
/// Os testes escopam asserções por merchant, então compartilham o banco sem interferência.
/// </summary>
public sealed class AmbienteFixture : IAsyncLifetime
{
    public PostgreSqlContainer Postgres { get; } = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("fluxodecaixa")
        .Build();

    // Porta do host FIXA: o teste de indisponibilidade para e religa o container, e o
    // Docker reatribuiria uma porta aleatória no restart, deixando a API configurada
    // com um endereço morto. Porta fixa sobrevive ao ciclo stop/start.
    private const int PortaFixaRabbit = 35672;

    public RabbitMqContainer Rabbit { get; } = new RabbitMqBuilder("rabbitmq:3.13-alpine")
        .WithUsername("fluxo")
        .WithPassword("fluxo")
        .WithPortBinding(PortaFixaRabbit, 5672)
        .Build();

    public string ConnString => Postgres.GetConnectionString();
    public string RabbitHost => Rabbit.Hostname;
    public int RabbitPort => PortaFixaRabbit;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(Postgres.StartAsync(), Rabbit.StartAsync());
    }

    public async Task DisposeAsync()
    {
        await Postgres.DisposeAsync();
        await Rabbit.DisposeAsync();
    }

    public WebApplicationFactory<MarcadorDaApiDeLancamentos> CriarApiDeLancamentos(bool outboxHabilitada = true) =>
        new WebApplicationFactory<MarcadorDaApiDeLancamentos>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Postgres", ConnString);
            b.UseSetting("RabbitMq:Host", RabbitHost);
            b.UseSetting("RabbitMq:Porta", RabbitPort.ToString());
            b.UseSetting("RabbitMq:Usuario", "fluxo");
            b.UseSetting("RabbitMq:Senha", "fluxo");
            b.UseSetting("Outbox:Desabilitado", outboxHabilitada ? "false" : "true");
        });

    public WebApplicationFactory<MarcadorDaApiDeConsolidado> CriarApiDeConsolidado() =>
        new WebApplicationFactory<MarcadorDaApiDeConsolidado>().WithWebHostBuilder(b =>
            b.UseSetting("ConnectionStrings:Postgres", ConnString));

    public WebApplicationFactory<MarcadorDoWorker> CriarWorker() =>
        new WebApplicationFactory<MarcadorDoWorker>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Postgres", ConnString);
            b.UseSetting("RabbitMq:Host", RabbitHost);
            b.UseSetting("RabbitMq:Porta", RabbitPort.ToString());
            b.UseSetting("RabbitMq:Usuario", "fluxo");
            b.UseSetting("RabbitMq:Senha", "fluxo");
        });

    public async Task<T> ConsultarAsync<T>(string sql, object? parametros = null)
    {
        await using var conn = new NpgsqlConnection(ConnString);
        return await conn.ExecuteScalarAsync<T>(sql, parametros) ?? default!;
    }

    public static async Task AguardarAsync(Func<Task<bool>> condicao, TimeSpan? limite = null, string? motivo = null)
    {
        var deadline = DateTimeOffset.UtcNow + (limite ?? TimeSpan.FromSeconds(60));
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condicao()) return;
            await Task.Delay(250);
        }
        throw new TimeoutException(motivo ?? "Condição não satisfeita dentro do limite.");
    }
}

[CollectionDefinition("ambiente")]
public sealed class AmbienteCollection : ICollectionFixture<AmbienteFixture>;
