using System.Data;
using Dapper;

namespace FluxoDeCaixa.Consolidado.Infrastructure.Persistencia;

/// <summary>
/// Dapper não mapeia DateOnly nativamente (NotSupportedException ao usá-lo como
/// parâmetro). Registrar() precisa ser chamado uma vez no startup de cada serviço
/// (Api e Worker) antes do primeiro uso de Dapper.
/// </summary>
public sealed class DateOnlyTypeHandler : SqlMapper.TypeHandler<DateOnly>
{
    public override void SetValue(IDbDataParameter parameter, DateOnly value)
    {
        parameter.DbType = DbType.Date;
        parameter.Value = value.ToDateTime(TimeOnly.MinValue);
    }

    public override DateOnly Parse(object value) => DateOnly.FromDateTime((DateTime)value);

    public static void Registrar() => SqlMapper.AddTypeHandler(new DateOnlyTypeHandler());
}
