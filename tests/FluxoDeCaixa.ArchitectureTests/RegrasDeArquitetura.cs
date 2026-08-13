using System.Reflection;
using FluentAssertions;
using FluxoDeCaixa.Lancamentos.Domain;
using NetArchTest.Rules;
using Xunit;

namespace FluxoDeCaixa.ArchitectureTests;

/// <summary>
/// Poucas regras, todas ligadas a uma decisão registrada em ADR. Uma regra de
/// arquitetura que não protege decisão nenhuma é ruído no build.
/// </summary>
public class RegrasDeArquitetura
{
    private static readonly Assembly Dominio = typeof(Lancamento).Assembly;
    private static readonly Assembly Aplicacao = typeof(Lancamentos.Application.RegistrarLancamentoHandler).Assembly;
    private static readonly Assembly InfraLancamentos = typeof(Lancamentos.Infrastructure.Persistencia.LancamentosDbContext).Assembly;
    private static readonly Assembly InfraConsolidado = typeof(Consolidado.Infrastructure.Persistencia.ProjecaoDeConsolidado).Assembly;

    [Fact]
    public void Dominio_nao_depende_de_infraestrutura_nem_de_framework()
    {
        // Protege a testabilidade do núcleo: regra de negócio roda sem banco, broker ou host.
        var resultado = Types.InAssembly(Dominio)
            .ShouldNot()
            .HaveDependencyOnAny(
                "Microsoft.EntityFrameworkCore",
                "Microsoft.AspNetCore",
                "Npgsql",
                "RabbitMQ",
                "Dapper",
                "FluxoDeCaixa.Lancamentos.Infrastructure",
                "FluxoDeCaixa.Lancamentos.Application")
            .GetResult();

        resultado.IsSuccessful.Should().BeTrue(
            "o domínio deve depender só da BCL. Violações: {0}",
            string.Join(", ", resultado.FailingTypeNames ?? Enumerable.Empty<string>()));
    }

    [Fact]
    public void Aplicacao_nao_depende_de_adapters_externos()
    {
        var resultado = Types.InAssembly(Aplicacao)
            .ShouldNot()
            .HaveDependencyOnAny(
                "Microsoft.EntityFrameworkCore",
                "Microsoft.AspNetCore",
                "Npgsql",
                "RabbitMQ",
                "FluxoDeCaixa.Lancamentos.Infrastructure")
            .GetResult();

        resultado.IsSuccessful.Should().BeTrue(
            "a aplicação conhece portas, não adapters. Violações: {0}",
            string.Join(", ", resultado.FailingTypeNames ?? Enumerable.Empty<string>()));
    }

    [Fact]
    public void Lancamentos_e_Consolidado_nao_se_referenciam()
    {
        // É o requisito central do desafio expresso em compilação: os dois domínios de
        // falha compartilham apenas o contrato do evento (FluxoDeCaixa.Contracts).
        Types.InAssembly(InfraLancamentos)
            .ShouldNot().HaveDependencyOnAny("FluxoDeCaixa.Consolidado")
            .GetResult().IsSuccessful.Should().BeTrue("Lançamentos não pode conhecer Consolidado");

        Types.InAssembly(InfraConsolidado)
            .ShouldNot().HaveDependencyOnAny("FluxoDeCaixa.Lancamentos")
            .GetResult().IsSuccessful.Should().BeTrue("Consolidado não pode conhecer Lançamentos");
    }

    [Fact]
    public void Entidades_de_dominio_nao_expoem_setters_publicos()
    {
        var tipos = Types.InAssembly(Dominio).GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && t.Namespace?.StartsWith("FluxoDeCaixa") == true)
            .ToList();

        tipos.Should().NotBeEmpty("se a varredura vier vazia, o teste passa sem verificar nada");

        var violacoes = tipos
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(p => p.SetMethod is { IsPublic: true } s
                        && !s.ReturnParameter.GetRequiredCustomModifiers()
                            .Any(m => m.Name == "IsExternalInit"))
            .Select(p => $"{p.DeclaringType!.Name}.{p.Name}")
            .ToList();

        violacoes.Should().BeEmpty("lançamento é imutável depois de criado; correção entra por compensação");
    }
}
